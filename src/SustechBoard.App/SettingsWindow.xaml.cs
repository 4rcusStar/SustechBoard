using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SustechBoard.Core;

namespace SustechBoard.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Secrets _secrets;

    public bool Saved { get; private set; }

    public SettingsWindow(AppSettings settings, Secrets secrets)
    {
        InitializeComponent();
        _settings = settings;
        _secrets = secrets;

        UserBox.Text = settings.Username;
        PasswordBox.Password = secrets.Password;

        IntervalBox.Text = settings.RefreshIntervalMinutes.ToString();

        DayOnlyCheck.IsChecked = settings.RestrictToDayWindow;
        StartHourBox.Text = settings.DayStartHour.ToString();
        EndHourBox.Text = settings.DayEndHour.ToString();

        DesktopNotifyCheck.IsChecked = settings.NotifyDesktop;
        WebhookCheck.IsChecked = settings.NotifyWebhook;
        WebhookBox.Text = settings.WebhookUrl;

        LlmCheck.IsChecked = settings.LlmEnabled;
        LlmBaseBox.Text = settings.LlmBaseUrl;
        LlmModelBox.Text = settings.LlmModel;
        LlmKeyBox.Password = secrets.LlmApiKey;

        StartupCheck.IsChecked = StartupManager.IsEnabled();

        if (settings.DisplayMode == PanelDisplayMode.AlwaysBottom)
            DispAlwaysBottom.IsChecked = true;
        else
            DispFocusTop.IsChecked = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.Username = UserBox.Text.Trim();
        _secrets.Password = PasswordBox.Password;   // verbatim: never trimmed
        _settings.RefreshIntervalMinutes = int.TryParse(IntervalBox.Text, out var mins)
            ? Math.Clamp(mins, 10, 1440)
            : 180;
        _settings.RestrictToDayWindow = DayOnlyCheck.IsChecked == true;
        _settings.DayStartHour = ClampHour(StartHourBox.Text, 8);
        _settings.DayEndHour = ClampHour(EndHourBox.Text, 23);

        _settings.NotifyDesktop = DesktopNotifyCheck.IsChecked == true;
        _settings.NotifyWebhook = WebhookCheck.IsChecked == true;
        _settings.WebhookUrl = WebhookBox.Text.Trim();

        _settings.LlmEnabled = LlmCheck.IsChecked == true;
        _settings.LlmBaseUrl = LlmBaseBox.Text.Trim();
        _settings.LlmModel = LlmModelBox.Text.Trim();
        _secrets.LlmApiKey = LlmKeyBox.Password.Trim();

        _settings.DisplayMode = DispAlwaysBottom.IsChecked == true
            ? PanelDisplayMode.AlwaysBottom
            : PanelDisplayMode.TopmostWhenFocused;

        var wantStartup = StartupCheck.IsChecked == true;
        if (wantStartup != StartupManager.IsEnabled()) StartupManager.Set(wantStartup);
        _settings.LaunchAtStartup = wantStartup;

        SettingsStore.Save(_settings);
        SettingsStore.SaveSecrets(_secrets);

        Saved = true;
        DialogResult = true;
        Close();
    }

    private static int ClampHour(string text, int fallback) =>
        int.TryParse(text, out var v) ? Math.Clamp(v, 0, 23) : fallback;

    private void Header_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    /// <summary>
    /// Probes the model with the values currently in the form (not the saved ones),
    /// so the key can be validated before it is persisted.
    /// </summary>
    private async void TestLlm_Click(object sender, RoutedEventArgs e)
    {
        TestLlmButton.IsEnabled = false;
        LlmTestStatus.Foreground = (Brush)FindResource("TextMuted");
        LlmTestStatus.Text = "正在请求…";
        try
        {
            var probeSettings = _settings.Clone();
            probeSettings.LlmEnabled = true;            // test even if the toggle is off
            probeSettings.LlmBaseUrl = LlmBaseBox.Text.Trim();
            probeSettings.LlmModel = LlmModelBox.Text.Trim();

            var probeSecrets = new Secrets { LlmApiKey = LlmKeyBox.Password.Trim() };

            if (string.IsNullOrWhiteSpace(probeSettings.LlmBaseUrl) ||
                string.IsNullOrWhiteSpace(probeSettings.LlmModel))
            {
                LlmTestStatus.Text = "请先填写接口地址和模型名";
                return;
            }
            if (probeSecrets.LlmApiKey.Length == 0)
            {
                LlmTestStatus.Text = "请先填写 API Key";
                return;
            }

            using var llm = new LlmAnalyzer(probeSettings, probeSecrets);
            var r = await llm.ProbeAsync();

            LlmTestStatus.Foreground = r.Ok
                ? (Brush)FindResource("Accent")
                : (Brush)FindResource("P0Brush");
            LlmTestStatus.Text = r.Ok
                ? $"✓ 工作正常（{r.ElapsedMs} ms）\n{r.Message}"
                : $"✗ {r.Message}";
        }
        catch (Exception ex)
        {
            LlmTestStatus.Foreground = (Brush)FindResource("P0Brush");
            LlmTestStatus.Text = "异常: " + ex.Message;
        }
        finally
        {
            TestLlmButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestStatus.Foreground = (Brush)FindResource("TextMuted");
        TestStatus.Text = "正在登录…";
        try
        {
            var probeSettings = _settings.Clone();
            probeSettings.Username = UserBox.Text.Trim();
            var probeSecrets = new Secrets
            {
                Password = PasswordBox.Password,
                LlmApiKey = LlmKeyBox.Password.Trim()
            };

            if (string.IsNullOrWhiteSpace(probeSettings.Username) || probeSecrets.Password.Length == 0)
            {
                TestStatus.Text = "请先填写账号和密码";
                return;
            }

            using var client = new BlackboardClient(probeSettings, probeSecrets);
            var r = await client.LoginAsync();
            TestStatus.Foreground = r.Success
                ? (Brush)FindResource("Accent")
                : (Brush)FindResource("P0Brush");
            TestStatus.Text = r.Success ? "✓ 登录成功" : "✗ " + r.Message;
        }
        catch (Exception ex)
        {
            TestStatus.Text = "异常: " + ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }
}
