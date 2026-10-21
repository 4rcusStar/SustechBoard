using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using SustechBoard.Core;

namespace SustechBoard.App;

/// <summary>Colour and text coding for calendar events / card labels.</summary>
public static class EventPalette
{
    public static readonly Brush QuizBrush = Frozen(0xB9, 0x8B, 0xFF);
    public static readonly Brush ExamBrush = Frozen(0xFF, 0x6B, 0x6B);
    public static readonly Brush AssignmentBrush = Frozen(0x5D, 0xA9, 0xFF);
    public static readonly Brush OtherBrush = Frozen(0x7A, 0x83, 0x97);
    public static readonly Brush NoticeBrush = Frozen(0x6B, 0x74, 0x88);
    public static readonly Brush GradeBrush = Frozen(0x4E, 0xC3, 0x8A);
    public static readonly Brush ResourceBrush = Frozen(0x3F, 0xBF, 0xC0);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static Brush ForKind(string kind) => kind switch
    {
        "quiz" => QuizBrush,
        "midterm" or "final" => ExamBrush,
        "assignment" => AssignmentBrush,
        _ => OtherBrush
    };

    public static string LabelForKind(string kind) => kind switch
    {
        "quiz" => "quiz",
        "midterm" => "考试",
        "final" => "考试",
        "assignment" => "作业",
        _ => "事件"
    };

    /// <summary>Colour for the AI label chip on a card.</summary>
    public static Brush ForLabel(string label) => label switch
    {
        "考试" => ExamBrush,
        "quiz" => QuizBrush,
        "作业" => AssignmentBrush,
        "成绩" => GradeBrush,
        "资料" => ResourceBrush,
        "通知" => NoticeBrush,
        _ => OtherBrush
    };

    /// <summary>Low-alpha background derived from the label colour.</summary>
    public static Brush ForLabelBg(string label)
    {
        var c = ((SolidColorBrush)ForLabel(label)).Color;
        var brush = new SolidColorBrush(Color.FromArgb(0x2E, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }
}

/// <summary>One cell of the month grid.</summary>
public sealed class CalendarDayVm : INotifyPropertyChanged
{
    private static readonly Brush ForeDim = Frozen(0x4A, 0x51, 0x63);
    private static readonly Brush ForeNormal = Frozen(0xE9, 0xEC, 0xF2);
    private static readonly Brush SelBg = Frozen(0x2E, 0x6F, 0xC0);
    private static readonly Brush TodayBg = Frozen(0x24, 0x2A, 0x36);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private bool _isSelected;

    public CalendarDayVm(DateTime date, bool isCurrentMonth, bool isToday, IReadOnlyList<Brush> dots)
    {
        Date = date;
        IsCurrentMonth = isCurrentMonth;
        IsToday = isToday;
        Dot1 = dots.Count > 0 ? dots[0] : null;
        Dot2 = dots.Count > 1 ? dots[1] : null;
        Dot3 = dots.Count > 2 ? dots[2] : null;
    }

    public DateTime Date { get; }
    public bool IsCurrentMonth { get; }
    public bool IsToday { get; }
    public string DayText => Date.Day.ToString(CultureInfo.InvariantCulture);

    public Brush? Dot1 { get; }
    public Brush? Dot2 { get; }
    public Brush? Dot3 { get; }

    public Visibility Dot1Visibility => Dot1 is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility Dot2Visibility => Dot2 is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility Dot3Visibility => Dot3 is null ? Visibility.Collapsed : Visibility.Visible;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Raise(nameof(IsSelected));
            Raise(nameof(Background));
            Raise(nameof(Foreground));
            Raise(nameof(CellBorder));
            Raise(nameof(FontWeight));
        }
    }

    public Brush Background => IsSelected ? SelBg : (IsToday ? TodayBg : Brushes.Transparent);

    public Brush Foreground =>
        IsSelected ? Brushes.White : (IsCurrentMonth ? ForeNormal : ForeDim);

    public Brush CellBorder => IsToday && !IsSelected
        ? new SolidColorBrush(Color.FromRgb(0x4E, 0xA1, 0xFF))
        : Brushes.Transparent;

    public FontWeight FontWeight => IsToday || IsSelected ? FontWeights.SemiBold : FontWeights.Normal;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One event row inside the selected day's list.</summary>
public sealed class CalendarEventVm
{
    public CalendarEventVm(CalendarEvent e)
    {
        KindLabel = EventPalette.LabelForKind(e.Kind);
        KindBrush = EventPalette.ForKind(e.Kind);
        Course = e.CourseName;
        Title = e.Title;

        if (e.AllDay)
        {
            TimeText = "全天";
        }
        else if (e.End is { } end)
        {
            TimeText = $"{e.Start:HH:mm}-{end:HH:mm}";
        }
        else
        {
            TimeText = e.Start.ToString("HH:mm");
        }

        Location = string.IsNullOrWhiteSpace(e.Location) ? "" : e.Location;
    }

    public string KindLabel { get; }
    public Brush KindBrush { get; }
    public string Course { get; }
    public string Title { get; }
    public string TimeText { get; }
    public string Location { get; }
    public bool HasLocation => Location.Length > 0;
}

/// <summary>Month grid + selected-day agenda, driven by the core's calendar events.</summary>
public sealed class CalendarViewModel : INotifyPropertyChanged
{
    private static readonly string[] WeekNames = { "一", "二", "三", "四", "五", "六", "日" };

