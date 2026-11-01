using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Data.Admin.Worker.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Backend.Service.Admin.System.V1;
using Jularr.Backend.Service.Admin.Translation.V1;
using Jularr.Frontend.Pages.Admin.Translation;
using Jularr.Backend.Service.Admin.Worker.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Live;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Jobs
{
    // Every background job with its state and the Owner's controls, and below it what the operations did; both are lists of the shared list kit.
    public sealed class JobsModel(ReadWorkerJobsV1 read, ReadBookTranslationProgressV1 readTranslation, PauseWorkerJobV1 pause, RunWorkerJobV1 run, SetWorkerJobUseV1 use, ReadActivityV1 readHistory, ReadHealthV1 readHealth, FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string Address = "/Admin/Jobs";
        private const int PageSize = 25;

        // The icon and tint of what a job does: downloads blue, storage green, syncing amber, the rest the accent.
        private static readonly Dictionary<string, (string Icon, UiTone Tone)> Kinds = new()
        {
            ["operations"] = ("library", UiTone.Success),
            ["backupQueue"] = ("shield", UiTone.Success),
            ["downloads"] = ("download", UiTone.Info),
            ["wantedSearch"] = ("search", UiTone.Info),
            ["providerSync"] = ("sync", UiTone.Warning),
            ["providerSyncPlex"] = ("providers", UiTone.Warning),
            ["providerSyncJellyfin"] = ("providers", UiTone.Warning),
            ["providerSyncEmby"] = ("providers", UiTone.Warning),
            ["usenet"] = ("download", UiTone.Info),
            ["aniListMatching"] = ("layers", UiTone.Warning),
            ["aniListRelations"] = ("link", UiTone.Warning),
            ["animeMappings"] = ("link", UiTone.Warning),
            ["aniListSync"] = ("sync", UiTone.Warning),
            ["metadataFill"] = ("layers", UiTone.Accent),
            ["wantedTargets"] = ("search", UiTone.Accent),
            ["covers"] = ("grid", UiTone.Accent),
            ["coverBackfill"] = ("grid", UiTone.Accent),
            ["catalogCleanup"] = ("layers", UiTone.Accent),
            ["storage"] = ("drive", UiTone.Success),
            ["scanScheduling"] = ("calendar", UiTone.Success),
            ["backup"] = ("shield", UiTone.Success),
            ["maintenance"] = ("settings", UiTone.Accent),
            ["trickplay"] = ("play", UiTone.Accent),
            ["subtitles"] = ("subtitles", UiTone.Accent),
            ["recognition"] = ("text-select", UiTone.Accent),
            ["catalog"] = ("globe", UiTone.Warning),
            ["discoverFeeds"] = ("globe", UiTone.Warning),
            ["resources"] = ("pulse", UiTone.Accent),
            ["health"] = ("status", UiTone.Success)
        };

        // The filter chips: a state of a job belongs to one of them.
        private static readonly string[] Groups = ["ready", "waiting", "paused", "running", "failed", "off"];

        private IReadOnlyList<WorkerJobV1> _jobs = [];
        private PageResult<ActivityItemV1>? _history;
        private HealthV1? _health;
        private BookTranslationProgressV1? _translation;

        // The chosen chip and the filter words live in the address.
        [BindProperty(SupportsGet = true, Name = "state")]
        public string? StateFilter { get; set; }

        [BindProperty(SupportsGet = true, Name = "q")]
        public string? QueryFilter { get; set; }

        [BindProperty(SupportsGet = true, Name = "pageNumber")]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("admin.jobs.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Jobs/Jobs.entry.scss");
            page.Asset("Framework/Components/Drawer.entry.ts");
            page.Asset("Framework/Components/ListFilter.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var canControl = texts.Can("instance.settings.update");
            var state = StateFilter is { } chosen && Groups.Contains(chosen) ? chosen : null;
            var counts = Groups.ToDictionary(group => group, group => _jobs.Count(job => GroupOf(job.WorkerJobStateTypeId) == group));
            var words = (QueryFilter ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var shown = _jobs.Where(job => (state is null || GroupOf(job.WorkerJobStateTypeId) == state) && words.All(word => FilterText(job).Contains(word, StringComparison.CurrentCultureIgnoreCase))).ToList();
            var allPaused = _jobs.Count > 0 && _jobs.All(job => job.IsPaused);

            // One row of filter chips (the state's icon, its name and how many) with the list's own filter.
            var chips = new List<NavigationTab> { new("common.all", Link(null, QueryFilter), state is null, Count: _jobs.Count, Icon: Glyph("layers", 14)) };
            chips.AddRange(Groups.Where(group => group is "ready" or "waiting" or "paused" or "running" or "failed" || counts[group] > 0 || group == state)
                .Select(group => new NavigationTab(ChipKey(group), Link(group, QueryFilter), group == state, Count: counts[group], Icon: Glyph(ChipIcon(group), 14))));
            var keep = state is null ? null : new Dictionary<string, string> { ["state"] = state };
            page.NoPageRow();
            page.Content.Add(new ListToolbar(AdminMenu.Row(texts, Request, chips, ariaLabelKey: "jobs.filters"), new SearchBox(Address, "jobs.filter", QueryFilter, keep, ".jobs-list", Glyph("search", 15)),
                ListToolbar.Count(string.Format(CultureInfo.CurrentCulture, texts["jobs.count"], shown.Count), texts["jobs.count"])));

            // What the last health check found; the Health check job below runs it again.
            if (_health is { Services.Count: > 0 } health) page.Content.Add(Health(health));

            // The jobs, a drawer of details for each of them.
            var columns = new List<ListColumn> { new("jobs.job", ListRole.Main), new("table.status", ListRole.Status), new("jobs.last"), new("jobs.next", ListRole.Data, 2), new("jobs.use", ListRole.Data, 1), new("common.actions", ListRole.Actions) };
            var current = Math.Max(1, PageNumber);
            var visible = shown.Skip((current - 1) * PageSize).Take(PageSize).ToList();
            var rows = visible.Select(job => Row(job, csrf, canControl)).ToList();
            var empty = new Div(new IconTile(Glyph("search", 22), UiTone.Neutral), new P(new Text("jobs.empty")),
                state is null && string.IsNullOrEmpty(QueryFilter) ? new Span() : new A(new Text("jobs.clear")).Class("button button--soft button--compact").Attribute("href", Address));
            page.Content.Add(new ListPanel("jobs.list", columns, rows, empty).Modifier("jobs-list").Paged(PageSize));
            page.Content.AddRange(visible.Select(job => Details(job, csrf, canControl)));

            // What the queue did, newest first.
            if (_history is { } history)
            {
                var list = history.Items.Count > 0
                    ? new ListPanel("jobs.history", [new ListColumn("jobs.job", ListRole.Main), new ListColumn("table.status", ListRole.Status), new ListColumn("jobs.when")], [.. history.Items.Select(HistoryRow)]).Modifier("jobs-history")
                    : (IUiComponent)new P(new Text("jobs.history.empty"));
                page.Content.Add(new Div(new H2(new Text("jobs.history")), list, new A(new Text("jobs.allActivity")).Attribute("href", "/Admin/Activity?Filter=History")).Class("jobs-history-block"));
            }

            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));

            // The page's actions and the paging of the list float at the bottom, as the save pill does.
            var actions = new List<IUiComponent> { new A(Glyph("sync", 16), new Span(new Text("jobs.refresh")).Class("action-dock__text")).Class("button").Attribute("href", Link(state, QueryFilter, current)) };
            if (canControl && _jobs.Count > 0)
            {
                actions.Add(Post("PauseAll", csrf, [("isPaused", allPaused ? "false" : "true")], new Button(Glyph(allPaused ? "play" : "pause", 16), new Span(new Text(allPaused ? "jobs.resumeAll" : "jobs.pauseAll")).Class("action-dock__text")).Soft().Submit()));
            }

            page.Content.Add(new ActionDock(assets, "jobs.actions", actions));
        }

        // Whether every service answered, how many, and when the health job last looked.
        private IUiComponent Health(HealthV1 health)
        {
            var asked = health.Services.Count(service => service.HealthStateTypeId != HealthStateType.Unknown);
            var reachable = health.Services.Count(service => service.HealthStateTypeId == HealthStateType.Available);
            return new Div(new StateChip(new Text(health.IsStable ? "dashboard.status.stable" : "dashboard.status.warning"), health.IsStable ? UiTone.Success : UiTone.Warning),
                new Span(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["jobs.health.summary"], reachable, asked)),
                    health.CheckedAt is { } at ? new Span(new ValueText(" · "), LocalTime.Format(texts["dashboard.services.checked"], at, "time")) : new Span()),
                new A(new Text("admin.dashboard.title")).Class("button button--soft button--compact").Attribute("href", "/Admin/Dashboard")).Class("jobs-health");
        }

        // The filter group of a job's state.
        private static string GroupOf(WorkerJobStateType state) => state switch
        {
            WorkerJobStateType.Idle => "ready",
            WorkerJobStateType.Pending or WorkerJobStateType.WaitingForUse => "waiting",
            WorkerJobStateType.Paused => "paused",
            WorkerJobStateType.Running => "running",
            WorkerJobStateType.Failed => "failed",
            _ => "off"
        };

        private static string ChipIcon(string group) => group switch
        {
            "ready" => "check",
            "waiting" => "clock",
            "paused" => "pause",
            "running" => "sync",
            "failed" => "alert",
            _ => "power"
        };

        private static string ChipKey(string group) => group switch
        {
            "ready" => "jobs.state.Idle",
            "waiting" => "jobs.state.Pending",
            "paused" => "jobs.state.Paused",
            "running" => "jobs.state.Running",
            "failed" => "jobs.state.Failed",
            _ => "jobs.state.Off"
        };

        // The icon and tone say the state as well as its colour does.
        private IUiComponent StateChipOf(WorkerJobV1 job)
        {
            var (tone, icon) = job.WorkerJobStateTypeId switch
            {
                WorkerJobStateType.Idle => (UiTone.Success, "check"),
                WorkerJobStateType.Running => (UiTone.Info, "sync"),
                WorkerJobStateType.Paused => (UiTone.Warning, "pause"),
                WorkerJobStateType.Failed => (UiTone.Danger, "alert"),
                WorkerJobStateType.Off => (UiTone.Neutral, "power"),
                _ => (UiTone.Accent, "clock")
            };

            return new StateChip(new Text("jobs.state." + job.WorkerJobStateTypeId), tone, Glyph(icon, 14), job.WorkerJobStateTypeId == WorkerJobStateType.Running);
        }

        private ListRow Row(WorkerJobV1 job, string? csrf, bool canControl)
        {
            var (icon, tone) = Kinds.GetValueOrDefault(job.Key, ("settings", UiTone.Accent));
            var child = job.ParentKey is not null;
            var drawer = "job-" + job.Key;
            var name = new Text("jobs.job." + job.Key);
            var subline = child ? new Text("jobs.job." + job.ParentKey) : (IUiComponent)new Span(new Text("jobs.worker." + job.WorkerGroupTypeId), new ValueText(" · "), Cadence(job.IntervalSeconds));
            var note = job.LastErrorKey is { } failure ? new Truncated(Failure(failure)) : null;
            var identity = new ListIdentity(new IconTile(Glyph(icon, child ? 15 : 18), tone, child), ListIdentity.TitleButton(name, drawer), subline, note);

            var waits = job.CanPauseWhileUsed && job.PausesWhileUsed;
            var row = new ListRow([identity, StateChipOf(job), Time(job.LastFinishedAt), Time(job.NextRunAt), waits ? new Tag(new Text("jobs.use.waits")).Warning() : new Span(), RowActions(job, csrf, canControl)],
                Caption(job, waits), FilterText(job));
            if (child) row.Child();
            if (job.WorkerJobStateTypeId == WorkerJobStateType.Paused) row.Tinted(UiTone.Warning);
            if (job.WorkerJobStateTypeId == WorkerJobStateType.Failed) row.Tinted(UiTone.Danger);
            return row;
        }

        // The words a filter looks in, as the server sees them.
        private string FilterText(WorkerJobV1 job) => string.Join(' ', texts["jobs.job." + job.Key], texts["jobs.worker." + job.WorkerGroupTypeId], texts["jobs.about." + job.Key]);

        // Pausing and resuming come first, running at once follows, then the details.
        private IUiComponent RowActions(WorkerJobV1 job, string? csrf, bool canControl)
        {
            var actions = new List<IUiComponent>();
            if (canControl)
            {
                actions.Add(Post("Pause", csrf, [("key", job.Key), ("isPaused", job.IsPaused ? "false" : "true")],
                    new Button(Glyph(job.IsPaused ? "play" : "pause", 14), new Text(job.IsPaused ? "jobs.resume" : "jobs.pause")).Compact().Submit()));
                if (job.CanRunNow)
                {
                    var runNow = new Button(Glyph("play", 14), new Text("jobs.run")).Compact().Soft().Submit();
                    actions.Add(Post("Run", csrf, [("key", job.Key)], job.IsPaused || job.WorkerJobStateTypeId == WorkerJobStateType.Running ? runNow.Disabled() : runNow));
                }
            }

            actions.Add(Drawer.Opener(new Button(Glyph("dots", 18)).IconOnly(string.Format(CultureInfo.CurrentCulture, texts["jobs.detailsOf"], texts["jobs.job." + job.Key])), "job-" + job.Key));
            return new Div([.. actions]).Class("list-actions");
        }

        // A phone card's line of the facts its columns would show.
        private IReadOnlyList<IUiComponent> Caption(WorkerJobV1 job, bool waits)
        {
            var parts = new List<IUiComponent> { ListPanel.Fact("jobs.last", Time(job.LastFinishedAt), Glyph("clock", 14)) };
            if (job.NextRunAt is not null) parts.Add(ListPanel.Fact("jobs.next", Time(job.NextRunAt)));
            if (waits) parts.Add(new Span(new Text("jobs.use.setting")));
            return parts;
        }

        // One job's details in a drawer: its state, when it ran, what it is for and its controls.
        private IUiComponent Details(WorkerJobV1 job, string? csrf, bool canControl)
        {
            var interval = job.ParentKey is { } parentKey ? _jobs.FirstOrDefault(other => other.Key == parentKey)?.IntervalSeconds : job.IntervalSeconds;
            var body = new List<IUiComponent>
            {
                Drawer.State("table.status", StateChipOf(job)),
                Drawer.Facts(("jobs.last", Time(job.LastFinishedAt)), ("jobs.next", Time(job.NextRunAt)), ("jobs.area", new Text("jobs.worker." + job.WorkerGroupTypeId)), ("jobs.interval", Cadence(interval)))
            };
            if (job.LastErrorKey is { } key) body.Add(Drawer.Block("jobs.error", new P(Time(job.LastFinishedAt), new ValueText(" · "), new ValueText(Failure(key)))));
            body.Add(Drawer.Block("jobs.about", new P(new Text("jobs.about." + job.Key))));
            if (job.Key == "bookTranslations" && _translation is not null)
            {
                body.Add(Drawer.Block("translation.progress.title", new Div([.. TranslationProgressView.Summary(_translation, texts)])));
                body.Add(Drawer.Block("translation.progress.more", new A(new Text("translation.progress.open")).Attribute("href", "/Admin/Instance/Translation")));
            }

            var footer = new List<IUiComponent>();
            if (canControl)
            {
                body.Add(Drawer.Setting("jobs.enabled", Post("Pause", csrf, [("key", job.Key), ("isPaused", job.IsPaused ? "false" : "true")], new SwitchButton(!job.IsPaused, "jobs.enabled"))));
                if (job.CanPauseWhileUsed)
                {
                    body.Add(Drawer.Setting("jobs.use.setting", Post("Use", csrf, [("key", job.Key), ("pausesWhileUsed", job.PausesWhileUsed ? "false" : "true")], new SwitchButton(job.PausesWhileUsed, "jobs.use.setting"))));
                }

                footer.Add(Post("Pause", csrf, [("key", job.Key), ("isPaused", job.IsPaused ? "false" : "true")], new Button(Glyph(job.IsPaused ? "play" : "pause", 14), new Text(job.IsPaused ? "jobs.resume" : "jobs.pause")).Submit()));
                if (job.CanRunNow)
                {
                    var runNow = new Button(Glyph("play", 14), new Text(job.WorkerJobStateTypeId == WorkerJobStateType.Failed ? "jobs.runAgain" : "jobs.run")).Primary().Submit();
                    footer.Add(Post("Run", csrf, [("key", job.Key)], job.IsPaused || job.WorkerJobStateTypeId == WorkerJobStateType.Running ? runNow.Disabled() : runNow));
                }
            }

            return new Drawer(assets, "job-" + job.Key, [new DrawerPane("details", "jobs.job." + job.Key, "jobs.job." + job.Key, "info", body, footer)], sheet: true);
        }

        private ListRow HistoryRow(ActivityItemV1 item)
        {
            var subline = item.OperationTypeId == OperationType.Job ? (IUiComponent)new Text("jobs.job." + item.Title) : new ValueText(item.Title);
            var failed = item.OperationStatusTypeId == OperationStatusType.Failed;
            var note = failed ? new Truncated(HistoryFailure(item)) : null;
            var identity = new ListIdentity(new IconTile(Glyph("history", 18), UiTone.Accent), new Text("operation.type." + item.OperationTypeId), subline, note);
            var (tone, icon) = item.OperationStatusTypeId switch
            {
                OperationStatusType.Completed => (UiTone.Success, "check"),
                OperationStatusType.Failed => (UiTone.Danger, "alert"),
                OperationStatusType.Running => (UiTone.Info, "sync"),
                _ => (UiTone.Accent, "clock")
            };

            var finished = Time(item.FinishedAt ?? item.CreatedAt);
            return new ListRow([identity, new StateChip(new Text("operation.status." + item.OperationStatusTypeId), tone, Glyph(icon, 14), item.OperationStatusTypeId == OperationStatusType.Running), finished],
                [ListPanel.Fact("jobs.when", finished, Glyph("clock", 14))]);
        }

        // The text of a failure's error code; a code without text reads as an unknown error, never as the raw exception.
        private string Failure(string key) => texts.Has("errors." + key) ? texts["errors." + key] : texts["errors.internal_error"];

        // A failed history entry says why: the outcome of a download, the error of anything else, an unknown error when none was kept.
        private string HistoryFailure(ActivityItemV1 item) =>
            item.ReasonKey is { } key && LiveEntities.Reason(texts, item.OperationTypeId, key) is { Length: > 0 } reason ? reason : texts["errors.internal_error"];

        // A form posts to the list as it is shown, so its answer returns to the same chip, filter words and page.
        private IUiComponent Post(string handler, string? csrf, (string Name, string Value)[] hidden, IUiComponent button)
        {
            var here = Here;
            return new PostForm(here + (here.Contains('?') ? "&" : "?") + "handler=" + handler, csrf, hidden, button);
        }

        private string Here => Link(StateFilter, QueryFilter, PageNumber);

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        private static IUiComponent Time(DateTime? value) => value is { } time
            ? new Span(new LocalTime(time)).Class("list-value")
            : new Span(new ValueText("–")).Class("list-value list-value--dim");

        // The address of the list with its chip and filter words.
        private static string Link(string? state, string? query, int page = 1)
        {
            var values = new List<string>();
            if (state is not null) values.Add("state=" + state);
            if (!string.IsNullOrEmpty(query)) values.Add("q=" + Uri.EscapeDataString(query));
            if (page > 1) values.Add("pageNumber=" + page.ToString(CultureInfo.InvariantCulture));
            return values.Count == 0 ? Address : Address + "?" + string.Join('&', values);
        }

        // How often the job looks: the Owner's interval, off, or when work arrives.
        private static IUiComponent Cadence(int? seconds) => seconds switch
        {
            null => new Text("jobs.continuous"),
            0 => new Text("jobs.off"),
            { } value => new ValueText(Interval(value))
        };

        private static string Interval(int seconds) => seconds switch
        {
            >= 3600 => (seconds / 3600).ToString(CultureInfo.CurrentCulture) + " h",
            >= 60 => (seconds / 60).ToString(CultureInfo.CurrentCulture) + " min",
            _ => seconds.ToString(CultureInfo.CurrentCulture) + " s"
        };

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostPauseAsync([BindRequired] string key, bool isPaused)
        {
            await pause.ExecuteAsync<UpdateResult>(new JobParametersV1(key), new PauseWorkerJobDataV1(isPaused), HttpContext.RequestAborted);
            return LocalRedirect(Here);
        }

        // Every job that is not yet in the wanted state, one by one.
        public async Task<IActionResult> OnPostPauseAllAsync(bool isPaused)
        {
            var ct = HttpContext.RequestAborted;
            var jobs = await read.ExecuteAsync<BaseDataList<WorkerJobV1>>(new ReadWorkerJobsParametersV1(), ct);
            foreach (var job in jobs.Where(job => job.IsPaused != isPaused))
            {
                await pause.ExecuteAsync<UpdateResult>(new JobParametersV1(job.Key), new PauseWorkerJobDataV1(isPaused), ct);
            }

            return LocalRedirect(Here);
        }

        public async Task<IActionResult> OnPostRunAsync([BindRequired] string key)
        {
            await run.ExecuteAsync<UpdateResult>(new JobParametersV1(key), default, HttpContext.RequestAborted);
            return LocalRedirect(Here);
        }

        public async Task<IActionResult> OnPostUseAsync([BindRequired] string key, bool pausesWhileUsed)
        {
            await use.ExecuteAsync<UpdateResult>(new JobParametersV1(key), new WorkerJobUseDataV1(pausesWhileUsed), HttpContext.RequestAborted);
            return LocalRedirect(Here);
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _jobs = await read.ExecuteAsync<BaseDataList<WorkerJobV1>>(new ReadWorkerJobsParametersV1(), ct);
            _health = await readHealth.ExecuteAsync<HealthV1>(new ReadHealthParametersV1(), ct);
            if (_jobs.Any(job => job.Key == "bookTranslations") && texts.Can("instance.settings.read"))
            {
                try
                {
                    _translation = await readTranslation.ExecuteAsync<BookTranslationProgressV1>(cancellationToken: ct);
                }
                catch (global::System.Security.SecurityException)
                {
                    // The Translation module is off: the job has nothing to show.
                }
            }

            if (texts.Can("media.read"))
            {
                _history = await readHistory.ExecuteAsync<PageResult<ActivityItemV1>>(new ReadActivityParametersV1(ActivityFilterType.History, 1, 10), ct);
            }
        }
    }
}
