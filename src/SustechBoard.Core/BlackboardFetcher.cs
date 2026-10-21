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

    /// <summary>
    /// Parses the course menu out of any course page. Blackboard renders it as
    /// li[id^=paletteItem] entries where class "subhead" is a group label (no link),
    /// "divider" is a rule, and everything else carries an &lt;a&gt;.
    /// </summary>
    public List<MenuEntry> ParseCourseMenu(string html)
    {
        var result = new List<MenuEntry>();
        if (string.IsNullOrWhiteSpace(html)) return result;

        var doc = _parser.ParseDocument(html);
        var group = "";

        foreach (var li in doc.QuerySelectorAll("li[id^='paletteItem']"))
        {
            var cls = li.ClassName ?? "";
            if (cls.Contains("divider", StringComparison.OrdinalIgnoreCase)) continue;

            if (cls.Contains("subhead", StringComparison.OrdinalIgnoreCase))
            {
                var span = li.QuerySelector("h3 span[title]");
                group = System.Net.WebUtility.HtmlDecode(
                    span?.GetAttribute("title")?.Trim() ?? Collapse(li.TextContent));
                continue;
            }

            var a = li.QuerySelector("a[href]");
            if (a is null) continue;

            var href = (a.GetAttribute("href") ?? "").Trim();
            if (href.Length == 0 || href == "#") continue;

            var labelSpan = a.QuerySelector("span[title]");
            var label = System.Net.WebUtility.HtmlDecode(
                labelSpan?.GetAttribute("title")?.Trim() ?? Collapse(a.TextContent));
            if (label.Length == 0) continue;

            var isContent = href.Contains("listContent.jsp", StringComparison.OrdinalIgnoreCase);
            var absolute = ToAbsolute(href);

            if (result.All(m => m.Url != absolute))
                result.Add(new MenuEntry(label, group, absolute, isContent));
        }

        return result;
    }

    /// <summary>
    /// Blackboard emits root-relative hrefs on live pages (but absolute ones in saved
    /// snapshots), so anything non-absolute is resolved against the Blackboard base.
    /// </summary>
    private static string ToAbsolute(string href)
    {
        if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return href;

        if (href.StartsWith("//", StringComparison.Ordinal))
            return "https:" + href;

        return href.StartsWith('/')
            ? BlackboardClient.BbBase + href
            : BlackboardClient.BbBase + "/" + href;
    }

    /// <summary>
    /// Fetches one course page and returns its parsed menu.
    /// Returns null when the request failed, so callers can tell "fetch broke" from
    /// "this course really has no menu" — mistaking the former for the latter wipes good data.
    /// </summary>
    public async Task<List<MenuEntry>?> GetCourseMenuAsync(CourseInfo course, CancellationToken ct = default)
    {
        var url = BlackboardClient.BbBase + "/webapps/blackboard/execute/announcement" +
                  "?method=search&context=course_entry" +
                  $"&course_id={Uri.EscapeDataString(course.Id)}" +
                  "&handle=announcements_entry&mode=view";

        var html = await _client.GetStringAsync(url, ct).ConfigureAwait(false);
        if (html is null) return null;

        return ParseCourseMenu(html);
    }

    /// <summary>
    /// Reads every announcement of one course site.
    /// Returns <c>null</c> when the request failed — distinct from an empty list
    /// ("this course genuinely has no announcements"), which callers need to tell apart
    /// before deleting stale rows.
    /// </summary>
    public async Task<List<Announcement>?> GetAnnouncementsAsync(CourseInfo course, CancellationToken ct = default)
        => (await GetCourseAnnouncementsAsync(course, ct).ConfigureAwait(false)).Items;

    /// <summary>
    /// One request, two payloads: the course menu (for cheap structure-change detection)
    /// and the announcements themselves. Items is null when the fetch failed.
    /// </summary>
    public async Task<(List<MenuEntry> Menu, List<Announcement>? Items)> GetCourseAnnouncementsAsync(
        CourseInfo course, CancellationToken ct = default)
    {
        var url = BlackboardClient.BbBase + "/webapps/blackboard/execute/announcement" +
                  "?method=search&context=course_entry" +
                  $"&course_id={Uri.EscapeDataString(course.Id)}" +
                  "&handle=announcements_entry&mode=view";

        var html = await _client.GetStringAsync(url, ct).ConfigureAwait(false);
        if (html is null)
        {
            Log.Warn($"  {course.Name}: 公告抓取失败");
            return (new List<MenuEntry>(), null);
        }

        return (ParseCourseMenu(html), ParseAnnouncements(html, course));
    }

    private List<Announcement> ParseAnnouncements(string html, CourseInfo course)
    {
        var doc = _parser.ParseDocument(html);
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
                Origin = "announcement",
                FirstSeen = now,
                LastChanged = now
            });
        }

        Log.Info($"  {course.Name}: {items.Count} 条公告");
        return items;
    }

    /// <summary>
    /// Parses one content area (e.g. 学习评价 / 我的作业). Content items carry no publish
    /// time, so the deadline has to come from the body text — which is exactly what the
    /// analysis layer extracts.
    /// </summary>
    public List<Announcement> ParseContentList(string html, CourseInfo course, string sourceMenu)
    {
        var doc = _parser.ParseDocument(html);
        var items = new List<Announcement>();
        var now = DateTimeOffset.Now;

        foreach (var li in doc.QuerySelectorAll("li[id^='contentListItem']"))
        {
            var raw = li.Id ?? "";
            var id = raw.StartsWith("contentListItem:", StringComparison.Ordinal)
                ? raw["contentListItem:".Length..]
                : raw;
            if (!ItemIdRegex.IsMatch(id)) continue;

            var a = li.QuerySelector("h3 a[href]");
            if (a is null) continue;

            var title = Collapse(a.TextContent);
            if (title.Length == 0) continue;

            var kind = li.QuerySelector("img.item_icon")?.GetAttribute("alt")?.Trim() ?? "";

            // Inline <script> blobs sit inside the details block; left in, they eat the whole
            // character budget and push the real "Due: …" line out of the captured text.
            foreach (var junk in li.QuerySelectorAll("script, style").ToList())
                junk.Remove();

            var parts = new List<string>();
            if (kind.Length > 0) parts.Add($"【{kind}】");

            // The due date is not always inside div.vtbegenerated — Blackboard also renders it
            // as a detailsLabel/detailsValue pair ("Due: Monday, 14 September 2026").
            // Take the whole details block so nothing is missed, then cap for the model's sake.
            var detailsEl = li.QuerySelector("div.details");
            var textSource = detailsEl?.TextContent
                             ?? li.QuerySelector("div.vtbegenerated")?.TextContent
                             ?? "";

            var text = Collapse(textSource);
            if (text.Length > 800) text = text[..800];
            if (text.Length > 0) parts.Add(text);

            // Attachment names often carry the assignment title (hw3.pdf) — useful signal.
            var attachments = li.QuerySelectorAll("ul.attachments li a")
                .Select(x => Collapse(x.TextContent))
                .Where(x => x.Length > 0)
                .Take(6)
                .ToList();

            var body = string.Join(" ", parts);
            if (attachments.Count > 0) body += " 附件: " + string.Join("; ", attachments);
            if (body.Trim().Length == 0) body = title;

            items.Add(new Announcement
            {
                Id = id,
                CourseId = course.Id,
                CourseName = course.Name,
                Title = title,
                Body = body,
                PublishedAt = null,             // content items have no publish time
                Author = "",
                ContentHash = Hash.Sha256(course.Id + "|" + title + "|" + body),
                Origin = "content",
                SourceMenu = sourceMenu,
                ContentKind = kind,
                FirstSeen = now,
                LastChanged = now
            });
        }

        return items;
    }

    /// <summary>Fetches and parses one content area. Returns null when the request failed.</summary>
    public async Task<List<Announcement>?> FetchContentAreaAsync(
        CourseInfo course, CoursePath path, CancellationToken ct = default)
    {
        var html = await _client.GetStringAsync(path.Url, ct).ConfigureAwait(false);
        if (html is null) return null;
        return ParseContentList(html, course, path.Display);
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
