using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace SustechBoard.Core;

public sealed record LoginResult(bool Success, string Message);

/// <summary>
/// Talks to SUSTech CAS + Blackboard using the exact flow verified against the live site:
///   GET  /cas/login?service=...        -> page containing a hidden "execution" token
///   POST /cas/login?service=...        -> 302 with Location ...?ticket=ST-...
///   GET  ...?ticket=ST-...             -> sets Blackboard session cookies
/// CAS itself is stateless (no cookies), so every refresh can simply re-authenticate.
/// </summary>
public sealed class BlackboardClient : IDisposable
{
    public const string CasBase = "https://cas.sustech.edu.cn";
    public const string BbBase = "https://bb.sustech.edu.cn";

    private static readonly Regex ExecutionRegex =
        new("name=\"execution\"\\s+value=\"([^\"]+)\"", RegexOptions.Compiled);

    private static readonly Regex CasMessageRegex =
        new("id=\"msg\"[^>]*>\\s*([^<]+)", RegexOptions.Compiled);

    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private AppSettings _settings;
    private Secrets _secrets;

    public bool IsAuthenticated { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public string LastError { get; private set; } = "";

    public BlackboardClient(AppSettings settings, Secrets secrets)
    {
        _settings = settings;
        _secrets = secrets;

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = _cookies,
            AutomaticDecompression = DecompressionMethods.All,
            MaxAutomaticRedirections = 12
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
    }

    public void UpdateCredentials(AppSettings settings, Secrets secrets)
    {
        _settings = settings;
        _secrets = secrets;
    }

    public string LoginUrl =>
        $"{CasBase}/cas/login?service={Uri.EscapeDataString(_settings.CasServiceUrl)}";

    /// <summary>Performs a full CAS login. Never throws for bad credentials.</summary>
    public async Task<LoginResult> LoginAsync(CancellationToken ct = default)
    {
        IsAuthenticated = false;

        if (string.IsNullOrWhiteSpace(_settings.Username) || string.IsNullOrWhiteSpace(_secrets.Password))
            return Fail("未配置账号或密码");

        try
        {
            var loginUrl = LoginUrl;

            using var page = await _http.GetAsync(loginUrl, ct).ConfigureAwait(false);
            var html = await page.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (LooksLikeLoginPage(html) && !html.Contains("name=\"execution\"", StringComparison.Ordinal))
                return Fail("无法加载 CAS 登录页");

            var m = ExecutionRegex.Match(html);
            if (!m.Success)
            {
                if (LooksLikeAuthenticatedPage(html))   // already logged in (stale TGC)
                {
                    IsAuthenticated = true;
                    LastLoginAt = DateTimeOffset.Now;
                    return new LoginResult(true, "已处于登录状态");
                }
                return Fail("未取到 CAS execution token");
            }

            var execution = m.Groups[1].Value;

            // Password is used verbatim: no Trim, no normalization.
            var body = "username=" + Uri.EscapeDataString(_settings.Username)
                     + "&password=" + Uri.EscapeDataString(_secrets.Password)
                     + "&execution=" + Uri.EscapeDataString(execution)
                     + "&_eventId=submit&geolocation=";

            using var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
            using var resp = await _http.PostAsync(loginUrl, content, ct).ConfigureAwait(false);

            var finalUri = resp.RequestMessage?.RequestUri?.ToString() ?? "";
            var respHtml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if ((int)resp.StatusCode == 401 || finalUri.Contains("/cas/login", StringComparison.OrdinalIgnoreCase))
            {
                var msg = CasMessageRegex.Match(respHtml) is { Success: true } mm
                    ? mm.Groups[1].Value.Trim()
                    : $"HTTP {(int)resp.StatusCode}";
                return Fail($"CAS 拒绝: {msg}");
            }

            if (!resp.IsSuccessStatusCode && (int)resp.StatusCode is not 200)
                return Fail($"登录后返回 HTTP {(int)resp.StatusCode}");

            // Success is confirmed by actually reaching Blackboard.
            using var probe = await _http.GetAsync($"{BbBase}/webapps/portal/execute/tabs/tabAction?tab_tab_group_id=_1_1", ct)
                                        .ConfigureAwait(false);
            var probeHtml = await probe.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var probeUri = probe.RequestMessage?.RequestUri?.ToString() ?? "";

            if (probeUri.Contains("/cas/login", StringComparison.OrdinalIgnoreCase) || LooksLikeLoginPage(probeHtml))
                return Fail("SSO 票据换取 Blackboard 会话失败");

            IsAuthenticated = true;
            LastLoginAt = DateTimeOffset.Now;
            LastError = "";
            Log.Info("CAS 登录成功");
            return new LoginResult(true, "登录成功");
        }
        catch (OperationCanceledException)
        {
            return Fail("登录超时");
        }
        catch (Exception ex)
        {
            Log.Error("登录异常", ex);
            return Fail($"登录异常: {ex.Message}");
        }
    }

    private LoginResult Fail(string msg)
    {
        IsAuthenticated = false;
        LastError = msg;
        Log.Warn("登录失败: " + msg);
        return new LoginResult(false, msg);
    }

    /// <summary>GET with automatic single-shot re-authentication when the session expired.</summary>
    public async Task<string?> GetStringAsync(string url, CancellationToken ct = default)
    {
        var html = await TryGetAsync(url, ct).ConfigureAwait(false);
        if (html is null) return null;

        if (LooksLikeLoginPage(html))
        {
            var r = await LoginAsync(ct).ConfigureAwait(false);
            if (!r.Success) return null;
            html = await TryGetAsync(url, ct).ConfigureAwait(false);
        }
        return html;
    }

    /// <summary>POST form-encoded with automatic single-shot re-authentication.</summary>
    public async Task<string?> PostFormAsync(string url, string formBody, CancellationToken ct = default,
                                             string? xhrHeader = null)
    {
        var html = await TryPostAsync(url, formBody, xhrHeader, ct).ConfigureAwait(false);
        if (html is null) return null;

        if (LooksLikeLoginPage(html))
        {
            var r = await LoginAsync(ct).ConfigureAwait(false);
            if (!r.Success) return null;
            html = await TryPostAsync(url, formBody, xhrHeader, ct).ConfigureAwait(false);
        }
        return html;
    }

    private async Task<string?> TryGetAsync(string url, CancellationToken ct)
    {
        try
        {
            await PolitenessDelayAsync(ct).ConfigureAwait(false);
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode && (int)resp.StatusCode is not 200) return null;
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("GET 失败 " + url, ex);
            return null;
        }
    }

    private async Task<string?> TryPostAsync(string url, string formBody, string? xhrHeader, CancellationToken ct)
    {
        try
        {
            await PolitenessDelayAsync(ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded")
            };
            if (xhrHeader is not null)
                req.Headers.TryAddWithoutValidation("X-Requested-With", xhrHeader);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode && (int)resp.StatusCode is not 200) return null;
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("POST 失败 " + url, ex);
            return null;
        }
    }

    private static async Task PolitenessDelayAsync(CancellationToken ct)
    {
        try { await Task.Delay(250, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    public static bool LooksLikeLoginPage(string html) =>
        html.Contains("name=\"execution\"", StringComparison.Ordinal) &&
        html.Contains("Central Authentication Service", StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeAuthenticatedPage(string html) =>
        html.Contains("欢迎", StringComparison.Ordinal) ||
        html.Contains("Blackboard Learn", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("logout", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _http.Dispose();
}
