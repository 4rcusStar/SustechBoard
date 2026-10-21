namespace SustechBoard.Core;

/// <summary>A Blackboard course site the user is enrolled in.</summary>
public sealed record CourseInfo(string Id, string Name);

/// <summary>One announcement scraped from a course site.</summary>
public sealed class Announcement
{
    public required string Id { get; init; }          // Blackboard item id, e.g. _45870_1
    public required string CourseId { get; init; }
    public required string CourseName { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string Author { get; init; } = "";
    public required string ContentHash { get; init; }

    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastChanged { get; set; }

    /// <summary>True when the user archived this item by hand. Survives refreshes.</summary>
    public bool IsManuallyArchived { get; set; }

    /// <summary>True when this row was not present (or changed) at the previous sync.</summary>
    public bool IsNew { get; set; }
}

/// <summary>A dated thing a student must show up for (exam/quiz) or deliver (assignment).</summary>
public sealed class AnnouncementEvent
{
    /// <summary>quiz | midterm | final | assignment | other</summary>
    public string Kind { get; set; } = "other";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset? End { get; set; }

    /// <summary>True when only a day is known (e.g. "11-02 截止"), so time must not be shown as a real clock time.</summary>
    public bool AllDay { get; set; }
    public string Location { get; set; } = "";

    public AnnouncementEvent Clone() => new()
    {
        Kind = Kind, Start = Start, End = End, AllDay = AllDay, Location = Location
    };
}

/// <summary>Outcome of analysing a single announcement (rule layer or LLM enrichment).</summary>
public sealed class AnnouncementAnalysis
{
    /// <summary>Short Chinese label shown on the card: 考试 / quiz / 作业 / 通知 / 成绩 / 资料 / 其他.</summary>
    public string Label { get; set; } = "";

    public string Category { get; set; } = "unknown";        // assignment|exam|grade_release|admin_notice|resource|irrelevant
    public bool ActionRequired { get; set; }
    public string? Action { get; set; }
    public DateTimeOffset? Deadline { get; set; }
    public double DeadlineConfidence { get; set; }
    public bool HardDeadline { get; set; }
    public List<string> KeyConstraints { get; set; } = new();
    public List<AnnouncementEvent> Events { get; set; } = new();
    public string Summary { get; set; } = "";
    public bool IsNoise { get; set; }

    /// <summary>"rule" when produced by heuristics only, "llm" when enriched by a model.</summary>
    public string Source { get; set; } = "rule";

    public AnnouncementAnalysis Clone() => new()
    {
        Label = Label,
        Category = Category,
        ActionRequired = ActionRequired,
        Action = Action,
        Deadline = Deadline,
        DeadlineConfidence = DeadlineConfidence,
        HardDeadline = HardDeadline,
        KeyConstraints = new List<string>(KeyConstraints),
        Events = Events.Select(e => e.Clone()).ToList(),
        Summary = Summary,
        IsNoise = IsNoise,
        Source = Source
    };
}

public enum Priority
{
    P0 = 0,   // due within 3 days
    P1 = 1,   // due within 14 days, or exam within 14 days
    P2 = 2,   // graded / needs attention, no hard date
    P3 = 3,   // general notice
    Archive = 4
}

/// <summary>Why an item sits in the archive. Expired wins over manual.</summary>
public enum ArchiveKind
{
    None = 0,
    /// <summary>The user archived it, but it has not expired yet — restorable.</summary>
    Manual = 1,
    /// <summary>Expired / stale / no longer actionable — not restorable.</summary>
    Expired = 2
}

/// <summary>An announcement paired with its analysis and derived priority, ready for the UI.</summary>
public sealed class PanelItem
{
    public required Announcement Announcement { get; init; }
    public required AnnouncementAnalysis Analysis { get; init; }
    public Priority Priority { get; set; } = Priority.P3;
    public ArchiveKind ArchiveKind { get; set; } = ArchiveKind.None;

    /// <summary>Whole days until the deadline; negative when already passed.</summary>
    public int? DaysLeft { get; set; }

    public bool IsOverdue => DaysLeft is < 0;

    public string DeadlineText => Analysis.Deadline is { } d
        ? (DaysLeft switch
        {
            null => d.ToString("MM-dd HH:mm"),
            0 => "今天 " + d.ToString("HH:mm"),
            > 0 => $"剩 {DaysLeft} 天",
            _ => "已过期"
        })
        : "";
}

public sealed class SyncResult
{
    public int TotalAnnouncements { get; set; }
    public List<Announcement> NewOrChanged { get; set; } = new();
    public List<CourseInfo> Courses { get; set; } = new();
    public DateTimeOffset CompletedAt { get; set; }
    public string? Error { get; set; }
    public bool Success => Error is null;
}

/// <summary>A calendar entry flattened from an announcement's events, ready for rendering.</summary>
public sealed record CalendarEvent(
    DateTimeOffset Start,
    DateTimeOffset? End,
    string Kind,          // quiz | midterm | final | assignment | other
    bool AllDay,
    string CourseName,
    string Title,
    string Location,
    string AnnId);