    private DateTime _month;
    private DateTime _selected;
    private IReadOnlyList<CalendarEvent> _events = Array.Empty<CalendarEvent>();

    public CalendarViewModel()
    {
        var today = DateTime.Today;
        _month = new DateTime(today.Year, today.Month, 1);
        _selected = today;
        Rebuild();
    }

    public ObservableCollection<CalendarDayVm> Days { get; } = new();
    public ObservableCollection<CalendarEventVm> SelectedEvents { get; } = new();

    public string MonthText => _month.ToString("yyyy 年 M 月", CultureInfo.InvariantCulture);

    public string SelectedDateText
    {
        get
        {
            var dow = _selected.DayOfWeek switch
            {
                DayOfWeek.Monday => "周一",
                DayOfWeek.Tuesday => "周二",
                DayOfWeek.Wednesday => "周三",
                DayOfWeek.Thursday => "周四",
                DayOfWeek.Friday => "周五",
                DayOfWeek.Saturday => "周六",
                _ => "周日"
            };
            return $"{_selected:M 月 d 日} {dow}";
        }
    }

    public string EmptyText => _month.Year == DateTime.Today.Year && _month.Month == DateTime.Today.Month
        ? "本月暂无事件"
        : "该月暂无事件";

    public bool HasSelectedEvents => SelectedEvents.Count > 0;

    public string SelectedCountText => SelectedEvents.Count == 0
        ? "无事件"
        : $"{SelectedEvents.Count} 个事件";

    public string WeekHeader => string.Join("   ", WeekNames);

    private bool _autoSelected;

    public void SetEvents(IReadOnlyList<CalendarEvent> events)
    {
        _events = events;

        // On the first load, focus the next upcoming event instead of "today",
        // otherwise a day with nothing on it makes the pane look empty.
        if (!_autoSelected && events.Count > 0)
        {
            _autoSelected = true;
            var today = DateTime.Today;
            var upcoming = events
                .Select(e => e.Start.ToLocalTime().Date)
                .Where(d => d >= today)
                .ToList();

            var pick = upcoming.Count > 0
                ? upcoming.Min()
                : events.Select(e => e.Start.ToLocalTime().Date).Max();

            if (pick != default)
            {
                _selected = pick;
                _month = new DateTime(pick.Year, pick.Month, 1);
            }
        }

        Rebuild();
    }

    public void PreviousMonth()
    {
        _month = _month.AddMonths(-1);
        if (_selected.Month != _month.Month || _selected.Year != _month.Year)
            _selected = FirstMeaningfulDay(_month);
        Rebuild();
    }

    public void NextMonth()
    {
        _month = _month.AddMonths(1);
        if (_selected.Month != _month.Month || _selected.Year != _month.Year)
            _selected = FirstMeaningfulDay(_month);
        Rebuild();
    }

    public void GoToday()
    {
        var today = DateTime.Today;
        _month = new DateTime(today.Year, today.Month, 1);
        _selected = today;
        Rebuild();
    }

    public void SelectDate(DateTime date)
    {
        _selected = date.Date;
        if (_selected.Month != _month.Month || _selected.Year != _month.Year)
            _month = new DateTime(_selected.Year, _selected.Month, 1);

        foreach (var d in Days) d.IsSelected = d.Date == _selected;
        RefreshSelectedEvents();
        RaiseState();
    }

    /// <summary>Prefer the first day that actually has events, else the 1st.</summary>
    private DateTime FirstMeaningfulDay(DateTime month)
    {
        var hit = _events
            .Select(e => e.Start.ToLocalTime().Date)
            .Where(d => d.Year == month.Year && d.Month == month.Month)
            .OrderBy(d => d)
            .FirstOrDefault();
        return hit != default ? hit : month;
    }

    private void Rebuild()
    {
        Days.Clear();

        var firstOfMonth = _month;
        int offset = ((int)firstOfMonth.DayOfWeek + 6) % 7;   // Monday-first
        var start = firstOfMonth.AddDays(-offset);

        for (int i = 0; i < 42; i++)
        {
            var date = start.AddDays(i).Date;

            var dots = _events
                .Where(e => e.Start.ToLocalTime().Date == date)
                .Select(e => EventPalette.ForKind(e.Kind))
                .Distinct()
                .Take(3)
                .ToList();

            Days.Add(new CalendarDayVm(
                date,
                date.Month == _month.Month && date.Year == _month.Year,
                date == DateTime.Today,
                dots)
            {
                IsSelected = date == _selected
            });
        }

        RefreshSelectedEvents();
        RaiseState();
    }

    private void RefreshSelectedEvents()
    {
        SelectedEvents.Clear();
        foreach (var e in _events.Where(e => e.Start.ToLocalTime().Date == _selected)
                                 .OrderBy(e => e.AllDay ? 0 : 1)
                                 .ThenBy(e => e.Start))
        {
            SelectedEvents.Add(new CalendarEventVm(e));
        }
    }

    private void RaiseState()
    {
        Raise(nameof(MonthText));
        Raise(nameof(SelectedDateText));
        Raise(nameof(SelectedCountText));
        Raise(nameof(EmptyText));
        Raise(nameof(HasSelectedEvents));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
