namespace SustechBoard.Core;

/// <summary>
/// Orchestrates one refresh cycle: authenticate, scrape, persist, analyse, rank.
/// Also owns the optional background LLM enrichment pass.
/// </summary>
public sealed class SyncService : IDisposable
{
    private readonly BoardDatabase _db;
    private AppSettings _settings;
    private Secrets _secrets;

    private readonly BlackboardClient _client;
    private readonly BlackboardFetcher _fetcher;
    private readonly LlmAnalyzer _llm;

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly HashSet<string> _llmFailed = new(StringComparer.Ordinal);

    public IReadOnlyList<PanelItem> Items { get; private set; } = Array.Empty<PanelItem>();
    public DateTimeOffset? LastSyncAt { get; private set; }
    public string LastError { get; private set; } = "";
    public bool IsSyncing { get; private set; }
    public int CourseCount { get; private set; }
    public int AnnouncementCount { get; private set; }

    public event Action? Updated;
    public event Action<string>? StatusChanged;

    public SyncService(AppSettings settings, Secrets secrets, string? dbPath = null)
    {
        _settings = settings;
        _secrets = secrets;
        _db = new BoardDatabase(dbPath);
        _client = new BlackboardClient(settings, secrets);
        _fetcher = new BlackboardFetcher(_client);
        _llm = new LlmAnalyzer(settings, secrets);
    }

    public bool LlmActive => _llm.IsConfigured;
    public string LlmLastError => _llm.LastError;

    private void ReloadItems(DateTimeOffset now)
    {
        var stored = _db.LoadAll();
        var analyses = _db.LoadAnalyses();
        Items = Prioritizer.BuildAll(stored, analyses, _settings, now);
    }

    private void RebuildItems()
    {
        ReloadItems(DateTimeOffset.Now);
        Updated?.Invoke();
    }

    /// <summary>
    /// Flattens every announcement's events into calendar entries.
    /// Archive state is deliberately ignored: a manually archived item's exam still happens,
    /// and past months must remain readable.
    /// </summary>
    public IReadOnlyList<CalendarEvent> BuildCalendarEvents()
    {
        var result = new List<CalendarEvent>();

        foreach (var item in Items)
        {
            var a = item.Announcement;
            var events = item.Analysis.Events;

            // Degraded fallback when the model produced no explicit event.
            if (events.Count == 0 &&
                item.Analysis.Deadline is { } dl &&
                item.Analysis.DeadlineConfidence >= 0.70)
            {
                events = new List<AnnouncementEvent>
                {
                    new()
                    {
                        Kind = item.Analysis.Category switch
                        {
                            "exam" => "other",
                            "assignment" => "assignment",
                            _ => "other"
                        },
                        Start = dl,
                        AllDay = dl.TimeOfDay == new TimeSpan(23, 59, 0)
                    }
                };
            }

            foreach (var e in events)
            {
                result.Add(new CalendarEvent(e.Start, e.End, e.Kind, e.AllDay,
                    a.CourseName, a.Title, e.Location, a.Id));
            }
        }

        return result.OrderBy(e => e.Start).ToList();
    }

    /// <summary>User action: move an announcement into / out of the manual archive.</summary>
    public bool SetManualArchived(string annId, bool archived)
    {
        if (!_db.SetManualArchived(annId, archived))
        {
            Log.Warn("手动归档失败，条目不存在: " + annId);
            return false;
        }
        Log.Info($"{(archived ? "手动归档" : "还原")}: {annId}");
        RebuildItems();
        return true;
    }

    public void UpdateSettings(AppSettings settings, Secrets secrets)
    {
        var userChanged = !string.Equals(_settings.Username, settings.Username, StringComparison.Ordinal);

        _settings = settings;
        _secrets = secrets;
        _client.UpdateCredentials(settings, secrets);
        _llm.Update(settings, secrets);

        if (userChanged)
        {
            Log.Info($"账号已更换为 {settings.Username}，清空上一账号的数据");
            ResetForAccountChange();
        }
    }

    /// <summary>Wipes every scraped row so a different account starts from a clean slate.</summary>
    public void ResetForAccountChange()
    {
        _db.ClearAll();
        _llmFailed.Clear();
        Items = Array.Empty<PanelItem>();
        AnnouncementCount = 0;
        CourseCount = 0;
        LastSyncAt = null;
        LastError = "";
        Updated?.Invoke();
    }

    /// <summary>Populates the panel from SQLite without touching the network.</summary>
    public void LoadCached()
    {
        try
        {
            var all = _db.LoadAll();
            var analyses = _db.LoadAnalyses();
            AnnouncementCount = all.Count;
            Items = Prioritizer.BuildAll(all, analyses, _settings, DateTimeOffset.Now);
            Updated?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("加载缓存失败", ex);
        }
    }

