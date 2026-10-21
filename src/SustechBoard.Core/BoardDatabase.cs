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
                    manual_archived INTEGER NOT NULL DEFAULT 0,
                    origin       TEXT NOT NULL DEFAULT 'announcement',
                    source_menu  TEXT NOT NULL DEFAULT '',
                    content_kind TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE IF NOT EXISTS analyses (
                    ann_id       TEXT PRIMARY KEY,
                    content_hash TEXT NOT NULL,
                    json         TEXT NOT NULL,
                    analyzed_at  TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_ann_published ON announcements(published_at);

                CREATE TABLE IF NOT EXISTS course_paths (
                    course_id     TEXT NOT NULL,
                    path_key      TEXT NOT NULL,
                    label         TEXT NOT NULL,
                    group_name    TEXT NOT NULL DEFAULT '',
                    url           TEXT NOT NULL,
                    selected      INTEGER NOT NULL DEFAULT 0,
                    item_count    INTEGER NOT NULL DEFAULT 0,
                    discovered_at TEXT NOT NULL,
                    last_ok_at    TEXT,
                    fail_count    INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (course_id, path_key)
                );

                CREATE TABLE IF NOT EXISTS courses (
                    id            TEXT PRIMARY KEY,
                    name          TEXT NOT NULL,
                    menu_hash     TEXT NOT NULL DEFAULT '',
                    discovered_at TEXT,
                    enabled       INTEGER NOT NULL DEFAULT 1
                );

                CREATE TABLE IF NOT EXISTS item_links (
                    course_id    TEXT NOT NULL,
                    ann_id       TEXT NOT NULL,
                    canonical_id TEXT NOT NULL,
                    computed_at  TEXT NOT NULL,
                    PRIMARY KEY (course_id, ann_id)
                );

                CREATE TABLE IF NOT EXISTS meta (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();

            // Migrate databases created before manual archiving existed.
            EnsureColumn("announcements", "manual_archived", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("announcements", "origin", "TEXT NOT NULL DEFAULT 'announcement'");
            EnsureColumn("announcements", "source_menu", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn("announcements", "content_kind", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn("courses", "dedup_at", "TEXT");
            EnsureColumn("courses", "enabled", "INTEGER NOT NULL DEFAULT 1");
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

    /// <summary>
    /// Deletes announcements of ONE course that this fetch did not return.
    /// Scoped per course on purpose: if the course list itself comes back partial (a flaky
    /// AJAX response), a global diff would delete whole courses that simply weren't fetched.
    /// </summary>
    public int DeleteNotInCourse(string courseId, IReadOnlyCollection<string> keepIds)
    {
        if (keepIds.Count == 0) return 0;

        lock (_gate)
        {
            var placeholders = string.Join(",", keepIds.Select((_, i) => "$p" + i));
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                $"DELETE FROM announcements WHERE course_id = $cid AND id NOT IN ({placeholders})";
            cmd.Parameters.AddWithValue("$cid", courseId);
            var i = 0;
            foreach (var id in keepIds) cmd.Parameters.AddWithValue("$p" + i++, id);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Removes analyses whose announcement no longer exists.</summary>
    public int PruneOrphanAnalyses()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "DELETE FROM analyses WHERE ann_id NOT IN (SELECT id FROM announcements)";
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Drops every scraped row — used when the signed-in account changes.</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM analyses; DELETE FROM announcements; " +
                              "DELETE FROM course_paths; DELETE FROM courses; " +
                              "DELETE FROM item_links;";
            cmd.ExecuteNonQuery();
        }
        Log.Info("已清空本地公告、分析缓存与课程路径");
    }

    // ---------- course paths & discovery bookkeeping ----------

    public void SaveCoursePaths(string courseId, IEnumerable<CoursePath> paths)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();

            using (var del = _conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM course_paths WHERE course_id = $cid";
                del.Parameters.AddWithValue("$cid", courseId);
                del.ExecuteNonQuery();
            }

            foreach (var p in paths)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO course_paths
                        (course_id, path_key, label, group_name, url, selected,
                         item_count, discovered_at, last_ok_at, fail_count)
                    VALUES ($cid, $key, $label, $group, $url, $sel, $cnt, $da, $ok, $fail)
                    """;
                cmd.Parameters.AddWithValue("$cid", p.CourseId);
                cmd.Parameters.AddWithValue("$key", p.PathKey);
                cmd.Parameters.AddWithValue("$label", p.Label);
                cmd.Parameters.AddWithValue("$group", p.Group);
                cmd.Parameters.AddWithValue("$url", p.Url);
                cmd.Parameters.AddWithValue("$sel", p.Selected ? 1 : 0);
                cmd.Parameters.AddWithValue("$cnt", p.ItemCount);
                cmd.Parameters.AddWithValue("$da", p.DiscoveredAt.ToString(IsoFormat, CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$ok", p.LastOkAt is { } okv
                    ? okv.ToString(IsoFormat, CultureInfo.InvariantCulture) : DBNull.Value);
                cmd.Parameters.AddWithValue("$fail", p.FailCount);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public List<CoursePath> LoadCoursePaths()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT course_id, path_key, label, group_name, url, selected,
                       item_count, discovered_at, last_ok_at, fail_count
                FROM course_paths
                """;
            using var r = cmd.ExecuteReader();
            var list = new List<CoursePath>();
            while (r.Read())
            {
                list.Add(new CoursePath
                {
                    CourseId = r.GetString(0),
                    PathKey = r.GetString(1),
                    Label = r.GetString(2),
                    Group = r.GetString(3),
                    Url = r.GetString(4),
                    Selected = r.GetInt32(5) != 0,
                    ItemCount = r.GetInt32(6),
                    DiscoveredAt = ParseIso(r.GetString(7)) ?? DateTimeOffset.MinValue,
                    LastOkAt = r.IsDBNull(8) ? null : ParseIso(r.GetString(8)),
                    FailCount = r.GetInt32(9)
                });
            }
            return list;
        }
    }

    public void SetPathSelected(string courseId, string pathKey, bool selected)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "UPDATE course_paths SET selected = $v WHERE course_id = $cid AND path_key = $key";
            cmd.Parameters.AddWithValue("$v", selected ? 1 : 0);
            cmd.Parameters.AddWithValue("$cid", courseId);
            cmd.Parameters.AddWithValue("$key", pathKey);
            cmd.ExecuteNonQuery();
        }
    }

    public void RecordPathResult(string courseId, string pathKey, bool ok)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = ok
                ? "UPDATE course_paths SET last_ok_at = $now, fail_count = 0 " +
                  "WHERE course_id = $cid AND path_key = $key"
                : "UPDATE course_paths SET fail_count = fail_count + 1 " +
                  "WHERE course_id = $cid AND path_key = $key";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString(IsoFormat, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$cid", courseId);
            cmd.Parameters.AddWithValue("$key", pathKey);
            cmd.ExecuteNonQuery();
        }
    }

    public List<CourseRecord> LoadCourseRecords()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT id, name, menu_hash, discovered_at, dedup_at, enabled FROM courses";
            using var r = cmd.ExecuteReader();
            var list = new List<CourseRecord>();
            while (r.Read())
            {
                list.Add(new CourseRecord
                {
                    Id = r.GetString(0),
                    Name = r.GetString(1),
                    MenuHash = r.GetString(2),
                    DiscoveredAt = r.IsDBNull(3) ? null : ParseIso(r.GetString(3)),
                    DedupAt = r.IsDBNull(4) ? null : ParseIso(r.GetString(4)),
                    Enabled = r.IsDBNull(5) || r.GetInt32(5) != 0
                });
            }
            return list;
        }
    }

    public void UpsertCourseRecord(CourseRecord rec)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO courses (id, name, menu_hash, discovered_at)
                VALUES ($id, $name, $hash, $da)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    menu_hash = excluded.menu_hash,
                    discovered_at = excluded.discovered_at
                """;
            cmd.Parameters.AddWithValue("$id", rec.Id);
            cmd.Parameters.AddWithValue("$name", rec.Name);
            cmd.Parameters.AddWithValue("$hash", rec.MenuHash ?? "");
            cmd.Parameters.AddWithValue("$da", rec.DiscoveredAt is { } d
                ? d.ToString(IsoFormat, CultureInfo.InvariantCulture) : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Inserts a course the first time it is seen (enabled by default) and keeps its name fresh.</summary>
    public void EnsureCourse(string id, string name)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO courses (id, name) VALUES ($id, $name)
                ON CONFLICT(id) DO UPDATE SET name = excluded.name
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetCourseEnabled(string courseId, bool enabled)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE courses SET enabled = $v WHERE id = $cid";
            cmd.Parameters.AddWithValue("$v", enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$cid", courseId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Deletes everything scraped for one course — used when the user unticks it.</summary>
    public int DeleteCourseData(string courseId)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            var rows = 0;

            foreach (var sql in new[]
                     {
                         "DELETE FROM announcements WHERE course_id = $cid",
                         "DELETE FROM course_paths  WHERE course_id = $cid",
                         "DELETE FROM item_links    WHERE course_id = $cid"
                     })
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$cid", courseId);
                rows += cmd.ExecuteNonQuery();
            }

            using (var prune = _conn.CreateCommand())
            {
                prune.CommandText =
                    "DELETE FROM analyses WHERE ann_id NOT IN (SELECT id FROM announcements)";
                prune.ExecuteNonQuery();
            }

            tx.Commit();
            return rows;
        }
    }

    /// <summary>
    /// Removes course records (and their scraped data) that the current account no longer lists.
    /// This is what cleans up another account's courses after a switch.
    /// </summary>
    public int PruneCoursesExcept(IReadOnlyCollection<string> keepIds)
    {
        if (keepIds.Count == 0) return 0;

        var keep = keepIds as ICollection<string> ?? keepIds.ToList();
        var keepSet = new HashSet<string>(keep, StringComparer.Ordinal);

        lock (_gate)
        {
            var victims = new List<string>();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id FROM courses";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var id = r.GetString(0);
                    if (!keepSet.Contains(id)) victims.Add(id);
                }
            }

            foreach (var id in victims)
            {
                DeleteCourseData(id);

                using var del = _conn.CreateCommand();
                del.CommandText = "DELETE FROM courses WHERE id = $id";
                del.Parameters.AddWithValue("$id", id);
                del.ExecuteNonQuery();
            }

            return victims.Count;
        }
    }

    // ---------- duplicate links (same assignment seen in two places) ----------
    /// <summary>Replaces the duplicate map for one course. Drops are hidden from the UI.</summary>
    public void ReplaceLinks(string courseId, IEnumerable<(string AnnId, string CanonicalId)> links)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();

            using (var del = _conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM item_links WHERE course_id = $cid";
                del.Parameters.AddWithValue("$cid", courseId);
                del.ExecuteNonQuery();
            }

            var stamp = DateTimeOffset.Now.ToString(IsoFormat, CultureInfo.InvariantCulture);
            foreach (var (annId, canonicalId) in links)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO item_links (course_id, ann_id, canonical_id, computed_at)
                    VALUES ($cid, $aid, $kid, $at)
                    ON CONFLICT(course_id, ann_id) DO UPDATE SET
                        canonical_id = excluded.canonical_id,
                        computed_at  = excluded.computed_at
                    """;
                cmd.Parameters.AddWithValue("$cid", courseId);
                cmd.Parameters.AddWithValue("$aid", annId);
                cmd.Parameters.AddWithValue("$kid", canonicalId);
                cmd.Parameters.AddWithValue("$at", stamp);
                cmd.ExecuteNonQuery();
            }

            // Record that dedup ran for this course even when nothing was merged.
            using (var mark = _conn.CreateCommand())
            {
                mark.CommandText = "UPDATE courses SET dedup_at = $at WHERE id = $cid";
                mark.Parameters.AddWithValue("$at", stamp);
                mark.Parameters.AddWithValue("$cid", courseId);
                mark.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>Announcement ids that are duplicates of a canonical row.</summary>
    public HashSet<string> LoadDuplicateIds()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT ann_id FROM item_links";
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.Ordinal);
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }
    }

    /// <summary>Forgets the duplicate map (used when the account or course set changes).</summary>
    public void ClearLinks()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM item_links;";
            cmd.ExecuteNonQuery();
        }
    }

    // ---------- small key/value store ----------

    public string? GetMeta(string key)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string value)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO meta (key, value) VALUES ($k, $v)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
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
                       author, content_hash, first_seen, last_changed, manual_archived,
                       origin, source_menu, content_kind
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
                    IsManuallyArchived = !r.IsDBNull(10) && r.GetInt32(10) != 0,
                    Origin = r.IsDBNull(11) ? "announcement" : r.GetString(11),
                    SourceMenu = r.IsDBNull(12) ? "" : r.GetString(12),
                    ContentKind = r.IsDBNull(13) ? "" : r.GetString(13)
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
                 content_hash, first_seen, last_changed, manual_archived,
                 origin, source_menu, content_kind)
            VALUES ($id, $cid, $cname, $title, $body, $pub, $author, $hash, $fs, $lc, 0,
                    $origin, $menu, $kind)
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
                first_seen = $fs, last_changed = $lc,
                origin = $origin, source_menu = $menu, content_kind = $kind
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
        cmd.Parameters.AddWithValue("$origin", string.IsNullOrEmpty(a.Origin) ? "announcement" : a.Origin);
        cmd.Parameters.AddWithValue("$menu", a.SourceMenu ?? "");
        cmd.Parameters.AddWithValue("$kind", a.ContentKind ?? "");
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
