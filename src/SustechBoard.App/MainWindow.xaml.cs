using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SustechBoard.Core;

namespace SustechBoard.App;

public partial class MainWindow : Window
{
    private readonly SyncService _sync;
    private readonly AppSettings _settings;
    private readonly MainViewModel _vm = new();
    private readonly CalendarViewModel _calendar = new();
    private ItemVm? _current;
    private bool _ready;

    /// <summary>Raised when the user asks for a refresh; the host performs it.</summary>
    public event Func<Task>? RefreshRequested;

    /// <summary>Raised when the user opens settings.</summary>
    public event Action? SettingsRequested;

    public MainWindow(SyncService sync, AppSettings settings)
    {
        InitializeComponent();
        _sync = sync;
        _settings = settings;
        DataContext = _vm;
        CalendarPane.DataContext = _calendar;

        _sync.Updated += OnSyncUpdated;
        _sync.StatusChanged += OnStatusChanged;

        // Restore the user's last panel size before the first layout pass.
        if (_settings.PanelWidth >= MinWidth && _settings.PanelWidth <= MaxWidth)
            Width = _settings.PanelWidth;
        if (_settings.PanelHeight >= MinHeight && _settings.PanelHeight <= MaxHeight)
            Height = _settings.PanelHeight;

        Loaded += (_, _) =>
        {
            ApplyDisplayMode();
            PositionWindow();
            Reload();
        };

        // XAML sets IsChecked on TabTodo, which fires Checked before the other tabs exist.
        _ready = true;
        _vm.SetTab(PanelTab.Todo);
    }

    // ---------- display mode ----------

    /// <summary>Applies the configured Z-order behaviour. Safe to call at any time.</summary>
    // ---------- normal behaviour + lock ----------

    /// <summary>Window is a plain, non-topmost window. Z-order is only affected by the lock.</summary>
    public void ApplyDisplayMode()
    {
        Topmost = false;
        if (IsLocked) SendToBottom();
        Log.Info($"面板状态: {(IsLocked ? "已锁定" : "正常")}  尺寸: {Width:0}x{Height:0}");
    }

    /// <summary>True while the panel is pinned to the desktop bottom with interaction disabled.</summary>
    public bool IsLocked { get; private set; }

    private void Lock_Click(object sender, RoutedEventArgs e) => SetLocked(!IsLocked);

    /// <summary>
    /// Pins the panel to the very bottom of the Z-order and disables everything except the lock
    /// button itself, so the user can only bring it back by clicking the lock while the desktop
    /// is showing.
    /// </summary>
    public void SetLocked(bool locked)
    {
        IsLocked = locked;

        LockButton.Content = locked ? "🔒" : "🔓";
        LockButton.ToolTip = locked
            ? "已锁定：切到桌面后点击此图标解锁"
            : "锁定：沉到桌面最底部并禁用交互";

        // Everything except the lock stops responding to the mouse.
        TabPanel.IsHitTestVisible = !locked;
        ContentHost.IsHitTestVisible = !locked;
        FooterBar.IsHitTestVisible = !locked;

        RefreshButton.IsEnabled = !locked;
        SettingsButton.IsEnabled = !locked;
        CloseButton.IsEnabled = !locked;

        // Dim slightly so the locked state is obvious.
        RootBorder.Opacity = locked ? 0.72 : 1.0;

        Topmost = false;
        if (locked)
        {
            SendToBottom();
            Log.Info("面板已锁定（沉底 + 禁用交互）");
        }
        else
        {
            BringToFront();
            Log.Info("面板已解锁");
        }
    }

