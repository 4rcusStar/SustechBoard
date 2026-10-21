using System.Text.Json;
using System.Text.Json.Serialization;

namespace SustechBoard.Core;

/// <summary>Serialization helpers for the analysis payload stored in SQLite.</summary>
public static class AnalysisJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize(AnnouncementAnalysis a) => JsonSerializer.Serialize(a, Options);

    public static AnnouncementAnalysis? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<AnnouncementAnalysis>(json, Options); }
        catch { return null; }
    }
}

/// <summary>Heuristic analysis used when no LLM is configured, and as the fallback when it fails.</summary>
public static class RuleAnalyzer
{
    private static readonly (string Category, string[] Keys)[] Categories =
    {
        ("exam",          new[] { "考试", "期中", "期末", "exam", "midterm", "final exam", "quiz" }),
        ("assignment",    new[] { "作业", "assignment", "homework", " hw", "lab ", "project", "due", "deadline", "提交" }),
        ("grade_release", new[] { "成绩", "批改", "评分", "grade", "graded", "score", "rubric", "复查" }),
        ("resource",      new[] { "答案", "解答", "solution", "slides", "课件", "复习", "material", "视频" }),
        ("admin_notice",  new[] { "通知", "说明会", "实习", "奖学金", "学分", "推免", "报名", "申请" })
    };

    private static readonly string[] NoiseMarkers =
    {
        "EXAMPLE", "Welcome!", "课程信息", "Course Information", "blackboard@sustech.edu.cn"
    };

    public static AnnouncementAnalysis Analyze(Announcement a, DeadlineHit? deadline, DateTimeOffset now)
    {
        var haystack = (a.Title + " " + a.Body).ToLowerInvariant();

        var category = "unknown";
        foreach (var (cat, keys) in Categories)
        {
            if (keys.Any(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                category = cat;
                break;
            }
        }

        var isNoise = NoiseMarkers.Any(m =>
            a.Title.Contains(m, StringComparison.OrdinalIgnoreCase));

        var actionRequired = category is "assignment" or "exam"
            || haystack.Contains("提交") || haystack.Contains("submit")
            || haystack.Contains("报名") || haystack.Contains("regard");

        var effectiveCategory = isNoise ? "irrelevant" : category;

        var result = new AnnouncementAnalysis
        {
            Label = LlmAnalyzer.CategoryToLabel(effectiveCategory),
            Category = effectiveCategory,
            ActionRequired = actionRequired,
            IsNoise = isNoise,
            Source = "rule",
            Summary = Summary(a.Body),
            Deadline = deadline?.Value,
            DeadlineConfidence = deadline?.Confidence ?? 0
        };

        // Degraded calendar support: one event derived from the single rule-layer date.
        if (deadline is { } hit && hit.Confidence >= 0.70)
        {
            var isAllDay = hit.Value.TimeOfDay == new TimeSpan(23, 59, 0);
            result.Events.Add(new AnnouncementEvent
            {
                Kind = effectiveCategory switch
                {
                    "exam" => "other",
                    "assignment" => "assignment",
                    _ => "other"
                },
                Start = hit.Value,
                AllDay = isAllDay
            });
        }

        // Constraints the rule layer can catch with confidence; the LLM layer finds far more.
        foreach (var (probe, label) in new[]
                 {
                     ("无法补考", "无法补考"), ("不能补考", "不能补考"), ("不予补考", "不予补考"),
                     ("不接受迟交", "不接受迟交"), ("no late submission", "不接受迟交"),
                     ("迟到", "考勤/迟到要求"), ("英文", "可能要求英文作答")
                 })
        {
            if (haystack.Contains(probe, StringComparison.OrdinalIgnoreCase) &&
                !result.KeyConstraints.Contains(label))
                result.KeyConstraints.Add(label);
        }

        return result;
    }

    private static string Summary(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var flat = body.Replace('\n', ' ').Replace('\r', ' ').Trim();
        var cut = flat.Length > 110 ? flat[..110] + "…" : flat;
        return cut;
    }
}

/// <summary>Derives UI priority. Ranking lives in code so it stays reproducible and debuggable.</summary>
public static class Prioritizer
{
    /// <summary>Administrative notices that actually change what a student must do.</summary>
    private static readonly string[] HighValueAdminKeys =
        { "实习", "推免", "奖学金", "学分", "认定", "毕业", "培养方案", "选课", "报名", "开课", "学籍" };

    private static readonly string[] EventWords =
        { "说明会", "会议", "讲座", "活动", "报告会", "座谈", "宣讲" };

    public static PanelItem Build(Announcement a, AnnouncementAnalysis analysis,
                                  AppSettings settings, DateTimeOffset now)
    {
        var item = new PanelItem { Announcement = a, Analysis = analysis };

        var stale = a.PublishedAt is { } p && p < now.AddDays(-60);
        var ageDays = a.PublishedAt is { } pub ? (now - pub).TotalDays : 0;

        int? days = null;
        if (analysis.Deadline is { } dl && analysis.DeadlineConfidence >= 0.70)
        {
            days = (int)Math.Floor((dl - now).TotalDays);
            item.DaysLeft = days;
        }

        var pastEvent = analysis.Deadline is { } eventDate && eventDate < now.AddDays(-1) &&
                        EventWords.Any(w => a.Title.Contains(w, StringComparison.Ordinal));
        var expiredByDeadline = days is < -7;

        // Expiry wins over manual archiving: an expired item is not restorable,
        // even if the user archived it by hand earlier.
        if (stale || expiredByDeadline || pastEvent || analysis.IsNoise)
        {
            item.Priority = Priority.Archive;
            item.ArchiveKind = ArchiveKind.Expired;
            return item;
        }

        if (a.IsManuallyArchived)
        {
            item.Priority = Priority.Archive;
            item.ArchiveKind = ArchiveKind.Manual;
            return item;
        }

        if (days is { } d)
        {
            item.Priority = d < 0 ? Priority.P2                          // recently overdue
                : d <= settings.P0WindowDays ? Priority.P0
                : d <= settings.P1WindowDays ? Priority.P1
                : Priority.P2;
            return item;
        }

        // No reliable date: promote only what genuinely needs attention soon.
        if (analysis.Category == "grade_release" && ageDays <= 14)
        {
            item.Priority = Priority.P2;
        }
        else if (analysis.Category is "assignment" or "exam" && ageDays <= 21)
        {
            item.Priority = Priority.P2;
        }
        else if (analysis.Category == "admin_notice" && ageDays <= 45 && IsHighValueAdmin(a))
        {
            item.Priority = Priority.P2;
        }
        else
        {
            item.Priority = Priority.P3;
        }

        return item;
    }

    private static bool IsHighValueAdmin(Announcement a)
    {
        var haystack = a.Title + " " + a.Body;
        return HighValueAdminKeys.Any(k => haystack.Contains(k, StringComparison.Ordinal));
    }

    public static List<PanelItem> BuildAll(IEnumerable<Announcement> announcements,
                                          IReadOnlyDictionary<string, AnnouncementAnalysis> analyses,
                                          AppSettings settings, DateTimeOffset now)
    {
        var items = new List<PanelItem>();
        foreach (var a in announcements)
        {
            if (!analyses.TryGetValue(a.Id, out var analysis))
                analysis = RuleAnalyzer.Analyze(a, DateParsing.FindDeadline(a.Body, a.PublishedAt ?? now), now);

            items.Add(Build(a, analysis, settings, now));
        }

        return items
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.DaysLeft ?? int.MaxValue)
            .ThenByDescending(i => i.Announcement.PublishedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }
}
