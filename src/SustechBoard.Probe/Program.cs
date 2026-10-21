using System.Text;
using System.Text.Json;
using SustechBoard.Core;

Console.OutputEncoding = Encoding.UTF8;

// Dev helper: inspect how the current database ranks without touching the network.
if (args.Length >= 1 && args[0] == "--dump")
{
    var dumpSettings = SettingsStore.Load();
    using var dumpDb = new BoardDatabase();
    var dumpAll = dumpDb.LoadAll();
    var dumpAnalyses = dumpDb.LoadAnalyses();
    var dumpItems = Prioritizer.BuildAll(dumpAll, dumpAnalyses, dumpSettings, DateTimeOffset.Now);

    Console.WriteLine($"公告 {dumpAll.Count} 条 / 分析缓存 {dumpAnalyses.Count} 条");
    Console.WriteLine("分布: " + string.Join("  ", dumpItems.GroupBy(i => i.Priority)
        .OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}")));
    Console.WriteLine($"归档拆分: 手动归档={dumpItems.Count(i => i.ArchiveKind == ArchiveKind.Manual)}  " +
                      $"过期归档={dumpItems.Count(i => i.ArchiveKind == ArchiveKind.Expired)}");
    Console.WriteLine(new string('-', 84));
    foreach (var i in dumpItems.Where(x => x.Priority <= Priority.P2)
                           .OrderBy(x => x.Priority).ThenBy(x => x.DaysLeft ?? int.MaxValue))
    {
        var dl = i.Analysis.Deadline is { } d ? d.ToString("MM-dd HH:mm") : "   --   ";
        Console.WriteLine($"{i.Priority,-2} {i.DaysLeft,5}d {dl} conf={i.Analysis.DeadlineConfidence:0.00} " +
                          $"{i.Analysis.Source,-4} [{i.Analysis.Label,-2}] ev={i.Analysis.Events.Count} " +
                          $"| {Trunc(i.Announcement.CourseName, 30),-30} | {Trunc(i.Announcement.Title, 34)}");
    }
    return 0;
}

// Dev helper: forget all discovered course paths so the next sync rescans every menu.
if (args.Length >= 1 && args[0] == "--reset-discovery")
{
    using var rdConn = new Microsoft.Data.Sqlite.SqliteConnection(
        $"Data Source={AppPaths.DatabaseFile}");
    rdConn.Open();
    using var rdCmd = rdConn.CreateCommand();
    rdCmd.CommandText = "DELETE FROM course_paths; DELETE FROM courses;";
    var rd = rdCmd.ExecuteNonQuery();
    Console.WriteLine($"已清除发现记录（{rd} 行），下次同步将全量扫描");
    return 0;
}

// Dev helper: show discovered course paths and their selection state.
if (args.Length >= 1 && args[0] == "--list-paths")
{
    using var lpDb = new BoardDatabase();
    var lp = lpDb.LoadCoursePaths();
    foreach (var g in lp.GroupBy(p => p.CourseId))
    {
        var sel = g.Count(x => x.Selected);
        Console.WriteLine($"course {g.Key}: {g.Count()} 个内容区, 选中 {sel}");
        foreach (var p in g)
            Console.WriteLine($"    {(p.Selected ? "[✓]" : "[ ]")} {p.Display}  items={p.ItemCount}");
    }
    return 0;
}

// Dev helper: for one course, list every content area and report what parsing yields.
if (args.Length >= 2 && args[0] == "--probe-areas")
{
    var paSettings = SettingsStore.Load();
    var paSecrets = SettingsStore.LoadSecrets();
    if (paSecrets.Password.Length == 0) { Console.WriteLine("未配置凭据"); return 1; }

    using var paClient = new BlackboardClient(paSettings, paSecrets);
    var paLogin = await paClient.LoginAsync();
    Console.WriteLine($"登录: {(paLogin.Success ? "成功" : "失败 " + paLogin.Message)}");
    if (!paLogin.Success) return 1;

    var paFetcher = new BlackboardFetcher(paClient);
    var paCourses = await paFetcher.GetCoursesAsync();
    var target = paCourses.FirstOrDefault(c => c.Id == args[1])
                 ?? paCourses.FirstOrDefault(c => c.Name.Contains(args[1], StringComparison.Ordinal));
    if (target is null) { Console.WriteLine("未找到课程 " + args[1]); return 1; }

    Console.WriteLine($"课程: {target.Name} ({target.Id})");
    var paMenu = await paFetcher.GetCourseMenuAsync(target);
    var areas = paMenu.Where(m => m.IsContentArea).ToList();
    Console.WriteLine($"内容区 {areas.Count} 个");

    foreach (var m in areas)
    {
        var path = new CoursePath
        {
            CourseId = target.Id,
            PathKey = Hash.Sha256(m.Url)[..12],
            Label = m.Label,
            Group = m.Group,
            Url = m.Url,
            DiscoveredAt = DateTimeOffset.Now
        };
        var items = await paFetcher.FetchContentAreaAsync(target, path);
        if (items is null)
        {
            Console.WriteLine($"  [{m.Group}] {m.Label}: 抓取失败");
            continue;
        }
        var kinds = string.Join("/", items.Select(i => i.ContentKind).Distinct());
        Console.WriteLine($"  [{m.Group}] {m.Label}: {items.Count} 条  类型={kinds}");
        foreach (var it in items.Take(3))
            Console.WriteLine($"      · {it.Title}  | {Trunc(it.Body, 60)}");
    }
    return 0;
}

// Dev helper: log in and dump each course's menu, highlighting content areas.
if (args.Length >= 1 && args[0] == "--discover")
{
    var dvSettings = SettingsStore.Load();
    var dvSecrets = SettingsStore.LoadSecrets();
    if (dvSecrets.Password.Length == 0) { Console.WriteLine("未配置凭据"); return 1; }

    using var dvClient = new BlackboardClient(dvSettings, dvSecrets);
    var dvLogin = await dvClient.LoginAsync();
    Console.WriteLine($"登录: {(dvLogin.Success ? "成功" : "失败 " + dvLogin.Message)}");
    if (!dvLogin.Success) return 1;

    var dvFetcher = new BlackboardFetcher(dvClient);
    var dvCourses = await dvFetcher.GetCoursesAsync();

    foreach (var c in dvCourses)
    {
        var menu = await dvFetcher.GetCourseMenuAsync(c);
        var content = menu.Where(m => m.IsContentArea).ToList();
        Console.WriteLine();
        Console.WriteLine($"=== {c.Name}  ({c.Id})  menu={menu.Count}  contentAreas={content.Count}");
        foreach (var m in content)
            Console.WriteLine($"    [{m.Group}] {m.Label}");
    }
    return 0;
}

// Dev helper: run the rule-layer date parser over a JSON list of {title, body, expect} cases.
if (args.Length >= 2 && args[0] == "--parse-dates")
{
    using var caseDoc = System.Text.Json.JsonDocument.Parse(
        File.ReadAllText(args[1], Encoding.UTF8));

    var anchor = DateTimeOffset.Now;
    Console.WriteLine($"anchor(now) = {anchor:yyyy-MM-dd HH:mm}");
    Console.WriteLine(new string('-', 86));

    int caseTotal = 0, casePass = 0;
    foreach (var el in caseDoc.RootElement.EnumerateArray())
    {
        caseTotal++;
        var title = el.GetProperty("title").GetString() ?? "";
        var body = el.GetProperty("body").GetString() ?? "";
        var expect = el.GetProperty("expect").GetString() ?? "";

        var hit = DateParsing.FindDeadline(body, anchor);
        var got = hit is { } h ? $"{h.Value:yyyy-MM-dd HH:mm}  conf={h.Confidence:0.00}" : "（未识别）";
        var ok = hit is { } x && expect.StartsWith(x.Value.ToString("yyyy-MM-dd"), StringComparison.Ordinal);
        if (ok) casePass++;

        Console.WriteLine($"{(ok ? "✓" : "✗")} {title,-8} 得到 {got}");
        Console.WriteLine($"   期望 {expect}");
    }

    Console.WriteLine(new string('-', 86));
    Console.WriteLine($"命中 {casePass}/{caseTotal}");
    return casePass == caseTotal ? 0 : 1;
}

static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

// Dev helper: inject a fake announcement to prove stale-row reconciliation works.
if (args.Length >= 1 && args[0] == "--inject-fake")
{
    using var fkConn = new Microsoft.Data.Sqlite.SqliteConnection(
        $"Data Source={AppPaths.DatabaseFile}");
    fkConn.Open();
    using var fkCmd = fkConn.CreateCommand();
    fkCmd.CommandText = """
        INSERT INTO announcements
            (id, course_id, course_name, title, body, published_at, author,
             content_hash, first_seen, last_changed, manual_archived)
        VALUES ('_999999_1','_fake_1','[旧账号遗留]','STALE ROW FROM PREVIOUS ACCOUNT','',
                NULL,'','fakehash',$now,$now,0)
        ON CONFLICT(id) DO NOTHING
        """;
    var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz");
    fkCmd.Parameters.AddWithValue("$now", stamp);
    var n = fkCmd.ExecuteNonQuery();
    Console.WriteLine(n > 0 ? "已注入 1 条假公告 (_999999_1)" : "假公告已存在");
    return 0;
}

// Dev helper: list rows currently flagged as manually archived.
if (args.Length >= 1 && args[0] == "--list-manual")
{
    using var lmConn = new Microsoft.Data.Sqlite.SqliteConnection(
        $"Data Source={AppPaths.DatabaseFile}");
    lmConn.Open();
    using var lmCmd = lmConn.CreateCommand();
    lmCmd.CommandText = """
        SELECT id, course_name, title, last_changed, first_seen
        FROM announcements WHERE manual_archived <> 0
        """;
    using var lmR = lmCmd.ExecuteReader();
    int n = 0;
    while (lmR.Read())
    {
        n++;
        Console.WriteLine($"{lmR.GetString(0)}  changed={lmR.GetString(3)}  first={lmR.GetString(4)}");
        Console.WriteLine($"    [{lmR.GetString(1)}] {Trunc(lmR.GetString(2), 50)}");
    }
    Console.WriteLine($"合计 {n} 条");
    return 0;
}

// Dev helper: clear every user-set manual archive flag (test cleanup).
if (args.Length >= 1 && args[0] == "--reset-manual")
{
    using var resetConn = new Microsoft.Data.Sqlite.SqliteConnection(
        $"Data Source={AppPaths.DatabaseFile}");
    resetConn.Open();
    using var cmd = resetConn.CreateCommand();
    cmd.CommandText = "UPDATE announcements SET manual_archived = 0 WHERE manual_archived <> 0";
    var n = cmd.ExecuteNonQuery();
    Console.WriteLine($"已清除 {n} 条手动归档标记");
    return 0;
}

// Dev helper: drop cached analyses so the next sync re-runs rule + LLM enrichment.
if (args.Length >= 1 && args[0] == "--clear-analyses")
{
    using var clearConn = new Microsoft.Data.Sqlite.SqliteConnection(
        $"Data Source={AppPaths.DatabaseFile}");
    clearConn.Open();
    using var cmd = clearConn.CreateCommand();
    cmd.CommandText = "DELETE FROM analyses";
    var n = cmd.ExecuteNonQuery();
    Console.WriteLine($"已清除 {n} 条分析缓存");
    return 0;
}

// Live probe against the saved settings + DPAPI-decrypted key (same path as the UI test button).
if (args.Length >= 1 && args[0] == "--llm-live")
{
    var liveSettings = SettingsStore.Load();
    var liveSecrets = SettingsStore.LoadSecrets();

    Console.WriteLine($"baseUrl = {liveSettings.LlmBaseUrl}");
    Console.WriteLine($"model   = {liveSettings.LlmModel}");
    Console.WriteLine($"enabled = {liveSettings.LlmEnabled}");
    Console.WriteLine($"keyLen  = {liveSecrets.LlmApiKey.Length}");

    if (liveSecrets.LlmApiKey.Length == 0)
    {
        Console.WriteLine("未找到 API Key（settings 里填了但没保存？）");
        return 1;
    }

    using var liveLlm = new LlmAnalyzer(liveSettings, liveSecrets);
    Console.WriteLine("IsConfigured = " + liveLlm.IsConfigured);

    var probe = await liveLlm.ProbeAsync();
    Console.WriteLine($"结果: {(probe.Ok ? "OK" : "FAIL")}   {probe.ElapsedMs} ms");
    Console.WriteLine("详情: " + probe.Message);
    return probe.Ok ? 0 : 1;
}

// Self-test the LLM enrichment path against a local mock of an OpenAI-compatible endpoint.
// Verifies request shape, JSON extraction, field mapping and strict-schema tolerance without a real key.
if (args.Length >= 1 && args[0] == "--llm-selftest")
{
    const string canned = """
        {"category":"assignment","action_required":true,
         "action":"提交 CS305 Lab1 到课程 OJ",
         "deadline":"2026-10-25T23:59:00+08:00","deadline_confidence":0.95,
         "hard_deadline":true,
         "key_constraints":["仅通过 OJ 提交","不接受迟交"],
         "summary":"Lab1 周日晚截止，需在 OJ 提交","is_noise":false}
        """;

    var bodyJson = System.Text.Json.JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { role = "assistant", content = canned } } }
    });

    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    Console.WriteLine($"mock LLM 监听 127.0.0.1:{port}");

    _ = Task.Run(async () =>
    {
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(async () =>
            {
                using var c = client;
                using var stream = c.GetStream();
                // Consume headers + body enough to keep the client happy.
                var buf = new byte[65536];
                int read = await stream.ReadAsync(buf);
                var request = System.Text.Encoding.UTF8.GetString(buf, 0, read);

                if (!request.Contains("/chat/completions"))
                {
                    Console.WriteLine("!! 请求路径不符合预期: " + request.Split('\n')[0]);
                }
                else
                {
                    Console.WriteLine("✓ 收到 /chat/completions 请求");
                    if (request.Contains("response_format")) Console.WriteLine("✓ 携带 response_format");
                    if (request.Contains("Bearer mock-key")) Console.WriteLine("✓ 携带 Authorization");
                }

                var bytes = System.Text.Encoding.UTF8.GetBytes(bodyJson);
                var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            });
        }
    });

    var llmSettings = new AppSettings
    {
        LlmEnabled = true,
        LlmBaseUrl = $"http://127.0.0.1:{port}/v1",
        LlmModel = "mock-model"
    };
    using var llm = new LlmAnalyzer(llmSettings, new Secrets { LlmApiKey = "mock-key" });
    Console.WriteLine("IsConfigured = " + llm.IsConfigured);

    var sample = new Announcement
    {
        Id = "_45871_1",
        CourseId = "_8804_1",
        CourseName = "Computer Networks Fall 2026",
        Title = "Lab Assignment1",
        Body = "Demander: deadline 10月25日 23:59, submit via OJ.",
        ContentHash = "x"
    };

    var result = await llm.AnalyzeAsync(sample, null);
    Console.WriteLine("---- 映射结果 ----");
    if (result is null)
    {
        Console.WriteLine("FAIL: 返回 null, LastError=" + llm.LastError);
        return 1;
    }
    Console.WriteLine($"category          = {result.Category}");
    Console.WriteLine($"action_required   = {result.ActionRequired}");
    Console.WriteLine($"action            = {result.Action}");
    Console.WriteLine($"deadline          = {result.Deadline:yyyy-MM-dd HH:mm zzz}");
    Console.WriteLine($"deadline_conf     = {result.DeadlineConfidence}");
    Console.WriteLine($"hard_deadline     = {result.HardDeadline}");
    Console.WriteLine($"key_constraints   = {string.Join(" | ", result.KeyConstraints)}");
    Console.WriteLine($"summary           = {result.Summary}");
    Console.WriteLine($"source            = {result.Source}");

    var ok = result.Category == "assignment"
             && result.ActionRequired && result.HardDeadline
             && result.Deadline is { } d && d.Date == new DateTime(2026, 10, 25)
             && result.KeyConstraints.Count == 2
             && result.Summary.Length > 0
             && result.Source == "llm";
    Console.WriteLine(ok ? "SELFTEST PASS" : "SELFTEST FAIL");
    listener.Stop();
    return ok ? 0 : 1;
}