    /// <summary>Explicit user request (tray click): surface the panel.</summary>
    public void BringToFront()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Native.SetWindowPos(hwnd, Native.HWND_TOP, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE);
        }
        Show();
        Activate();
    }

    private void SendToBottom()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        // A locked panel must never rise above other windows.
        if (IsLocked) SendToBottom();
    }

    private void PositionWindow()
    {
        // WPF reports DIPs while the screen APIs below report physical pixels under
        // per-monitor DPI, so position with Win32/WinForms and never touch Left/Top.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!Native.GetWindowRect(hwnd, out var wr)) return;

        int w = wr.Right - wr.Left;
        int h = wr.Bottom - wr.Top;
        if (w <= 0 || h <= 0) return;

        double scaleX = w / Math.Max(1.0, Width);
        double scaleY = h / Math.Max(1.0, Height);

        int x, y;
        if (_settings.PanelLeft >= 0 && _settings.PanelTop >= 0)
        {
            x = (int)Math.Round(_settings.PanelLeft * scaleX);
            y = (int)Math.Round(_settings.PanelTop * scaleY);
        }
        else
        {
            var primary = System.Windows.Forms.Screen.PrimaryScreen;
            var area = primary?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            x = area.Right - w - 16;
            y = area.Bottom - h - 16;
        }

        // Keep the panel inside the work area of whichever monitor it targets.
        var screen = System.Windows.Forms.Screen.FromPoint(
                         new System.Drawing.Point(x + w / 2, y + h / 2))
                     ?? System.Windows.Forms.Screen.PrimaryScreen;
        var bounds = screen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, w, h);

        x = Math.Clamp(x, bounds.Left, Math.Max(bounds.Left, bounds.Right - w));
        y = Math.Clamp(y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - h));

        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        Log.Info($"面板定位: 窗口={w}x{h} 物理缩放={scaleX:0.00} 目标=({x},{y}) " +
                 $"屏工作区={bounds} 主屏={System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea} " +
                 $"DIP={dpi.PixelsPerDip:0.00}");
    }

    private static class Native
    {
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;

        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public static readonly IntPtr HWND_BOTTOM = new(1);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);
    }

    // ---------- sync wiring ----------

    private void OnSyncUpdated()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnSyncUpdated));
            return;
        }
        Reload();
    }

    private void OnStatusChanged(string status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<string>(OnStatusChanged), status);
            return;
        }
        _vm.Status = status;
        UpdateSyncText();
    }

    public void Reload()
    {
        _vm.SetItems(_sync.Items, _sync.CourseCount);
        _calendar.SetEvents(_sync.BuildCalendarEvents());

        if (DetailPane.Visibility != Visibility.Visible)
            EmptyHint.Visibility = CalendarPane.Visibility != Visibility.Visible && _vm.Items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        UpdateSyncText();
    }

    private void UpdateSyncText()
    {
        SyncText.Text = _sync.IsSyncing
            ? "刷新中…"
            : _sync.LastSyncAt is { } t ? "更新于 " + t.ToString("HH:mm") : "";
    }

    // ---------- header ----------

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsLocked) return;   // a locked panel must not be draggable
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            if (RefreshRequested is not null) await RefreshRequested();
        }
        catch (Exception ex)
        {
            Log.Error("手动刷新失败", ex);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    // ---------- tabs ----------

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;   // fires during InitializeComponent, before sibling tabs exist

        var tab = PanelTab.Todo;
        if (TabCalendar.IsChecked == true) tab = PanelTab.Calendar;
        else if (TabAll.IsChecked == true) tab = PanelTab.All;
        else if (TabManual.IsChecked == true) tab = PanelTab.ManualArchive;
        else if (TabExpired.IsChecked == true) tab = PanelTab.ExpiredArchive;

        _vm.SetTab(tab);
        ShowPane(tab);
    }

    /// <summary>Switches between the three mutually exclusive content panes.</summary>
    private void ShowPane(PanelTab tab)
    {
        var calendar = tab == PanelTab.Calendar;
        CalendarPane.Visibility = calendar ? Visibility.Visible : Visibility.Collapsed;
        ListPane.Visibility = calendar ? Visibility.Collapsed : Visibility.Visible;
        DetailPane.Visibility = Visibility.Collapsed;

        ItemList.SelectedItem = null;
        _current = null;

        EmptyHint.Visibility = !calendar && _vm.Items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ---------- list / detail ----------

    // Opening the detail pane is driven by a click on the row, not by ListBox selection —
    // otherwise pressing the archive button (which selects the row) would flash the detail view.
    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ItemVm vm)
            ShowDetail(vm);
    }

    // Must be Click (not preview mouse events): Click is what keyboard input and
    // UI Automation raise, so a preview-only handler would be unreachable for them.
    private void Archive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ItemVm vm) return;

        var id = vm.Model.Announcement.Id;
        var archive = !vm.Model.Announcement.IsManuallyArchived;
        _sync.SetManualArchived(id, archive);
    }

    private void ShowDetail(ItemVm vm)
    {
        _current = vm;
        DTitle.Text = vm.Title;
        DMeta.Text = vm.MetaLine;
        DDeadline.Text = vm.DeadlineLine;
        DBody.Text = vm.Body;

        DAction.Text = vm.HasAction ? "→ " + vm.ActionLine : "";
        DAction.Visibility = vm.HasAction ? Visibility.Visible : Visibility.Collapsed;

        DConstraints.Text = vm.HasConstraints ? vm.ConstraintsLine : "";
        DConstraints.Visibility = vm.HasConstraints ? Visibility.Visible : Visibility.Collapsed;

        DSource.Text = vm.AnalyzeSource;

        DetailPane.Visibility = Visibility.Visible;
        ListPane.Visibility = Visibility.Collapsed;
        CalendarPane.Visibility = Visibility.Collapsed;
    }

    private void ShowList()
    {
        _current = null;
        ItemList.SelectedItem = null;
        DetailPane.Visibility = Visibility.Collapsed;
        CalendarPane.Visibility = Visibility.Collapsed;
        ListPane.Visibility = Visibility.Visible;
        EmptyHint.Visibility = _vm.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- calendar ----------

    private void CalPrev_Click(object sender, RoutedEventArgs e) => _calendar.PreviousMonth();

    private void CalNext_Click(object sender, RoutedEventArgs e) => _calendar.NextMonth();

    private void CalToday_Click(object sender, RoutedEventArgs e) => _calendar.GoToday();

    private void Day_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is CalendarDayVm day)
            _calendar.SelectDate(day.Date);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowList();

    private void OpenBb_Click(object sender, RoutedEventArgs e)
    {
        var url = _current?.AnnouncementUrl;
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开浏览器失败", ex);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DetailPane.Visibility == Visibility.Visible) ShowList();
            else Hide();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing the panel only hides it; the tray icon owns the app lifetime.
        if (!App.IsShuttingDown)
        {
            e.Cancel = true;
            SavePosition();
            Hide();
        }
        base.OnClosing(e);
    }

    public void SavePosition()
    {
        _settings.PanelWidth = Width;
        _settings.PanelHeight = Height;

        // WPF's Left/Top are DIPs and unreliable under per-monitor DPI, so read the native rect.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && Native.GetWindowRect(hwnd, out var wr))
        {
            double scaleX = (wr.Right - wr.Left) / Math.Max(1.0, Width);
            double scaleY = (wr.Bottom - wr.Top) / Math.Max(1.0, Height);
            _settings.PanelLeft = wr.Left / scaleX;
            _settings.PanelTop = wr.Top / scaleY;
        }
        else
        {
            _settings.PanelLeft = Left;
            _settings.PanelTop = Top;
        }
        SettingsStore.Save(_settings);
    }

    /// <summary>Development aid: opens the detail pane for the first visible card.</summary>
    public void ShowFirstItemDetail()
    {
        Reload();
        if (_vm.Items.Count > 0)
        {
            ItemList.SelectedIndex = 0;
            ShowDetail(_vm.Items[0]);
        }
    }
}
