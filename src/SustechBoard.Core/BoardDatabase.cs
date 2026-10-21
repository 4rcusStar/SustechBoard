using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SustechBoard.Core;

/// <summary>SQLite persistence: announcements, their analyses, and change detection.</summary>
public sealed class BoardDatabase : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    private const string IsoFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";

    public BoardDatabase(string? path = null)
    {
        var file = path ?? AppPaths.DatabaseFile;
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={file}");
        _conn.Open();
        Initialize();
    }

    private void Initialize()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS announcements (
                    id           TEXT PRIMARY KEY,
                    course_id    TEXT NOT NULL,
                    course_name  TEXT NOT NULL,
                    title        TEXT NOT NULL,
                    body         TEXT NOT NULL,
                    published_at TEXT,
                    author       TEXT NOT NULL DEFAULT '',
                    content_hash TEXT NOT NULL,
                    first_seen   TEXT NOT NULL,
                    last_changed TEXT NOT NULL,
                    manual_archived INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS analyses (
                    ann_id       TEXT PRIMARY KEY,
                    content_hash TEXT NOT NULL,
                    json         TEXT NOT NULL,
                    analyzed_at  TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_ann_published ON announcements(published_at);
                """;
            cmd.ExecuteNonQuery();

            // Migrate databases created before manual archiving existed.
            EnsureColumn("announcements", "manual_archived", "INTEGER NOT NULL DEFAULT 0");
        }
    }

    private void EnsureColumn(string table, string column, string definition)
    {
        bool exists = false;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }
        if (exists) return;

        using var alter = _conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
        Log.Info($"数据库迁移: {table} 新增列 {column}");
    }

    /// <summary>Sets or clears the user's manual-archive flag. Returns false when the row is missing.</summary>
    public bool SetManualArchived(string annId, bool archived)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE announcements SET manual_archived = $v WHERE id = $id";
            cmd.Parameters.AddWithValue("$v", archived ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", annId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Inserts new rows and updates changed ones. Returns only new/changed items (with IsNew=true).</summary>
    public List<Announcement> Upsert(IEnumerable<Announcement> incoming, DateTimeOffset now)
    {
        var changed = new List<Announcement>();
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var a in incoming)
            {
                var (existingHash, existingFirstSeen) = QueryExisting(a.Id);

                if (existingHash is null)
                {
                    a.FirstSeen = now;
                    a.LastChanged = now;
                    a.IsNew = true;
                    Insert(a);
                    changed.Add(a);
                }
                else if (!string.Equals(existingHash, a.ContentHash, StringComparison.Ordinal))
                {
                    a.FirstSeen = existingFirstSeen ?? now;
                    a.LastChanged = now;
                    a.IsNew = true;
                    Update(a);
                    changed.Add(a);
                }
                else
                {
                    Touch(a.Id, now);
                }
            }
            tx.Commit();
        }
        return changed;
    }

    public List<Announcement> LoadAll()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, course_id, course_name, title, body, published_at,
                       author, content_hash, first_seen, last_changed, manual_archived
                FROM announcements
                ORDER BY published_at DESC
                """;
            using var r = cmd.ExecuteReader();
            var list = new List<Announcement>();
            while (r.Read())
            {
                list.Add(new Announcement
                {
                    Id = r.GetString(0),
                    CourseId = r.GetString(1),
                    CourseName = r.GetString(2),
                    Title = r.GetString(3),
                    Body = r.GetString(4),
                    PublishedAt = r.IsDBNull(5) ? null : ParseIso(r.GetString(5)),
                    Author = r.IsDBNull(6) ? "" : r.GetString(6),
                    ContentHash = r.GetString(7),
                    FirstSeen = ParseIso(r.GetString(8)) ?? DateTimeOffset.MinValue,
                    LastChanged = ParseIso(r.GetString(9)) ?? DateTimeOffset.MinValue,
                    IsManuallyArchived = !r.IsDBNull(10) && r.GetInt32(10) != 0
                });
            }
            return list;
        }
    }

    public Dictionary<string, AnnouncementAnalysis> LoadAnalyses()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT ann_id, json FROM analyses";
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<string, AnnouncementAnalysis>();
            while (r.Read())
            {
                try
                {
                    var a = AnalysisJson.Deserialize(r.GetString(1));
                    if (a is not null) map[r.GetString(0)] = a;
                }
                catch (Exception ex)
                {
                    Log.Warn($"分析缓存损坏: {ex.Message}");
                }
            }
            return map;
        }
    }

    public AnnouncementAnalysis? GetAnalysis(string annId, string contentHash)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT content_hash, json FROM analyses WHERE ann_id = $id";
            cmd.Parameters.AddWithValue("$id", annId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            if (!string.Equals(r.GetString(0), contentHash, StringComparison.Ordinal)) return null;
            return AnalysisJson.Deserialize(r.GetString(1));
        }
    }

    public void SaveAnalysis(string annId, string contentHash, AnnouncementAnalysis analysis)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO analyses (ann_id, content_hash, json, analyzed_at)
                VALUES ($id, $hash, $json, $at)
                ON CONFLICT(ann_id) DO UPDATE SET
                    content_hash = excluded.content_hash,
                    json         = excluded.json,
                    analyzed_at  = excluded.analyzed_at
                """;
            cmd.Parameters.AddWithValue("$id", annId);
            cmd.Parameters.AddWithValue("$hash", contentHash);
            cmd.Parameters.AddWithValue("$json", AnalysisJson.Serialize(analysis));
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString(IsoFormat, CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }

    private (string? Hash, DateTimeOffset? FirstSeen) QueryExisting(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT content_hash, first_seen FROM announcements WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (null, null);
        return (r.GetString(0), ParseIso(r.GetString(1)));
    }

    private void Insert(Announcement a)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO announcements
                (id, course_id, course_name, title, body, published_at, author,
                 content_hash, first_seen, last_changed, manual_archived)
            VALUES ($id, $cid, $cname, $title, $body, $pub, $author, $hash, $fs, $lc, 0)
            """;
        AddCoreParams(cmd, a);
        cmd.Parameters.AddWithValue("$fs", a.FirstSeen.ToString(IsoFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$lc", a.LastChanged.ToString(IsoFormat, CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private void Update(Announcement a)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE announcements SET
                course_id = $cid, course_name = $cname, title = $title, body = $body,
                published_at = $pub, author = $author, content_hash = $hash,
                first_seen = $fs, last_changed = $lc
            WHERE id = $id
            """;
        AddCoreParams(cmd, a);
        cmd.Parameters.AddWithValue("$fs", a.FirstSeen.ToString(IsoFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$lc", a.LastChanged.ToString(IsoFormat, CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private static void AddCoreParams(SqliteCommand cmd, Announcement a)
    {
        cmd.Parameters.AddWithValue("$id", a.Id);
        cmd.Parameters.AddWithValue("$cid", a.CourseId);
        cmd.Parameters.AddWithValue("$cname", a.CourseName);
        cmd.Parameters.AddWithValue("$title", a.Title);
        cmd.Parameters.AddWithValue("$body", a.Body);
        cmd.Parameters.AddWithValue("$pub", a.PublishedAt is { } p
            ? p.ToString(IsoFormat, CultureInfo.InvariantCulture)
            : DBNull.Value);
        cmd.Parameters.AddWithValue("$author", a.Author ?? "");
        cmd.Parameters.AddWithValue("$hash", a.ContentHash);
    }

    private void Touch(string id, DateTimeOffset now)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE announcements SET last_changed = $lc WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$lc", now.ToString(IsoFormat, CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private static DateTimeOffset? ParseIso(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var v) ? v : null;
    }

    public void Dispose()
    {
        lock (_gate) { _conn.Dispose(); }
    }
}