// Dev helper: seed settings + DPAPI-encrypted secrets through the real app code path.
if (args.Length >= 2 && args[0] == "--seed")
{
    var src = File.ReadAllText(args[1], Encoding.UTF8).Replace("\r\n", "\n").Split('\n');

    var seeded = SettingsStore.Load();
    seeded.Username = src.Length > 0 ? src[0].Trim() : "";

    var existing = SettingsStore.LoadSecrets();
    existing.Password = src.Length > 1 ? src[1].TrimEnd('\n') : "";

    SettingsStore.Save(seeded);
    SettingsStore.SaveSecrets(existing);

    Console.WriteLine($"设置 -> {AppPaths.SettingsFile}");
    Console.WriteLine($"凭据 -> {AppPaths.SecretsFile} ({new FileInfo(AppPaths.SecretsFile).Length} bytes, DPAPI)");
    Console.WriteLine($"账号: {seeded.Username}   密码长度: {existing.Password.Length}");
    return 0;
}

string? credPath = null;
string outPath = Path.Combine(AppContext.BaseDirectory, "probe_out.json");

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--cred" && i + 1 < args.Length) credPath = args[++i];
    else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
}

if (credPath is null || !File.Exists(credPath))
{
    Console.WriteLine("用法: SustechBoard.Probe --cred <文件> [--out <json>]");
    Console.WriteLine("凭据文件格式: 第1行账号, 第2行密码（密码原样，不 Trim）");
    return 2;
}

