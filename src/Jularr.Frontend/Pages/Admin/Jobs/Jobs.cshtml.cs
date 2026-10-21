using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Worker.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Backend.Service.Admin.Worker.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Jobs
{
    // Every background job with its state and the Owner's controls, and below it what the operations did.
    public sealed class JobsModel(ReadWorkerJobsV1 read, PauseWorkerJobV1 pause, RunWorkerJobV1 run, SetWorkerJobUseV1 use, ReadActivityV1 readHistory, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string Address = "/Admin/Jobs";
        private IReadOnlyList<WorkerJobV1> _jobs = [];
        private PageResult<ActivityItemV1>? _history;

        protected override void Define(Page page)
        {
            page.Title("admin.jobs.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Jobs/Jobs.entry.scss");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var canControl = texts.Can("instance.settings.update");
            var table = new Table<WorkerJobV1>(_jobs)
                .Content("jobs.job", item => new Div(new Text("jobs.job." + item.Key),
                    new Span(new Text("jobs.worker." + item.WorkerGroupTypeId), new ValueText(" · "), item.IntervalSeconds is { } seconds ? new ValueText(Interval(seconds)) : new Text("jobs.continuous"))).Class("jobs-job"))
                .Content("table.status", item => new Div(
                    new Span(new Text("jobs.state." + item.WorkerJobStateTypeId)).Class("state-chip jobs-state state-chip--" + item.WorkerJobStateTypeId.ToString().ToLowerInvariant()),
                    item.LastErrorKey is { } key ? new Span(new Text("errors." + key)) : new Span()).Class("jobs-status"))
                .Content("jobs.last", item => Time(item.LastFinishedAt))
                .Content("jobs.next", item => Time(item.NextRunAt))
                .Content("jobs.use", item => item.CanPauseWhileUsed ? Use(item, csrf, canControl) : new Span());
            if (canControl)
            {
                table = table.Content("common.actions", item => Actions(item, csrf));
            }

            page.Content.Add(table);

            // What the queue did, newest first.
            if (_history is { } history)
            {
                page.Content.Add(new UiSection("jobs.history", history.Items.Count > 0
                    ? [new Table<ActivityItemV1>(history.Items)
                        .Content("jobs.when", item => Time(item.FinishedAt ?? item.CreatedAt))
                        .Content("table.type", item => item.OperationTypeId == OperationType.Job
                            ? new Div(new Text("operation.type.Job"), new Span(new Text("jobs.job." + item.Title))).Class("jobs-job")
                            : new Div(new Text("operation.type." + item.OperationTypeId), new Span(new ValueText(item.Title))).Class("jobs-job"))
                        .Content("table.status", item => new Text("operation.status." + item.OperationStatusTypeId)),
                        new A(new Text("jobs.allActivity")).Attribute("href", "/Admin/Activity?Filter=History")]
                    : [new P(new Text("jobs.history.empty"))]));
            }

            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        // Pausing and resuming come first, running at once follows.
        private static IUiComponent Actions(WorkerJobV1 item, string? csrf)
        {
            var actions = new List<IUiComponent>
            {
                Post("Pause", csrf, [("key", item.Key), ("isPaused", item.IsPaused ? "false" : "true")], new Button(new Text(item.IsPaused ? "jobs.resume" : "jobs.pause")).Compact().Submit())
            };
            if (item.CanRunNow)
            {
                var runNow = new Button(new Text("jobs.run")).Compact().Submit();
                actions.Add(Post("Run", csrf, [("key", item.Key)], item.IsPaused ? runNow.Disabled() : runNow));
            }

            return new Div([.. actions]).Class("jobs-actions");
        }

        // The setting is a button that shows the choice and flips it.
        private static IUiComponent Use(WorkerJobV1 item, string? csrf, bool canControl)
        {
            var label = new Text(item.PausesWhileUsed ? "jobs.use.waits" : "jobs.use.runs");
            return canControl
                ? Post("Use", csrf, [("key", item.Key), ("pausesWhileUsed", item.PausesWhileUsed ? "false" : "true")], new Button(label).Compact().Submit())
                : label;
        }

        private static IUiComponent Post(string handler, string? csrf, (string Name, string Value)[] hidden, IUiComponent button) => new PostForm(Address + "?handler=" + handler, csrf, hidden, button);

        private static IUiComponent Time(DateTime? value) => new ValueText(value is { } time ? time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "–");

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
            return LocalRedirect(Address);
        }

        public async Task<IActionResult> OnPostRunAsync([BindRequired] string key)
        {
            await run.ExecuteAsync<UpdateResult>(new JobParametersV1(key), default, HttpContext.RequestAborted);
            return LocalRedirect(Address);
        }

        public async Task<IActionResult> OnPostUseAsync([BindRequired] string key, bool pausesWhileUsed)
        {
            await use.ExecuteAsync<UpdateResult>(new JobParametersV1(key), new WorkerJobUseDataV1(pausesWhileUsed), HttpContext.RequestAborted);
            return LocalRedirect(Address);
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _jobs = await read.ExecuteAsync<BaseDataList<WorkerJobV1>>(new ReadWorkerJobsParametersV1(), ct);
            if (texts.Can("media.read"))
            {
                _history = await readHistory.ExecuteAsync<PageResult<ActivityItemV1>>(new ReadActivityParametersV1(ActivityFilterType.History, 1, 10), ct);
            }
        }
    }
}
