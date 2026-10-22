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

    // The live SUSTech CAS (6.2, Thymeleaf + MDC) renders credential errors as:
    //   <div class="banner banner-danger banner-dismissible"><p>认证信息无效。</p></div>
    private static readonly Regex BannerMessageRegex =
        new("class=\"[^\"]*banner-danger[^\"]*\"[^>]*>\\s*<p>\\s*([^<]+?)\\s*</p>", RegexOptions.Compiled);

    // Older Apereo CAS templates used <div id="msg">message</div>;
    // keep it as a fallback so the message still shows up if the school reverts the template.
    private static readonly Regex LegacyMessageRegex =
        new("id=\"msg\"[^>]*>\\s*([^<]+)", RegexOptions.Compiled);

    private CookieContainer _cookies = new();
    private HttpClient _http = null!;
    private AppSettings _settings;
    private Secrets _secrets;

    public bool IsAuthenticated { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public string LastError { get; private set; } = "";

    /// <summary>Account the current session cookies belong to. Empty until a login succeeds.</summary>
    public string SessionUser { get; private set; } = "";

    public BlackboardClient(AppSettings settings, Secrets secrets)
    {
        _settings = settings;
        _secrets = secrets;
        _http = BuildHttp(out _cookies);
    }

    private static HttpClientHandler BuildHandler(out CookieContainer cookies)
    {
        cookies = new CookieContainer();
        return new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.All,
            MaxAutomaticRedirections = 12,
            // This tool only ever talks to two fixed campus hosts, so a proxy can only hurt.
            // A stale system proxy (left behind by a VPN client, an accelerator or a packet
            // sniffer) rewrites the CAS login exchange between the GET and the POST, which
            // shows up as a ticket-less "HTTP 200" from /cas/login. Always connect directly.
            UseProxy = false
        };
    }

    private static HttpClient BuildClient(HttpClientHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        return http;
    }

    private static HttpClient BuildHttp(out CookieContainer cookies) =>
        BuildClient(BuildHandler(out cookies));

    /// <summary>
    /// Drops every cookie and rebuilds the HTTP stack. Without this the previous account's
    /// Blackboard session survives the credential change, the client looks "already logged in",
    /// and every fetch keeps returning the old account's data until the app is restarted.
    /// </summary>
    public void ResetSession()
    {
        try { _http.Dispose(); } catch { /* already gone */ }
        _http = BuildHttp(out _cookies);
        IsAuthenticated = false;
        LastLoginAt = null;
        SessionUser = "";
        Log.Info("已重置登录会话（Cookie 清空）");
    }

    public void UpdateCredentials(AppSettings settings, Secrets secrets)
    {
        var requestedUser = settings.Username.Trim();

        _settings = settings;
        _secrets = secrets;

        // The settings object is mutated in place, so compare against who the cookies belong to.
        if (SessionUser.Length > 0 &&
            !string.Equals(SessionUser, requestedUser, StringComparison.Ordinal))
        {
            ResetSession();
        }
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
            var first = await AttemptLoginAsync(ct).ConfigureAwait(false);
            if (first.Result.Success) return first.Result;

            // A wrong password is a definitive answer: retrying would just burn another failed
            // attempt and edge the account towards CAS's lockout, so stop right here.
            if (first.CredentialsRejected)
            {
                Log.Warn("凭据被 CAS 拒绝，不再重试：" + first.Result.Message);
                return first.Result;
            }

            // Anything else is ambiguous, so retry once on a completely fresh session with
            // automatic redirects disabled, which also tells us whether CAS issued a ticket:
            //   * ticket     -> the account was accepted, Blackboard rejected it
            //   * no ticket  -> CAS never took the form (stale flow, rewritten request)
            Log.Info($"首次登录未通过（{first.Result.Message}），清空会话后重试");
            ResetSession();

            var raw = await RawLoginAsync(ct).ConfigureAwait(false);

            if (raw.TicketUrl.Length > 0)
            {
                Log.Info("诊断: CAS 已签发票据，继续完成 SSO");
                using var ticket = await _http.GetAsync(raw.TicketUrl, ct).ConfigureAwait(false);
                var ticketHtml = await ticket.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var ticketUri = ticket.RequestMessage?.RequestUri?.ToString() ?? "";

                if (ticketUri.Contains("/cas/login", StringComparison.OrdinalIgnoreCase) || LooksLikeLoginPage(ticketHtml))
                    return Fail("CAS 放行，但 Blackboard 拒绝该账号的 SSO 票据（Blackboard 里可能没有这个账号）");

                return await CompleteLoginAsync(ct).ConfigureAwait(false);
            }

            SaveFailurePage(raw);

            if (raw.Banner.Length > 0)
                return Fail($"CAS 拒绝: {raw.Banner}");

            Log.Warn(ProxyHint());
            return Fail("CAS 未受理登录（已自动重试）· 检查本机代理或抓包软件");
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

    /// <summary>Outcome of one login round-trip; <c>CredentialsRejected</c> means CAS said no outright.</summary>
    private sealed record AttemptOutcome(LoginResult Result, bool CredentialsRejected);

    /// <summary>One normal login round-trip: login page -> credentials -> Blackboard probe.</summary>
    private async Task<AttemptOutcome> AttemptLoginAsync(CancellationToken ct)
    {
        var loginUrl = LoginUrl;

        using var page = await _http.GetAsync(loginUrl, ct).ConfigureAwait(false);
        var html = await page.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (LooksLikeLoginPage(html) && !html.Contains("name=\"execution\"", StringComparison.Ordinal))
            return new AttemptOutcome(Fail("无法加载 CAS 登录页"), false);

        var m = ExecutionRegex.Match(html);
        if (!m.Success)
        {
            if (LooksLikeAuthenticatedPage(html))   // already logged in (stale TGC)
            {
                IsAuthenticated = true;
                LastLoginAt = DateTimeOffset.Now;
                SessionUser = _settings.Username.Trim();
                return new AttemptOutcome(new LoginResult(true, "已处于登录状态"), false);
            }
            return new AttemptOutcome(Fail("未取到 CAS execution token"), false);
        }

        using var resp = await PostCredentialsAsync(_http, loginUrl, m.Groups[1].Value, ct).ConfigureAwait(false);

        var finalUri = resp.RequestMessage?.RequestUri?.ToString() ?? "";
        var respHtml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if ((int)resp.StatusCode == 401 || finalUri.Contains("/cas/login", StringComparison.OrdinalIgnoreCase))
        {
            var msg = ExtractCasMessage(respHtml);
            var rejected = (int)resp.StatusCode == 401 || msg.Length > 0;
            return new AttemptOutcome(
                Fail($"CAS 拒绝: {(msg.Length > 0 ? msg : $"HTTP {(int)resp.StatusCode}")}"), rejected);
        }

        if (!resp.IsSuccessStatusCode && (int)resp.StatusCode is not 200)
            return new AttemptOutcome(Fail($"登录后返回 HTTP {(int)resp.StatusCode}"), false);

        return new AttemptOutcome(await CompleteLoginAsync(ct).ConfigureAwait(false), false);
    }

    /// <summary>Confirms the session by actually reaching Blackboard, then marks us logged in.</summary>
    private async Task<LoginResult> CompleteLoginAsync(CancellationToken ct)
    {
        using var probe = await _http.GetAsync($"{BbBase}/webapps/portal/execute/tabs/tabAction?tab_tab_group_id=_1_1", ct)
                                    .ConfigureAwait(false);
        var probeHtml = await probe.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var probeUri = probe.RequestMessage?.RequestUri?.ToString() ?? "";

        if (probeUri.Contains("/cas/login", StringComparison.OrdinalIgnoreCase) || LooksLikeLoginPage(probeHtml))
            return Fail("SSO 票据换取 Blackboard 会话失败");

        IsAuthenticated = true;
        LastLoginAt = DateTimeOffset.Now;
        SessionUser = _settings.Username.Trim();
        LastError = "";
        Log.Info("CAS 登录成功");
        return new LoginResult(true, "登录成功");
    }

    /// <summary>Result of a login attempt that must not follow redirects.</summary>
    private sealed record RawLogin(string TicketUrl, int Status, string Banner, string Html, string Location);

    /// <summary>
    /// Replays the login on a throwaway cookie jar with automatic redirects disabled, so the raw
    /// CAS response is visible: a 302 carrying "ticket=" means the credentials were accepted.
    /// </summary>
    private async Task<RawLogin> RawLoginAsync(CancellationToken ct)
    {
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = false
        };
        using var http = BuildClient(handler);

        var loginUrl = LoginUrl;

        using var page = await http.GetAsync(loginUrl, ct).ConfigureAwait(false);
        var html = await page.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var exec = ExecutionRegex.Match(html);
        if (!exec.Success)
            return new RawLogin("", (int)page.StatusCode, ExtractCasMessage(html), html, "");

        using var post = await PostCredentialsAsync(http, loginUrl, exec.Groups[1].Value, ct).ConfigureAwait(false);
        var status = (int)post.StatusCode;

        if (status is 301 or 302 or 303 or 307 or 308 && post.Headers.Location is { } loc)
        {
            var abs = loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(loginUrl), loc).ToString();
            var ticketed = abs.Contains("ticket=", StringComparison.OrdinalIgnoreCase);
            return new RawLogin(ticketed ? abs : "", status, "", "", abs);
        }

        var postHtml = await post.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new RawLogin("", status, ExtractCasMessage(postHtml), postHtml, "");
    }

    private async Task<HttpResponseMessage> PostCredentialsAsync(HttpClient http, string loginUrl, string execution,
                                                                 CancellationToken ct)
    {
        // Password is used verbatim: no Trim, no normalization.
        var body = "username=" + Uri.EscapeDataString(_settings.Username)
                 + "&password=" + Uri.EscapeDataString(_secrets.Password)
                 + "&execution=" + Uri.EscapeDataString(execution)
                 + "&_eventId=submit&geolocation=";

        using var req = new HttpRequestMessage(HttpMethod.Post, loginUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        return await http.SendAsync(req, ct).ConfigureAwait(false);
    }

    private static string ExtractCasMessage(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        var m = BannerMessageRegex.Match(html);
        if (!m.Success) m = LegacyMessageRegex.Match(html);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static void SaveFailurePage(RawLogin raw)
    {
        try
        {
            if (raw.Html.Length == 0) return;

            AppPaths.EnsureCreated();
            var text = raw.Html.Length > 200_000 ? raw.Html[..200_000] : raw.Html;
            File.WriteAllText(AppPaths.CasFailureFile, text, Encoding.UTF8);
            Log.Warn($"已保存 CAS 失败页面（HTTP {raw.Status}，跳转 {raw.Location}）: {AppPaths.CasFailureFile}");
        }
        catch (Exception ex)
        {
            Log.Error("保存 CAS 失败页面失败", ex);
        }
    }

    private static string ProxyHint()
    {
        var hint = "CAS 返回的是登录页而不是票据：登录请求多半被本机代理 / 抓包软件改写，或登录页已过期。";
        try
        {
            var proxy = HttpClient.DefaultProxy;
            var addr = proxy?.GetProxy(new Uri(CasBase));
            if (addr is not null && !addr.ToString().Contains("cas.sustech.edu.cn", StringComparison.OrdinalIgnoreCase))
                hint += $" 检测到系统代理 {addr}，本程序已改为直连。";
        }
        catch
        {
            // Proxy detection is best-effort only.
        }
        return hint;
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
