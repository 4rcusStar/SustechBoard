namespace SustechBoard.Core;

/// <summary>Pushes new items to a chat webhook (WeCom bot or a generic JSON endpoint).</summary>
public static class Notifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static bool IsWeCom(string url) =>
        url.Contains("qyapi.weixin.qq.com", StringComparison.OrdinalIgnoreCase);

    public static async Task<bool> SendWebhookAsync(string url, string markdown, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        try
        {
            object payload = IsWeCom(url)
                ? new { msgtype = "markdown", markdown = new { content = markdown } }
                : new { title = "SustechBoard", text = markdown };

            using var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8, "application/json");

            using var resp = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"webhook HTTP {(int)resp.StatusCode}: {body}");
                return false;
            }

            // WeCom reports logical failures with HTTP 200 + errcode.
            if (IsWeCom(url) && !body.Contains("\"errcode\":0", StringComparison.Ordinal))
            {
                Log.Warn("webhook 返回错误: " + body);
                return false;
            }

            Log.Info("webhook 推送成功");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("webhook 推送失败", ex);
            return false;
        }
    }

    public static string BuildMarkdown(IReadOnlyList<PanelItem> items, DateTimeOffset now)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"**SustechBoard · {now:MM-dd HH:mm}**  ");
        sb.AppendLine($"检测到 {items.Count} 条更新");
        sb.AppendLine();

        foreach (var it in items.Take(8))
        {
            var tag = it.DaysLeft switch
            {
                null => "",
                0 => "[今天] ",
                > 0 => $"[剩{it.DaysLeft}天] ",
                _ => "[已过期] "
            };
            var color = it.Priority == Priority.P0 ? "<font color=\"warning\">" : "";
            var end = it.Priority == Priority.P0 ? "</font>" : "";

            sb.AppendLine($"{color}{tag}{Escape(it.Announcement.Title)}{end}");
            sb.AppendLine($"　{it.Announcement.CourseName}");
            if (!string.IsNullOrWhiteSpace(it.Analysis.Summary))
                sb.AppendLine($"　{Truncate(it.Analysis.Summary, 60)}");
        }

        if (items.Count > 8) sb.AppendLine($"…另有 {items.Count - 8} 条");
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
