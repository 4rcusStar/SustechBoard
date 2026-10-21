using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using SustechBoard.Core;

namespace SustechBoard.App;

public enum PanelTab { Todo, Calendar, All, ManualArchive, ExpiredArchive }

/// <summary>Presentation wrapper around a <see cref="PanelItem"/>.</summary>
public sealed class ItemVm
{
    private static readonly Brush BrushP0 = Frozen(0xFF, 0x6B, 0x6B);
    private static readonly Brush BrushP1 = Frozen(0xFF, 0xB0, 0x4D);
    private static readonly Brush BrushP2 = Frozen(0x5D, 0xA9, 0xFF);
    private static readonly Brush BrushP3 = Frozen(0x7A, 0x83, 0x97);
    private static readonly Brush BrushArchive = Frozen(0x4A, 0x51, 0x63);
    private static readonly Brush BrushManual = Frozen(0x9B, 0x8C, 0xF0);
    private static readonly Brush BrushMuted = Frozen(0x9A, 0xA3, 0xB5);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public ItemVm(PanelItem model) => Model = model;

    public PanelItem Model { get; }

    public string Title => Model.Announcement.Title;
    public string Course => Model.Announcement.CourseName;

    public string Summary => string.IsNullOrWhiteSpace(Model.Analysis.Summary)
        ? Collapse(Model.Announcement.Body, 70)
        : Model.Analysis.Summary;

    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    public string DeadlineText => Model.DeadlineText;

    public bool HasDeadline => Model.Analysis.Deadline is not null && Model.Analysis.DeadlineConfidence >= 0.7;

    public Brush DeadlineBrush => Model.DaysLeft switch
    {
        null => BrushMuted,
        <= 1 => BrushP0,
        <= 3 => BrushP1,
        <= 14 => BrushP2,
        _ => BrushMuted
    };

    public string BadgeText => Model.Priority switch
    {
        Priority.P0 => "P0",
        Priority.P1 => "P1",
        Priority.P2 => "P2",
        Priority.P3 => "P3",
        _ => Model.ArchiveKind switch
        {
            ArchiveKind.Manual => "手动",
            ArchiveKind.Expired => "过期",
            _ => "归档"
        }
    };

    public Brush BadgeBrush => Model.Priority switch
    {
        Priority.P0 => BrushP0,
        Priority.P1 => BrushP1,
        Priority.P2 => BrushP2,
        Priority.P3 => BrushP3,
        _ => Model.ArchiveKind == ArchiveKind.Manual ? BrushManual : BrushArchive
    };

    // ---- manual archive affordance ----

    /// <summary>Expired items cannot be restored, so the button is hidden for them.</summary>
    public bool CanToggleArchive => Model.ArchiveKind != ArchiveKind.Expired;

    public string ArchiveButtonText => Model.Announcement.IsManuallyArchived ? "↩ 还原" : "归档";

    public Visibility ArchiveButtonVisibility =>
        CanToggleArchive ? Visibility.Visible : Visibility.Collapsed;

    public bool IsLlmAnalyzed => Model.Analysis.Source == "llm";

    // ---- AI label chip ----

    public string LabelText =>
        string.IsNullOrWhiteSpace(Model.Analysis.Label) ? "其他" : Model.Analysis.Label;

    public Brush LabelBrush => EventPalette.ForLabel(LabelText);
    public Brush LabelBgBrush => EventPalette.ForLabelBg(LabelText);

    // ---- detail pane ----
    public string Body => Model.Announcement.Body;

    public string MetaLine
    {
        get
        {
            var parts = new List<string> { Course };
            if (Model.Announcement.PublishedAt is { } p) parts.Add("发布于 " + p.ToString("yyyy-MM-dd HH:mm"));
            if (!string.IsNullOrWhiteSpace(Model.Announcement.Author)) parts.Add(Model.Announcement.Author);
            return string.Join("  ·  ", parts);
        }
    }

    public string DeadlineLine
    {
        get
        {
            if (Model.Analysis.Deadline is not { } d) return "未识别到明确截止时间";
            var conf = Model.Analysis.DeadlineConfidence;
            var kind = Model.Analysis.HardDeadline ? "硬截止" : "截止";
            return $"{kind}：{d:yyyy-MM-dd HH:mm}   （{Model.DeadlineText}，置信度 {conf:0.00}）";
        }
    }

    public string ConstraintsLine => Model.Analysis.KeyConstraints.Count == 0
        ? ""
        : string.Join("    ", Model.Analysis.KeyConstraints.Select(c => "· " + c));

    public bool HasConstraints => Model.Analysis.KeyConstraints.Count > 0;

    public string ActionLine => Model.Analysis.Action ?? "";

    public bool HasAction => !string.IsNullOrWhiteSpace(ActionLine);

    public string AnalyzeSource => Model.Analysis.Source switch
    {
        "llm" => "智能分析",
        _ => "规则分析"
    };

    public string AnnouncementUrl =>
        "https://bb.sustech.edu.cn/webapps/blackboard/execute/announcement" +
        $"?method=search&context=course_entry&course_id={Uri.EscapeDataString(Model.Announcement.CourseId)}" +
        "&handle=announcements_entry&mode=view";

    private static string Collapse(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private IReadOnlyList<PanelItem> _all = Array.Empty<PanelItem>();
    private PanelTab _tab = PanelTab.Todo;
    private string _status = "启动中…";
    private int _courseCount;

    public ObservableCollection<ItemVm> Items { get; } = new();

    public string Status
    {
        get => _status;
        set { _status = value; Raise(); }
    }

    public string Subtitle
    {
        get
        {
            var todo = _all.Count(i => i.Priority is Priority.P0 or Priority.P1 or Priority.P2);
            var all = _all.Count(i => i.Priority != Priority.Archive);
            return $"{todo} 项待办 · {all} 条有效 · {_courseCount} 门课";
        }
    }

    public string EmptyText => _tab switch
    {
        PanelTab.ManualArchive => "没有手动归档",
        PanelTab.ExpiredArchive => "没有过期内容",
        PanelTab.Todo => "暂无待办事项",
        _ => "暂无内容"
    };

    public void SetItems(IReadOnlyList<PanelItem> items, int courseCount)
    {
        _all = items;
        _courseCount = courseCount;
        Apply();
    }

    public void SetTab(PanelTab tab)
    {
        _tab = tab;
        Apply();
    }

    private void Apply()
    {
        IEnumerable<PanelItem> src = _tab switch
        {
            PanelTab.Todo => _all.Where(i => i.Priority is Priority.P0 or Priority.P1 or Priority.P2),
            PanelTab.Calendar => Enumerable.Empty<PanelItem>(),
            PanelTab.ManualArchive => _all.Where(i => i.ArchiveKind == ArchiveKind.Manual),
            PanelTab.ExpiredArchive => _all.Where(i => i.ArchiveKind == ArchiveKind.Expired),
            _ => _all.Where(i => i.ArchiveKind == ArchiveKind.None)
        };

        Items.Clear();
        foreach (var i in src) Items.Add(new ItemVm(i));

        Raise(nameof(Subtitle));
        Raise(nameof(EmptyText));
        Raise(nameof(IsEmpty));
    }

    public bool IsEmpty => Items.Count == 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