    public async Task<SyncResult> RunAsync(bool isFirstRun, CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IsSyncing = true;
            LastError = "";
            StatusChanged?.Invoke("正在登录…");

            var result = new SyncResult();

            var login = await _client.LoginAsync(ct).ConfigureAwait(false);
            if (!login.Success)
            {
                LastError = login.Message;
                result.Error = login.Message;
                StatusChanged?.Invoke("登录失败");
                return result;
            }

            StatusChanged?.Invoke("正在读取课程…");
            var courses = await _fetcher.GetCoursesAsync(ct).ConfigureAwait(false);
            result.Courses = courses;
            CourseCount = courses.Count;

            StatusChanged?.Invoke($"正在抓取 {courses.Count} 门课的公告…");
            var all = new List<Announcement>();
            var fetchComplete = courses.Count > 0;

            foreach (var c in courses)
            {
                if (ct.IsCancellationRequested) break;
                var items = await _fetcher.GetAnnouncementsAsync(c, ct).ConfigureAwait(false);
                if (items is null)
                {
                    fetchComplete = false;   // partial fetch — never reconcile on this
                    continue;
                }
                all.AddRange(items);
            }

            if (ct.IsCancellationRequested) fetchComplete = false;

            // De-duplicate defensively: Blackboard ids should be unique, but be safe.
            all = all.GroupBy(a => a.Id).Select(g => g.First()).ToList();

            var now = DateTimeOffset.Now;
            var changed = _db.Upsert(all, now);
            result.TotalAnnouncements = all.Count;
            result.NewOrChanged = changed;
            AnnouncementCount = all.Count;

            // Reconcile removals so a switched account (or a dropped course) leaves nothing behind.
            if (fetchComplete)
            {
                var removed = _db.DeleteNotIn(all.Select(a => a.Id).ToList());
                if (removed > 0)
                    Log.Info($"清理 {removed} 条本次未返回的旧公告（换号或退课）");

                result.RemovedCount = removed;
                _db.PruneOrphanAnalyses();
            }
            else
            {
                Log.Warn("本次抓取不完整，跳过旧公告清理");
            }

            // Ensure every announcement has at least a rule-layer analysis.
            StatusChanged?.Invoke("正在分析…");
            foreach (var a in all)
            {
                var hit = DateParsing.FindDeadline(a.Body, a.PublishedAt ?? now);
                var cached = _db.GetAnalysis(a.Id, a.ContentHash);
                if (cached is not null) continue;
                var rule = RuleAnalyzer.Analyze(a, hit, now);
                _db.SaveAnalysis(a.Id, a.ContentHash, rule);
            }

            ReloadItems(now);

            LastSyncAt = now;
            result.CompletedAt = now;
            StatusChanged?.Invoke("已更新");
            Updated?.Invoke();

            if (changed.Count > 0 && !isFirstRun)
                NotifyIfNeeded(changed, now);

            return result;
        }
        catch (OperationCanceledException)
        {
            LastError = "已取消";
            return new SyncResult { Error = "已取消", CompletedAt = DateTimeOffset.Now };
        }
        catch (Exception ex)
        {
            Log.Error("同步失败", ex);
            LastError = ex.Message;
            StatusChanged?.Invoke("同步失败");
            return new SyncResult { Error = ex.Message, CompletedAt = DateTimeOffset.Now };
        }
        finally
        {
            IsSyncing = false;
            _syncLock.Release();
        }
    }

    private void NotifyIfNeeded(List<Announcement> changed, DateTimeOffset now)
    {
        if (!_settings.NotifyWebhook || string.IsNullOrWhiteSpace(_settings.WebhookUrl)) return;

        var changedIds = changed.Select(c => c.Id).ToHashSet();
        var relevant = Items
            .Where(i => changedIds.Contains(i.Announcement.Id) && i.Priority != Priority.Archive)
            .Where(i => i.Priority is Priority.P0 or Priority.P1 or Priority.P2)
            .ToList();

        if (relevant.Count == 0) return;

        var md = Notifier.BuildMarkdown(relevant, now);
        _ = Notifier.SendWebhookAsync(_settings.WebhookUrl, md).ContinueWith(t =>
        {
            if (t.IsFaulted) Log.Error("webhook 任务异常", t.Exception);
        }, TaskScheduler.Default);
    }

    /// <summary>Runs the LLM pass over items that still only have rule-layer analysis.</summary>
    public async Task<int> EnrichAsync(int max, CancellationToken ct = default)
    {
        if (!_llm.IsConfigured) return 0;

        var now = DateTimeOffset.Now;
        // Every visible item goes through the model — including archived ones, whose dates
        // still belong on the calendar. Content hashing makes this a one-time cost per item.
        var candidates = Items
            .Where(i => i.Analysis.Source != "llm")
            .Where(i => !_llmFailed.Contains(i.Announcement.Id))
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.DaysLeft ?? int.MaxValue)
            .Take(max)
            .ToList();

        if (candidates.Count == 0) return 0;

        StatusChanged?.Invoke($"智能分析 {candidates.Count} 条…");
        int done = 0;

        foreach (var item in candidates)
        {
            if (ct.IsCancellationRequested) break;

            var a = item.Announcement;
            var hit = DateParsing.FindDeadline(a.Body, a.PublishedAt ?? now);
            try
            {
                var enriched = await _llm.AnalyzeAsync(a, hit, ct).ConfigureAwait(false);
                if (enriched is null)
                {
                    _llmFailed.Add(a.Id);
                    continue;
                }
                _db.SaveAnalysis(a.Id, a.ContentHash, enriched);
                done++;
            }
            catch (Exception ex)
            {
                Log.Error("富化失败 " + a.Title, ex);
                _llmFailed.Add(a.Id);
            }
        }

        if (done > 0)
        {
            ReloadItems(now);
            Updated?.Invoke();
        }

        StatusChanged?.Invoke(done > 0 ? $"智能分析完成 {done} 条" : "已更新");
        return done;
    }

    public void Dispose()
    {
        _client.Dispose();
        _llm.Dispose();
        _db.Dispose();
        _syncLock.Dispose();
    }
}
