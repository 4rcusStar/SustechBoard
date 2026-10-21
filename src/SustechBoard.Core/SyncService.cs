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

    // ---------- two-tier discovery ----------

    private bool _forceFullScan;

    /// <summary>Ask the next sync to rescan every course menu from scratch.</summary>
    public void RequestFullScan() => _forceFullScan = true;

    /// <summary>Number of content areas currently being crawled on every sync.</summary>
    public int SelectedPathCount
    {
        get
        {
            try { return _db.LoadCoursePaths().Count(p => p.Selected); }
            catch { return 0; }
        }
    }

    private static string MenuHash(List<MenuEntry> menu) =>
        Hash.Sha256(string.Join("|", menu.Where(m => m.IsContentArea)
            .Select(m => m.Group + "/" + m.Label + "@" + m.Url)));

    private bool IsDiscoveryDue(List<CourseRecord> records, List<CourseInfo> courses)
    {
        if (records.Count == 0) return true;

        // A course we have never seen: new enrolment, or a switched account.
        if (courses.Any(c => records.All(r => r.Id != c.Id))) return true;

        var days = Math.Max(1, _settings.FullScanIntervalDays);
        var cutoff = DateTimeOffset.Now.AddDays(-days);
        return records.Any(r => r.DiscoveredAt is null || r.DiscoveredAt < cutoff);
    }

    private static bool LooksLikeWork(string label) =>
        new[] { "作业", "assignment", "homework", "quiz", "test", "exam",
                "考试", "测验", "任务", "评价", "assessment" }
            .Any(k => label.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Tier A: read a course menu, let the model (or keyword rules) choose which content
    /// areas are worth crawling, probe the chosen ones, and persist the result.
    /// </summary>
    private async Task DiscoverCourseAsync(CourseInfo course, List<MenuEntry>? menu, CancellationToken ct)
    {
        menu ??= await _fetcher.GetCourseMenuAsync(course, ct).ConfigureAwait(false);
        var candidates = menu.Where(m => m.IsContentArea).ToList();
        var now = DateTimeOffset.Now;

        var paths = candidates.Select(m => new CoursePath
        {
            CourseId = course.Id,
            PathKey = Hash.Sha256(m.Url)[..12],
            Label = m.Label,
            Group = m.Group,
            Url = m.Url,
            DiscoveredAt = now
        }).ToList();

        if (paths.Count > 0)
        {
            var labels = paths.Select(p => p.Display).ToList();

            HashSet<int>? picked = null;
            if (_llm.IsConfigured)
                picked = await _llm.PickContentAreasAsync(course.Name, labels, ct).ConfigureAwait(false);

            picked ??= new HashSet<int>(
                Enumerable.Range(0, labels.Count).Where(i => LooksLikeWork(labels[i])));

            var pickedList = picked.OrderBy(i => i).ToList();

            // Safety net: if the model found nothing, still probe the two best keyword hits
            // rather than silently scanning nothing at all.
            if (pickedList.Count == 0)
            {
                pickedList = Enumerable.Range(0, labels.Count)
                                       .Where(i => LooksLikeWork(labels[i]))
                                       .Take(2)
                                       .ToList();
                if (pickedList.Count > 0)
                    Log.Warn($"{course.Name}: 模型未选出内容区，回退关键字猜测");
            }

            var capped = pickedList.Take(Math.Max(1, _settings.MaxContentAreasPerCourse)).ToList();

            foreach (var i in capped)
            {
                if (ct.IsCancellationRequested) break;

                var p = paths[i];
                var items = await _fetcher.FetchContentAreaAsync(course, p, ct).ConfigureAwait(false);
                if (items is null) continue;

                p.ItemCount = items.Count;

                // Ground truth beats guessing: an area counts only if it really holds work.
                var hasWork = items.Any(a => a.ContentKind is "作业" or "测验" or "考试" or "测试");
                p.Selected = hasWork || (items.Count > 0 && LooksLikeWork(p.Display));
                if (p.Selected) p.LastOkAt = DateTimeOffset.Now;
            }
        }

        _db.SaveCoursePaths(course.Id, paths);
        _db.UpsertCourseRecord(new CourseRecord
        {
            Id = course.Id,
            Name = course.Name,
            MenuHash = MenuHash(menu),
            DiscoveredAt = now
        });

        Log.Info($"{course.Name}: 内容区 {paths.Count} 个，探测选中 {paths.Count(p => p.Selected)} 个");
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

            // ---------- Tier A: full structure discovery (expensive, rare) ----------
            var records = _db.LoadCourseRecords();
            var fullScanDue = _settings.ContentScanEnabled &&
                              (_forceFullScan || IsDiscoveryDue(records, courses));

            if (fullScanDue)
            {
                StatusChanged?.Invoke("正在扫描课程结构…");
                Log.Info($"开始全量课程结构扫描（{courses.Count} 门课）");
                foreach (var c in courses)
                {
                    if (ct.IsCancellationRequested) break;
                    try { await DiscoverCourseAsync(c, null, ct).ConfigureAwait(false); }
                    catch (Exception ex) { Log.Error("结构发现失败: " + c.Name, ex); }
                }
                _forceFullScan = false;
                records = _db.LoadCourseRecords();
            }

            // ---------- Tier B pass 1: announcements + free menu-change detection ----------
            StatusChanged?.Invoke($"正在抓取 {courses.Count} 门课的公告…");
            var all = new List<Announcement>();
            var fetchComplete = courses.Count > 0;
            var staleStructure = new List<(CourseInfo Course, List<MenuEntry> Menu)>();

            foreach (var c in courses)
            {
                if (ct.IsCancellationRequested) break;

                var (menu, items) = await _fetcher.GetCourseAnnouncementsAsync(c, ct).ConfigureAwait(false);
                if (items is null)
                {
                    fetchComplete = false;   // partial fetch — never reconcile on this
                    continue;
                }
                all.AddRange(items);

                // Structure changed under us? Rediscover this course before reading its areas.
                if (_settings.ContentScanEnabled)
                {
                    var rec = records.FirstOrDefault(r => r.Id == c.Id);
                    var hash = MenuHash(menu);
                    if (rec is not null && rec.MenuHash.Length > 0 && rec.MenuHash != hash)
                    {
                        Log.Info($"{c.Name}: 菜单结构变化，重新发现内容区");
                        staleStructure.Add((c, menu));
                    }
                }
            }

            foreach (var (course, menu) in staleStructure)
            {
                if (ct.IsCancellationRequested) break;
                try { await DiscoverCourseAsync(course, menu, ct).ConfigureAwait(false); }
                catch (Exception ex) { Log.Error("结构发现失败: " + course.Name, ex); }
            }

            // ---------- Tier B pass 2: selected content areas ----------
            if (_settings.ContentScanEnabled)
            {
                var selected = _db.LoadCoursePaths()
                                  .Where(p => p.Selected)
                                  .GroupBy(p => p.CourseId)
                                  .ToDictionary(g => g.Key, g => g.ToList());

                if (selected.Count > 0)
                {
                    StatusChanged?.Invoke("正在抓取作业/测验区…");
                    foreach (var c in courses)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (!selected.TryGetValue(c.Id, out var paths)) continue;

                        foreach (var path in paths)
                        {
                            var content = await _fetcher.FetchContentAreaAsync(c, path, ct).ConfigureAwait(false);
                            if (content is null)
                            {
                                _db.RecordPathResult(c.Id, path.PathKey, false);
                                fetchComplete = false;
                                continue;
                            }
                            _db.RecordPathResult(c.Id, path.PathKey, true);
                            all.AddRange(content);
                            Log.Info($"  {c.Name} · {path.Display}: {content.Count} 条");
                        }
                    }
                }
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
