using System.Text.RegularExpressions;

namespace SustechBoard.Core;

public readonly record struct DeadlineHit(DateTimeOffset Value, double Confidence, bool ExplicitYear);

/// <summary>
/// Extracts a publish time and the most likely deadline from free-form announcement text.
/// Blackboard renders dates in Chinese on Chinese-language sites and in English
/// ("Posted on: Monday, October 19, 2026 8:31:10 PM CST") on English ones, so both are handled.
/// Year inference for year-less dates is anchored to the announcement's publish date, never "now".
/// </summary>
public static class DateParsing
{
    private static readonly TimeSpan Cst = TimeSpan.FromHours(8);

    // 2026年10月19日 星期一 下午08时31分10秒 CST
    private static readonly Regex PublishedCn = new(
        @"(\d{4})\s*年\s*(\d{1,2})\s*月\s*(\d{1,2})\s*日(?:[^\d]*(上午|下午))?[^\d]*?(\d{1,2})\s*时\s*(\d{1,2})\s*分(?:(\d{1,2})\s*秒)?",
        RegexOptions.Compiled);

    // Monday, October 19, 2026 8:31:10 PM CST
    private static readonly Regex PublishedEn = new(
        @"([A-Za-z]+)\.?\s+(\d{1,2}),?\s+(\d{4})\s+(\d{1,2}):(\d{2})(?::(\d{1,2}))?\s*(AM|PM)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 2026年10月19日
    private static readonly Regex CnYear = new(
        @"(\d{4})\s*年\s*(\d{1,2})\s*月\s*(\d{1,2})\s*日", RegexOptions.Compiled);

    // 10月25日
    private static readonly Regex CnNoYear = new(
        @"(?<!\d)(\d{1,2})\s*月\s*(\d{1,2})\s*日", RegexOptions.Compiled);

    // 2026-10-19
    private static readonly Regex IsoDate = new(
        @"(?<!\d)(\d{4})-(\d{1,2})-(\d{1,2})(?!\d)", RegexOptions.Compiled);

    // November 8th, 2026 / Nov 2
    private static readonly Regex EnMonth = new(
        @"\b(January|February|March|April|May|June|July|August|September|October|November|December|" +
        @"Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)\.?\s+(\d{1,2})(?:st|nd|rd|th)?" +
        @"(?:\s*,?\s*(\d{4}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] DeadlineKeywords =
    {
        "截止", "deadline", "due", "ddl", "考试", "提交", "前完成", "之前", "报名",
        "不接收", "不接受", "exam", "midterm", "final", "quiz", "submit", "submission",
        "presentation", "defense", "defence", "interview", "assignment"
    };

    /// <summary>Parses the published timestamp of an announcement (Chinese or English rendering).</summary>
    public static DateTimeOffset? FromPublishedText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var cn = PublishedCn.Match(text);
        if (cn.Success)
        {
            int y = int.Parse(cn.Groups[1].Value);
            int mo = int.Parse(cn.Groups[2].Value);
            int d = int.Parse(cn.Groups[3].Value);
            var ap = cn.Groups[4].Value;
            int hh = int.Parse(cn.Groups[5].Value);
            int mi = int.Parse(cn.Groups[6].Value);
            int ss = cn.Groups[7].Success ? int.Parse(cn.Groups[7].Value) : 0;

            if (ap == "下午" && hh < 12) hh += 12;
            if (ap == "上午" && hh == 12) hh = 0;

            try { return new DateTimeOffset(y, mo, d, hh, mi, ss, Cst); } catch { return null; }
        }

        var en = PublishedEn.Match(text);
        if (en.Success && TryMonth(en.Groups[1].Value, out var month))
        {
            int day = int.Parse(en.Groups[2].Value);
            int year = int.Parse(en.Groups[3].Value);
            int hh = int.Parse(en.Groups[4].Value);
            int mi = int.Parse(en.Groups[5].Value);
            int ss = en.Groups[6].Success ? int.Parse(en.Groups[6].Value) : 0;
            var ampm = en.Groups[7].Value;

            if (ampm.Equals("PM", StringComparison.OrdinalIgnoreCase) && hh < 12) hh += 12;
            if (ampm.Equals("AM", StringComparison.OrdinalIgnoreCase) && hh == 12) hh = 0;

            try { return new DateTimeOffset(year, month, day, hh, mi, ss, Cst); } catch { return null; }
        }

        return null;
    }

    /// <summary>Best deadline candidate, or null when nothing plausible was found.</summary>
    public static DeadlineHit? FindDeadline(string body, DateTimeOffset anchor)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        DeadlineHit? best = null;
        var taken = new List<(int Start, int End)>();

        void Consider(int index, int end, int year, int month, int day, bool explicitYear)
        {
            int hh = 23, mi = 59;
            bool explicitTime = false;

            if (TryParseTimeAfter(body, end, out var t)) { hh = t.H; mi = t.M; explicitTime = true; }

            if (year == 0)
            {
                year = anchor.Year;
                try
                {
                    var probe = new DateTimeOffset(year, month, day, 0, 0, 0, Cst);
                    if (probe < anchor.AddDays(-30)) year += 1;
                }
                catch { }
            }

            DateTimeOffset value;
            try { value = new DateTimeOffset(year, month, day, hh, mi, 0, Cst); }
            catch { return; }

            var conf = 0.55;
            if (NearDeadlineKeyword(body, index)) conf += 0.30;
            if (explicitYear) conf += 0.10;
            if (explicitTime) conf += 0.05;
            conf = Math.Min(conf, 1.0);

            if (best is null || conf > best.Value.Confidence)
                best = new DeadlineHit(value, conf, explicitYear);

            taken.Add((index, end));
        }

        foreach (Match m in CnYear.Matches(body))
        {
            if (int.TryParse(m.Groups[2].Value, out var mo) && int.TryParse(m.Groups[3].Value, out var d))
                if (mo is >= 1 and <= 12 && d is >= 1 and <= 31)
                    Consider(m.Index, m.Index + m.Length, int.Parse(m.Groups[1].Value), mo, d, true);
        }

        foreach (Match m in IsoDate.Matches(body))
        {
            if (Overlaps(taken, m.Index)) continue;
            if (int.TryParse(m.Groups[2].Value, out var mo) && int.TryParse(m.Groups[3].Value, out var d))
                if (mo is >= 1 and <= 12 && d is >= 1 and <= 31)
                    Consider(m.Index, m.Index + m.Length, int.Parse(m.Groups[1].Value), mo, d, true);
        }

        foreach (Match m in CnNoYear.Matches(body))
        {
            if (Overlaps(taken, m.Index)) continue;
            if (int.TryParse(m.Groups[1].Value, out var mo) && int.TryParse(m.Groups[2].Value, out var d))
                if (mo is >= 1 and <= 12 && d is >= 1 and <= 31)
                    Consider(m.Index, m.Index + m.Length, 0, mo, d, false);
        }

        foreach (Match m in EnMonth.Matches(body))
        {
            if (Overlaps(taken, m.Index)) continue;
            if (!TryMonth(m.Groups[1].Value, out var mo)) continue;
            if (!int.TryParse(m.Groups[2].Value, out var d) || d is < 1 or > 31) continue;
            int year = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            Consider(m.Index, m.Index + m.Length, year, mo, d, m.Groups[3].Success);
        }

        return best;
    }

