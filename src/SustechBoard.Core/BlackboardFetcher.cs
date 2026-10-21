using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace SustechBoard.Core;

/// <summary>Fetches and parses Blackboard course sites and their announcements.</summary>
public sealed class BlackboardFetcher
{
    private readonly BlackboardClient _client;
    private readonly HtmlParser _parser = new();

    private static readonly Regex CourseIdRegex = new(@"id=(_[0-9]+_1)", RegexOptions.Compiled);
    private static readonly Regex ItemIdRegex = new(@"^_\d+_1$", RegexOptions.Compiled);
    private static readonly Regex PublishedRegex = new(
        "(?:发布时间|Posted\\s+on)\\s*[:：]?\\s*([^<\\r\\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public BlackboardFetcher(BlackboardClient client) => _client = client;

    /// <summary>Reads the enrolled-course list from the "我的课程" module.</summary>
    public async Task<List<CourseInfo>> GetCoursesAsync(CancellationToken ct = default)
    {
        const string url = BlackboardClient.BbBase + "/webapps/portal/execute/tabs/tabAction";
        const string body =
            "action=refreshAjaxModule&modId=_3_1&tabId=_1_1&tab_tab_group_id=_1_1";

        var raw = await _client.PostFormAsync(url, body, ct, xhrHeader: "XMLHttpRequest")
                               .ConfigureAwait(false);
        if (raw is null) return new List<CourseInfo>();

        var html = StripCdata(raw);
        var doc = await _parser.ParseDocumentAsync(html, ct).ConfigureAwait(false);

        var result = new List<CourseInfo>();
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href") ?? "";
            if (!href.Contains("launcher?type=Course", StringComparison.OrdinalIgnoreCase)) continue;

            var m = CourseIdRegex.Match(href);
            if (!m.Success) continue;

            var name = Collapse(a.TextContent);
            if (name.Length == 0) continue;

            var id = m.Groups[1].Value;
            if (result.All(c => c.Id != id))
                result.Add(new CourseInfo(id, name));
        }

        Log.Info($"发现 {result.Count} 门课程");
        return result;
    }

    /// <summary>Reads every announcement of one course site.</summary>
    public async Task<List<Announcement>> GetAnnouncementsAsync(CourseInfo course, CancellationToken ct = default)
    {
        var url = BlackboardClient.BbBase + "/webapps/blackboard/execute/announcement" +
                  "?method=search&context=course_entry" +
                  $"&course_id={Uri.EscapeDataString(course.Id)}" +
                  "&handle=announcements_entry&mode=view";

        var html = await _client.GetStringAsync(url, ct).ConfigureAwait(false);
        if (html is null) return new List<Announcement>();

        var doc = await _parser.ParseDocumentAsync(html, ct).ConfigureAwait(false);
        var items = new List<Announcement>();
        var now = DateTimeOffset.Now;

        foreach (var li in doc.QuerySelectorAll("li.clearfix"))
        {
            var rawId = li.Id ?? "";
            if (!ItemIdRegex.IsMatch(rawId)) continue;

            var titleEl = li.QuerySelector("h3.item") ?? li.QuerySelector("h3");
            if (titleEl is null) continue;
            var title = Collapse(titleEl.TextContent);
            if (title.Length == 0) continue;

            var details = li.QuerySelector("div.details");
            var metaText = details?.TextContent ?? li.TextContent;
            var published = DateParsing.FromPublishedText(PublishedRegex.Match(metaText).Groups[1].Value);

            var bodyEl = li.QuerySelector("div.vtbegenerated");
            var body = bodyEl is not null ? Collapse(bodyEl.TextContent) : "";

            var author = "";
            var info = li.QuerySelector("div.announcementInfo");
            if (info is not null)
            {
                var am = Regex.Match(info.TextContent, "发帖者[:：]\\s*([^\\r\\n]+?)\\s*发布至");
                if (am.Success) author = Collapse(am.Groups[1].Value);
            }

            items.Add(new Announcement
            {
                Id = rawId,
                CourseId = course.Id,
                CourseName = course.Name,
                Title = title,
                Body = body,
                PublishedAt = published,
                Author = author,
                ContentHash = Hash.Sha256(course.Id + "|" + title + "|" + body),
                FirstSeen = now,
                LastChanged = now
            });
        }

        Log.Info($"  {course.Name}: {items.Count} 条公告");
        return items;
    }

    /// <summary>Blackboard wraps AJAX module payloads in an XML CDATA envelope.</summary>
    public static string StripCdata(string raw)
    {
        var start = raw.IndexOf("<![CDATA[", StringComparison.Ordinal);
        var end = raw.LastIndexOf("]]>", StringComparison.Ordinal);
        if (start >= 0 && end > start)
            return raw.Substring(start + 9, end - start - 9);
        return raw;
    }

    public static string Collapse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length);
        bool lastWasSpace = false;
        foreach (var ch in text)
        {
            bool isSpace = char.IsWhiteSpace(ch);
            if (isSpace)
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }
        return sb.ToString().Trim();
    }
}
