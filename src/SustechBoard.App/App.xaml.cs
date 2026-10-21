using System.Windows;
using System.Windows.Threading;
using SustechBoard.Core;
using WinForms = System.Windows.Forms;

namespace SustechBoard.App;

public partial class App : Application
{
    public static bool IsShuttingDown { get; private set; }

    private Mutex? _instanceMutex;
    private SyncService? _sync;
    private MainWindow? _panel;
    private WinForms.NotifyIcon? _tray;
    private DispatcherTimer? _timer;
    private AppSettings _settings = new();
    private Secrets _secrets = new();
    private DateTimeOffset _lastSyncAt = DateTimeOffset.MinValue;
    private bool _enriching;
    private bool _coursePrompted;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Development-only: exercise the autostart registration without the UI.
        if (e.Args.Any(a => a.Equals("--dev-startup-on", StringComparison.OrdinalIgnoreCase)))
        {
            StartupManager.Set(true);
            Shutdown();
            return;
        }
        if (e.Args.Any(a => a.Equals("--dev-startup-off", StringComparison.OrdinalIgnoreCase)))
        {
            StartupManager.Set(false);
            Shutdown();
            return;
        }

        _instanceMutex = new Mutex(true, @"Global\SustechBoard_SingleInstance", out var isNew);
        if (!isNew)
        {
            // Another instance owns the tray; just reveal it.
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("未处理异常", args.Exception);
            args.Handled = true;
        };

        AppPaths.EnsureCreated();
        _settings = SettingsStore.Load();
        _secrets = SettingsStore.LoadSecrets();
        _sync = new SyncService(_settings, _secrets);

        _panel = new MainWindow(_sync, _settings);
        _panel.RefreshRequested += () => DoSyncAsync(false);
        _panel.SettingsRequested += OpenSettings;