    private static bool Overlaps(List<(int Start, int End)> ranges, int index) =>
        ranges.Any(r => index >= r.Start - 2 && index <= r.End + 2);

    private static bool TryParseTimeAfter(string body, int end, out (int H, int M) time)
    {
        time = default;
        if (end >= body.Length) return false;

        var window = body.Substring(end, Math.Min(30, body.Length - end));

        var m = Regex.Match(window, @"^[^\d]{0,12}(\d{1,2})\s*[:：]\s*(\d{2})");
        if (m.Success)
        {
            int h = int.Parse(m.Groups[1].Value), mi = int.Parse(m.Groups[2].Value);
            if (h is >= 0 and <= 23 && mi is >= 0 and <= 59) { time = (h, mi); return true; }
        }

        var m2 = Regex.Match(window, @"^[^\d]{0,12}(\d{1,2})\s*时\s*(\d{1,2})\s*分");
        if (m2.Success)
        {
            int h = int.Parse(m2.Groups[1].Value), mi = int.Parse(m2.Groups[2].Value);
            if (h is >= 0 and <= 23 && mi is >= 0 and <= 59) { time = (h, mi); return true; }
        }

        return false;
    }

    private static bool NearDeadlineKeyword(string body, int index)
    {
        int start = Math.Max(0, index - 90);
        int len = Math.Min(body.Length - start, 200);
        var window = body.Substring(start, len);
        foreach (var kw in DeadlineKeywords)
            if (window.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool TryMonth(string token, out int month)
    {
        month = token.ToLowerInvariant().TrimEnd('.') switch
        {
            "january" or "jan" => 1,
            "february" or "feb" => 2,
            "march" or "mar" => 3,
            "april" or "apr" => 4,
            "may" => 5,
            "june" or "jun" => 6,
            "july" or "jul" => 7,
            "august" or "aug" => 8,
            "september" or "sep" or "sept" => 9,
            "october" or "oct" => 10,
            "november" or "nov" => 11,
            "december" or "dec" => 12,
            _ => 0
        };
        return month != 0;
    }
}
