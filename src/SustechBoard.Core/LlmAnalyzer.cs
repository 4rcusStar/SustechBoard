using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SustechBoard.Core;

/// <summary>Outcome of a live connectivity/quality probe against the configured model.</summary>
public readonly record struct LlmProbeResult(bool Ok, string Message, long ElapsedMs);

/// <summary>
/// Enriches an announcement with semantic fields via an OpenAI-compatible chat endpoint
/// (DeepSeek by default). Strictly an enhancement layer: any failure simply falls back to rules.
/// </summary>
public sealed class LlmAnalyzer : IDisposable
{
    private const string SystemPrompt = """
        你是南方科技大学 Blackboard 课程公告的分析器。用户是计算机系本科生。
        你会收到一条课程公告的标题和正文，请输出严格的 JSON（不要 markdown 代码块，不要解释）。

        JSON 字段（全部必填）：
        {
          "label": "考试 | quiz | 作业 | 通知 | 成绩 | 资料 | 其他",
          "category": "assignment | exam | grade_release | admin_notice | resource | irrelevant",
          "action_required": true/false,          // 学生是否需要采取行动
          "action": "一句话说明要做什么，无则空字符串",
          "deadline": "YYYY-MM-DDTHH:mm:ss+08:00 或 null",
          "deadline_confidence": 0.0-1.0,
          "hard_deadline": true/false,
          "key_constraints": ["关键限定语，如：仅接受英文作答、无法补考、不接受迟交"],
          "events": [
            { "kind": "quiz | midterm | final | assignment | other",
              "start": "YYYY-MM-DDTHH:mm:ss+08:00",
              "end": "YYYY-MM-DDTHH:mm:ss+08:00 或 null",
              "all_day": true/false,
              "location": "地点，无则空字符串" }
          ],
          "summary": "不超过40字的中文摘要",
          "is_noise": true/false
        }

        label 判定优先级：
        - 出现 quiz / 小测 / 随堂测 → "quiz"
        - 出现 考试 / 期中 / 期末 / midterm / final exam → "考试"
        - 出现 作业 / assignment / homework / 提交 / due → "作业"
        - 出现 成绩 / 批改 / 分数 / grade → "成绩"
        - 出现 课件 / 答案 / 资料 / slides → "资料"
        - 其余行政类通知 → "通知"

        events 抽取规则（日历会直接用它，务必准）：
        - 凡是学生需要在某个时刻出现、或必须交付的时刻，都抽成一条 event。
        - 考试类（quiz/midterm/final）务必给出 end：正文写 "10:00-12:00" 就 start=10:00、end=12:00。
        - 只有日期、没有钟点时 all_day=true 且 start 取当天 00:00 之外更语义化的时刻也不必纠结，直接用当天时间并把 all_day 置 true。
        - 作业截止：kind=assignment，all_day=true，start 取截止日 23:59。
        - 不要把"发布时间""通知发布日""会议/讲座时间"当成事件。
        - 相对时间（"两周后""大二暑假"）无法换算成具体日期时，不要造 events。
        - 一条公告可以有多个 event（如同一门课既有期中考又有作业截止）；没有则为空数组。

        deadline 取该公告最主要的那个时刻（考试取开始时间），没有明确截止则填 null。
        如果公告是上学期/往年遗留、且与本学期无关，is_noise 设 true。
        """;