var lines = File.ReadAllText(credPath, Encoding.UTF8).Replace("\r\n", "\n").Split('\n');
var user = lines.Length > 0 ? lines[0].Trim() : "";
var pass = lines.Length > 1 ? lines[1].TrimEnd('\n') : "";

var now = DateTimeOffset.Now;
Console.WriteLine($"账号: {user}   密码长度: {pass.Length}   现在: {now:yyyy-MM-dd HH:mm dddd}");
Console.WriteLine(new string('-', 70));

var settings = new AppSettings { Username = user };
var secrets = new Secrets { Password = pass };

using var client = new BlackboardClient(settings, secrets);
var fetcher = new BlackboardFetcher(client);

var login = await client.LoginAsync();
Console.WriteLine($"登录: {(login.Success ? "成功" : "失败")}  {login.Message}");
if (!login.Success) return 1;

var courses = await fetcher.GetCoursesAsync();
Console.WriteLine($"课程数: {courses.Count}");
foreach (var c in courses) Console.WriteLine($"  {c.Id,-10} {c.Name}");

Console.WriteLine(new string('-', 70));
var all = new List<Announcement>();
foreach (var c in courses)
{
    var fetched = await fetcher.GetAnnouncementsAsync(c);
    if (fetched is null)
    {
        Console.WriteLine($"  !! {c.Name}: 抓取失败");
        continue;
    }
    all.AddRange(fetched);
}
Console.WriteLine($"公告合计: {all.Count}");