        // Development-only: "-dev-size 780x760" sizes the panel through WPF (not Win32).
        for (int i = 0; i < e.Args.Length - 1; i++)
        {
            if (!e.Args[i].Equals("--dev-size", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = e.Args[i + 1].Split('x', 'X');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], out var dw) &&
                double.TryParse(parts[1], out var dh))
            {
                _panel.Width = dw;
                _panel.Height = dh;
            }
        }

        _panel.Show();

        CreateTray();

        var startHidden = e.Args.Any(a =>
            a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        if (startHidden) _panel.Hide();

        _sync.LoadCached();

        // Development-only verification hooks.
        if (e.Args.Any(a => a.Equals("--dev-detail", StringComparison.OrdinalIgnoreCase)))
            Dispatcher.BeginInvoke(new Action(() => _panel.ShowFirstItemDetail()),
                                   DispatcherPriority.Loaded);

        if (e.Args.Any(a => a.Equals("--dev-settings", StringComparison.OrdinalIgnoreCase)))
            _ = Dispatcher.BeginInvoke(new Action(OpenSettings), DispatcherPriority.Background);

        if (string.IsNullOrWhiteSpace(_settings.Username) || _secrets.Password.Length == 0)
        {
            _panel.Show();
            OpenSettings();
        }

        _ = InitialSyncAsync();
        StartTimer();
    }

    // ---------- tray ----------

    private void CreateTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = IconFactory.Create(),
            Visible = true,
            Text = "SustechBoard"
        };

        var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("显示面板", null, (_, _) => ShowPanel());
        menu.Items.Add("立即刷新", null, async (_, _) => await DoSyncAsync(false));
        menu.Items.Add("重新扫描课程结构", null, async (_, _) =>
        {
            _sync?.RequestFullScan();
            await DoSyncAsync(false);
        });
        menu.Items.Add("设置", null, (_, _) => OpenSettings());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => RequestShutdown());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowPanel();
    }

    private void ShowPanel()
    {
        if (_panel is null) return;
        _panel.WindowState = WindowState.Normal;
        _panel.BringToFront();
    }

    // ---------- sync ----------

    private async Task InitialSyncAsync()
    {
        try
        {
            await DoSyncAsync(true);
        }
        catch (Exception ex)
        {
            Log.Error("首次同步失败", ex);
        }
    }

    private async Task DoSyncAsync(bool isFirst)
    {
        if (_sync is null) return;

        _lastSyncAt = DateTimeOffset.Now;
        var result = await _sync.RunAsync(isFirst);

        if (!result.Success)
        {
            if (!isFirst) Notify("刷新失败", result.Error ?? "未知错误");
            return;
        }

        if (!isFirst && result.NewOrChanged.Count > 0)
        {
            var pending = _sync.Items.Count(i => i.Priority is Priority.P0 or Priority.P1 or Priority.P2);
            if (_settings.NotifyDesktop)
                Notify("SustechBoard", $"发现 {result.NewOrChanged.Count} 条更新 · 当前 {pending} 项待办");
        }

        // First usable sync: ask which courses are actually active. Teachers rarely close a
        // course site, so this is the moment to drop last term's leftovers.
        if (!_coursePrompted && _sync.NeedsCourseSelection && _sync.GetCourses().Count > 0)
        {
            _coursePrompted = true;
            Log.Info("首次同步完成，请选择生效课程");
            _ = Dispatcher.BeginInvoke(new Action(OpenSettings), DispatcherPriority.Background);
        }

        _ = EnrichInBackgroundAsync();
    }

    private async Task EnrichInBackgroundAsync()
    {
        if (_sync is null || _enriching || !_sync.LlmActive) return;
        _enriching = true;
        try
        {
            // Drain the backlog in bounded rounds so a first run finishes in a minute
            // instead of dribbling 12 items per sync cycle across many hours.
            var total = 0;
            for (int round = 0; round < 10; round++)
            {
                var n = await _sync.EnrichAsync(12);
                if (n == 0) break;
                total += n;
                await Task.Delay(800);
            }
            if (total > 0) Log.Info($"智能分析累计完成 {total} 条");
        }
        catch (Exception ex)
        {
            Log.Error("智能分析失败", ex);
        }
        finally
        {
            _enriching = false;
        }
    }

    private void StartTimer()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += (_, _) => OnTimerTick();
        _timer.Start();
    }

    private void OnTimerTick()
    {
        if (_sync is null || _sync.IsSyncing) return;

        var now = DateTimeOffset.Now;
        var interval = TimeSpan.FromMinutes(Math.Max(10, _settings.RefreshIntervalMinutes));
        if (now - _lastSyncAt < interval) return;

        if (_settings.RestrictToDayWindow &&
            (now.Hour < _settings.DayStartHour || now.Hour >= _settings.DayEndHour))
            return;

        _ = DoSyncAsync(false);
    }

    // ---------- settings ----------

    private void OpenSettings()
    {
        // The dialog mutates the settings object in place, so the previous account must be
        // captured before it opens — afterwards the old value is gone.
        var beforeUser = _settings.Username.Trim();

        var courses = _sync?.GetCourses() ?? new List<CourseRecord>();
        var dialog = new SettingsWindow(_settings, _secrets, courses) { Topmost = true };
        var ok = dialog.ShowDialog();

        if (ok != true || !dialog.Saved) return;

        _sync?.UpdateSettings(_settings, _secrets);
        _sync?.ApplyCourseSelection(dialog.CourseSelection);

        var afterUser = _settings.Username.Trim();
        if (!string.Equals(beforeUser, afterUser, StringComparison.Ordinal))
        {
            Log.Info($"账号已由 {beforeUser} 更换为 {afterUser}，清空本地数据");
            _sync?.ResetForAccountChange();
        }

        _panel?.ApplyDisplayMode();
        _panel?.Reload();
        _ = DoSyncAsync(false);
    }

    private void Notify(string title, string text)
    {
        try
        {
            _tray?.ShowBalloonTip(6000, title, text, WinForms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error("通知失败", ex);
        }
    }

    // ---------- shutdown ----------

    public void RequestShutdown()
    {
        IsShuttingDown = true;
        _timer?.Stop();
        try { _panel?.SavePosition(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _sync?.Dispose(); } catch { }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsShuttingDown = true;
        try { _tray?.Dispose(); } catch { }
        try { _sync?.Dispose(); } catch { }
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