    private AppSettings _settings;
    private Secrets _secrets;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(70) };

    public LlmAnalyzer(AppSettings settings, Secrets secrets)
    {
        _settings = settings;
        _secrets = secrets;
    }

    public void Update(AppSettings settings, Secrets secrets)
    {
        _settings = settings;
        _secrets = secrets;
    }

    public bool IsConfigured =>
        _settings.LlmEnabled &&
        !string.IsNullOrWhiteSpace(_settings.LlmBaseUrl) &&
        !string.IsNullOrWhiteSpace(_settings.LlmModel) &&
        !string.IsNullOrWhiteSpace(_secrets.LlmApiKey);

    public string LastError { get; private set; } = "";

    /// <summary>
    /// Runs the real analysis path against a synthetic announcement, so a pass here means
    /// actual production calls will work too (same endpoint, same request shape, same parser).
    /// </summary>
    public async Task<LlmProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new LlmProbeResult(false, "未启用或配置不完整（需要接口地址、模型名、API Key）", 0);

        // Deliberately shaped like a real announcement so the model cannot dismiss it as a test.
        // It also states an explicit deadline, which lets us verify extraction quality, not just connectivity.
        var sample = new Announcement
        {
            Id = "_probe_1",
            CourseId = "_probe",
            CourseName = "Computer Networks Fall 2026",
            Title = "Lab Assignment 1 released",
            Body = "Hello everyone, Lab Assignment 1 is now available. " +
                   "Deadline: November 2, 2026, at 23:59 (Beijing time, UTC+8). " +
                   "Submit your answers through the course OJ website. No late submission will be accepted.",
            PublishedAt = DateTimeOffset.Now,
            ContentHash = "probe"
        };

        var sw = Stopwatch.StartNew();
        AnnouncementAnalysis? result;
        try
        {
            result = await AnalyzeAsync(sample, null, ct).ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
        }

        if (result is null)
            return new LlmProbeResult(false,
                string.IsNullOrWhiteSpace(LastError) ? "无有效响应" : LastError,
                sw.ElapsedMilliseconds);

        var msg = $"分类 {result.Category}";
        msg += result.Deadline is { } d
            ? $" · 截止提取 ✓ {d:MM-dd HH:mm}"
            : " · 截止提取 ✗（模型未识别明文截止）";
        if (result.HardDeadline) msg += " · 硬截止";
        if (result.Summary.Length > 0) msg += $" · {Truncate(result.Summary, 22)}";
        return new LlmProbeResult(true, msg, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Decides which content areas hold graded work, judging from the items actually inside them
    /// rather than from the menu wording. One call per course; the model returns indices only.
    /// Returns null when the call failed.
    /// </summary>
    public async Task<HashSet<int>?> ClassifyContentAreasAsync(
        string courseName, IReadOnlyList<string> areaDigests, CancellationToken ct = default)
    {
        if (!IsConfigured || areaDigests.Count == 0) return null;

        const string system = """
            你是南方科技大学 Blackboard 课程的助手。用户会给出某门课若干「内容区」，
            每个区下列出它实际包含的条目（标题 | 类型 | 说明片段 | 附件名）。

            你的任务：判断哪些内容区包含**需要学生提交或参加、且有截止时间的东西**
            ——作业、实验、小测、期中/期末、需要上交的报告等。

            只输出 JSON：{"keep":[0,3]}

            判断要点：
            - 看条目标题和说明，不要只看区名。"Chapter 2" 里可能有 "HW1 Force Vectors / Due: ..."。
            - 纯课件、教材、Past exam papers、教师简介、课程大纲 → 不要。
            - "Homework Solution / 作业答案" 是答案，不是作业本身 → 需要你自己判断，若一个区里既有作业又有答案，保留该区。
            - 只要区里**至少有一个**条目像作业/测验/考试，就保留该区。
            - 宁可多留，漏掉作业的代价远大于多抓一个区。
            - 只填编号，不要编造。
            """;

        var sb = new StringBuilder();
        sb.AppendLine($"课程：{courseName}");
        sb.AppendLine();

        for (int i = 0; i < areaDigests.Count; i++)
        {
            sb.AppendLine($"[{i}] {areaDigests[i]}");
            sb.AppendLine();
        }

        var user = sb.ToString();
        if (user.Length > 12000) user = user[..12000];

        var content = await RawChatAsync(system, user, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content)) return null;

        try
        {
            using var doc = JsonDocument.Parse(StripCodeFence(content));
            if (!doc.RootElement.TryGetProperty("keep", out var arr) ||
                arr.ValueKind != JsonValueKind.Array) return null;

            var picked = new HashSet<int>();
            foreach (var el in arr.EnumerateArray())
                if (el.TryGetInt32(out var idx) && idx >= 0 && idx < areaDigests.Count)
                    picked.Add(idx);

            return picked;
        }
        catch (Exception ex)
        {
            Log.Warn("内容区分类返回无法解析: " + ex.Message);
            return null;
        }
    }

    /// <summary>Bare chat call used by non-analysis helpers (no retry, no mapping).</summary>
    private async Task<string?> RawChatAsync(string system, string user, CancellationToken ct)
    {
        try
        {
            var url = _settings.LlmBaseUrl.TrimEnd('/') + "/chat/completions";
            var payload = new
            {
                model = _settings.LlmModel,
                temperature = 0,
                response_format = new { type = "json_object" },
                messages = new[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secrets.LlmApiKey);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)resp.StatusCode}: {Truncate(text, 160)}";
                return null;
            }

            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetProperty("choices")[0]
                      .GetProperty("message").GetProperty("content").GetString();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("LLM 调用异常", ex);
            return null;
        }
    }

    public async Task<AnnouncementAnalysis?> AnalyzeAsync(
        Announcement a, DeadlineHit? ruleHit, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;

        var body = a.Body.Length > 3000 ? a.Body[..3000] : a.Body;
        var user = $"课程：{a.CourseName}\n标题：{a.Title}\n正文：\n{body}";

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var url = _settings.LlmBaseUrl.TrimEnd('/') + "/chat/completions";
                var payload = new
                {
                    model = _settings.LlmModel,
                    temperature = 0,
                    response_format = new { type = "json_object" },
                    messages = new[]
                    {
                        new { role = "system", content = SystemPrompt },
                        new { role = "user", content = user }
                    }
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secrets.LlmApiKey);

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    var code = (int)resp.StatusCode;
                    LastError = code switch
                    {
                        401 or 403 => $"API Key 无效或无权限（HTTP {code}）",
                        404 => "接口地址不存在，检查 Base URL 是否以 /v1 结尾",
                        429 => "触发频率限制，请稍后再试",
                        _ => $"HTTP {code}: {Truncate(text, 180)}"
                    };
                    Log.Warn("LLM 调用失败 " + LastError);
                    if (code is 401 or 403) return null;   // bad key: stop trying
                    continue;
                }

                var dto = ParseResponse(text);
                if (dto is null)
                {
                    LastError = "响应无法解析为 JSON";
                    continue;
                }

                return Map(dto, ruleHit);
            }
            catch (OperationCanceledException)
            {
                LastError = "LLM 超时";
                return null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Error("LLM 异常", ex);
            }
        }

        return null;
    }

    private static LlmDto? ParseResponse(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.GetArrayLength() == 0) return null;

            var content = choices[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) return null;

            content = StripCodeFence(content);
            return JsonSerializer.Deserialize<LlmDto>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            Log.Warn("LLM JSON 解析失败: " + ex.Message);
            return null;
        }
    }

    private static string StripCodeFence(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;
        var nl = s.IndexOf('\n');
        if (nl < 0) return s;
        var inner = s[(nl + 1)..];
        var end = inner.LastIndexOf("```", StringComparison.Ordinal);
        return end >= 0 ? inner[..end].Trim() : inner.Trim();
    }

    private static AnnouncementAnalysis Map(LlmDto dto, DeadlineHit? ruleHit)
    {
        var result = new AnnouncementAnalysis
        {
            Label = NormalizeLabel(dto.Label, dto.Category),
            Category = NormalizeCategory(dto.Category),
            ActionRequired = dto.ActionRequired,
            Action = string.IsNullOrWhiteSpace(dto.Action) ? null : dto.Action!.Trim(),
            DeadlineConfidence = Math.Clamp(dto.DeadlineConfidence, 0, 1),
            HardDeadline = dto.HardDeadline,
            KeyConstraints = dto.KeyConstraints?
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .Distinct()
                .Take(6)
                .ToList() ?? new List<string>(),
            Events = ParseEvents(dto.Events),
            Summary = (dto.Summary ?? "").Trim(),
            IsNoise = dto.IsNoise,
            Source = "llm"
        };

        if (!string.IsNullOrWhiteSpace(dto.Deadline) &&
            DateTimeOffset.TryParse(dto.Deadline, out var dl))
        {
            result.Deadline = dl;
            if (result.DeadlineConfidence <= 0) result.DeadlineConfidence = 0.8;
        }
        else if (ruleHit is { } hit && hit.Confidence >= 0.80)
        {
            // Model found no explicit date, but the rule layer did with high confidence.
            result.Deadline = hit.Value;
            result.DeadlineConfidence = hit.Confidence * 0.9;
        }

        // Two independent extractors landing on the same day is strong evidence.
        // Without this, a low self-reported confidence can demote a genuinely explicit deadline.
        if (result.Deadline is { } agreed && ruleHit is { } rule &&
            rule.Confidence >= 0.70 &&
            Math.Abs((agreed.Date - rule.Value.Date).TotalDays) <= 1)
        {
            result.DeadlineConfidence = Math.Max(result.DeadlineConfidence,
                                                 Math.Max(rule.Confidence, 0.85));
        }

        if (result.Summary.Length == 0)
            result.Summary = dto.Action ?? "";

        return result;
    }

    private static string NormalizeCategory(string? raw)
    {
        var c = (raw ?? "").Trim().ToLowerInvariant();
        return c switch
        {
            "assignment" or "exam" or "grade_release" or "admin_notice" or "resource" or "irrelevant" => c,
            _ => "unknown"
        };
    }

    private static readonly string[] AllowedLabels = { "考试", "quiz", "作业", "通知", "成绩", "资料", "其他" };

    private static string NormalizeLabel(string? raw, string? category)
    {
        var v = (raw ?? "").Trim();
        if (AllowedLabels.Contains(v)) return v;

        // Tolerate English answers from the model.
        var lower = v.ToLowerInvariant();
        var mapped = lower switch
        {
            "quiz" => "quiz",
            "exam" or "midterm" or "final" => "考试",
            "assignment" or "homework" => "作业",
            "grade_release" or "grade" => "成绩",
            "resource" => "资料",
            "admin_notice" or "notice" => "通知",
            _ => null
        };
        return mapped ?? CategoryToLabel(NormalizeCategory(category));
    }

    /// <summary>Fallback label used by both the rule layer and the LLM mapping.</summary>
    public static string CategoryToLabel(string category) => category switch
    {
        "exam" => "考试",
        "assignment" => "作业",
        "grade_release" => "成绩",
        "admin_notice" => "通知",
        "resource" => "资料",
        _ => "其他"
    };

    private static readonly string[] EventKinds = { "quiz", "midterm", "final", "assignment", "other" };

    private static List<AnnouncementEvent> ParseEvents(List<LlmEventDto>? raw)
    {
        var list = new List<AnnouncementEvent>();
        if (raw is null) return list;

        foreach (var e in raw.Take(5))
        {
            if (string.IsNullOrWhiteSpace(e.Start)) continue;
            if (!DateTimeOffset.TryParse(e.Start, out var start)) continue;

            DateTimeOffset? end = null;
            if (!string.IsNullOrWhiteSpace(e.End) &&
                DateTimeOffset.TryParse(e.End, out var en) && en > start)
                end = en;

            var kind = (e.Kind ?? "other").Trim().ToLowerInvariant();
            if (!EventKinds.Contains(kind)) kind = "other";

            list.Add(new AnnouncementEvent
            {
                Kind = kind,
                Start = start,
                End = end,
                AllDay = e.AllDay,
                Location = (e.Location ?? "").Trim()
            });
        }

        return list;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private sealed class LlmDto
    {
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("action_required")] public bool ActionRequired { get; set; }
        [JsonPropertyName("action")] public string? Action { get; set; }
        [JsonPropertyName("deadline")] public string? Deadline { get; set; }
        [JsonPropertyName("deadline_confidence")] public double DeadlineConfidence { get; set; }
        [JsonPropertyName("hard_deadline")] public bool HardDeadline { get; set; }
        [JsonPropertyName("key_constraints")] public List<string>? KeyConstraints { get; set; }
        [JsonPropertyName("events")] public List<LlmEventDto>? Events { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("is_noise")] public bool IsNoise { get; set; }
    }

    private sealed class LlmEventDto
    {
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("start")] public string? Start { get; set; }
        [JsonPropertyName("end")] public string? End { get; set; }
        [JsonPropertyName("all_day")] public bool AllDay { get; set; }
        [JsonPropertyName("location")] public string? Location { get; set; }
    }

    public void Dispose() => _http.Dispose();
}
