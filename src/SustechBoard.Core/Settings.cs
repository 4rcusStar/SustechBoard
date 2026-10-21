using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SustechBoard.Core;

public sealed class AppSettings
{
    public const string DefaultService =
        "https://bb.sustech.edu.cn/webapps/bb-sso-BBLEARN/index.jsp";

    public string Username { get; set; } = "";
    public string CasServiceUrl { get; set; } = DefaultService;

    /// <summary>Minutes between automatic refreshes.</summary>
    public int RefreshIntervalMinutes { get; set; } = 180;

    /// <summary>When true, automatic refreshes only happen between DayStartHour and DayEndHour.</summary>
    public bool RestrictToDayWindow { get; set; } = true;
    public int DayStartHour { get; set; } = 8;
    public int DayEndHour { get; set; } = 23;

    public bool NotifyDesktop { get; set; } = true;
    public bool NotifyWebhook { get; set; } = false;
    public string WebhookUrl { get; set; } = "";

    public bool LlmEnabled { get; set; } = true;
    public string LlmBaseUrl { get; set; } = "https://api.deepseek.com/v1";
    public string LlmModel { get; set; } = "deepseek-chat";

    /// <summary>Also crawl course content areas (e.g. 学习评价 / 我的作业) besides announcements.</summary>
    public bool ContentScanEnabled { get; set; } = true;

    /// <summary>Days between automatic full rescans of every course menu.</summary>
    public int FullScanIntervalDays { get; set; } = 7;

    /// <summary>Upper bound on content areas fetched per course per sync.</summary>
    public int MaxContentAreasPerCourse { get; set; } = 12;

    /// <summary>Upper bound on content areas probed during one discovery run, per course.</summary>
    public int MaxProbesPerCourse { get; set; } = 30;

    public bool LaunchAtStartup { get; set; } = false;

    /// <summary>Days considered "urgent" (P0).</summary>
    public int P0WindowDays { get; set; } = 3;
    /// <summary>Days considered "soon" (P1).</summary>
    public int P1WindowDays { get; set; } = 14;

    public double PanelWidth { get; set; } = 392;
    public double PanelHeight { get; set; } = 560;
    public double PanelLeft { get; set; } = -1;
    public double PanelTop { get; set; } = -1;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

public sealed class Secrets
{
    public string Password { get; set; } = "";
    public string LlmApiKey { get; set; } = "";
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile, Encoding.UTF8);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (s is not null) return s;
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取设置失败，使用默认值", ex);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(AppPaths.SettingsFile,
                JsonSerializer.Serialize(settings, JsonOpts), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Error("保存设置失败", ex);
        }
    }

    public static Secrets LoadSecrets()
    {
        try
        {
            if (!File.Exists(AppPaths.SecretsFile)) return new Secrets();
            var encrypted = File.ReadAllBytes(AppPaths.SecretsFile);
            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Secrets>(Encoding.UTF8.GetString(plain), JsonOpts)
                   ?? new Secrets();
        }
        catch (Exception ex)
        {
            Log.Error("读取凭据失败（可能换了 Windows 账户）", ex);
            return new Secrets();
        }
    }

    public static void SaveSecrets(Secrets secrets)
    {
        try
        {
            AppPaths.EnsureCreated();
            var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(secrets, JsonOpts));
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(AppPaths.SecretsFile, encrypted);
        }
        catch (Exception ex)
        {
            Log.Error("保存凭据失败", ex);
        }
    }
}