Console.WriteLine(new string('-', 70));
Console.WriteLine("截止日期抽取（规则层，年份锚定发布日）:");
var scan = new List<(Announcement A, DeadlineHit? Hit)>();
foreach (var a in all.OrderBy(a => a.PublishedAt ?? DateTimeOffset.MinValue))
{
    var anchor = a.PublishedAt ?? now;
    var hit = DateParsing.FindDeadline(a.Body, anchor);
    scan.Add((a, hit));
    if (hit is not { } h) continue;
    var stale = a.PublishedAt is { } p && p < now.AddDays(-60);
    var tag = h.Value < now ? (stale ? "STALE" : "PAST ") : "FUTURE";
    if (tag == "STALE") continue;
    Console.WriteLine($"  [{tag}] {h.Value:yyyy-MM-dd HH:mm} conf={h.Confidence:0.00}  [{a.CourseName}] {a.Title}");
}

var upcoming = scan
    .Where(x => x.Hit is { } h && h.Value >= now && h.Confidence >= 0.70)
    .OrderBy(x => x.Hit!.Value.Value)
    .ToList();
Console.WriteLine(new string('-', 70));
Console.WriteLine($"未来且可信(conf>=0.70)的截止: {upcoming.Count} 条");
foreach (var (a, hit) in upcoming)
    Console.WriteLine($"  {hit!.Value.Value:MM-dd HH:mm}  [{a.CourseName}] {a.Title}");

var payload = new
{
    generatedAt = now,
    user,
    courses,
    announcements = all.Select(a => new
    {
        a.Id, a.CourseId, a.CourseName, a.Title, a.Body,
        publishedAt = a.PublishedAt,
        a.Author, a.ContentHash,
        deadline = DateParsing.FindDeadline(a.Body, a.PublishedAt ?? now) is { } d ? (DateTimeOffset?)d.Value : null,
        deadlineConfidence = DateParsing.FindDeadline(a.Body, a.PublishedAt ?? now)?.Confidence ?? 0
    })
};
File.WriteAllText(outPath, JsonSerializer.Serialize(payload,
    new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    }), Encoding.UTF8);
Console.WriteLine(new string('-', 70));
Console.WriteLine($"已保存: {outPath}");
return 0;
