using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Backend.Service.Admin.Translation.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Live;
using Jularr.Frontend.Pages.Admin.Queue;
using Jularr.Frontend.Pages.Admin.Translation;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Tasks
{
    // Jularr's own work, as Sonarr's System → Tasks: scans, backups, media list imports, searches, book translations and the notices of failed background jobs, in three groups,
    // what needs a person first (retry or dismiss), then what runs (the book being translated too, read-only) and what waits. Downloads are the Queue's; the Schedule (the Jobs page) runs and pauses the jobs.
    public sealed class TasksModel(ReadActivityV1 read, ReadBookTranslationProgressV1 readTranslation, RetryActivityV1 retry, DismissActivityV1 dismiss, IAntiforgery antiforgery, UiText texts, FrontendAssets assets) : JularrPage
    {
        private const string Address = "/Admin/Jobs/Tasks";

        // Open tasks are few: one read takes up to the most a read allows.
        private const int TaskSize = PageRequest.MaximumPageSize;

        private PageResult<ActivityItemV1>? _items;
        private BookTranslationProgressV1? _translation;

        private enum TaskGroup { Help, Running, Queued }

        private static readonly ListColumn[] Columns = [new("tasks.task", ListRole.Main), new("table.status", ListRole.Status), new("operation.created", ListRole.Data, 1), new("common.actions", ListRole.Actions)];

        protected override void Define(Page page)
        {
            page.Title("admin.tasks.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Queue/Queue.entry.scss");
            page.Asset("Pages/Admin/Tasks/Tasks.entry.scss");
            page.Asset("Framework/Components/LiveProgress.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            if (_items is not { } items) return;

            // The book being translated runs with the other tasks.
            var groups = new (TaskGroup Group, string Icon, UiTone Tone, List<ListRow> Rows)[]
            {
                (TaskGroup.Help, "alert", UiTone.Danger, [.. items.Items.Where(item => GroupOf(item) == TaskGroup.Help).Select(item => Row(item, csrf))]),
                (TaskGroup.Running, "sync", UiTone.Info, [.. items.Items.Where(item => GroupOf(item) == TaskGroup.Running).Select(item => Row(item, csrf)), .. Translation()]),
                (TaskGroup.Queued, "clock", UiTone.Neutral, [.. items.Items.Where(item => GroupOf(item) == TaskGroup.Queued).Select(item => Row(item, csrf))])
            };

            if (groups.All(group => group.Rows.Count == 0))
            {
                page.Content.Add(new Div(new IconTile(Glyph("check", 22), UiTone.Success), new P(new Text("tasks.empty"))).Class("list-empty"));
            }

            foreach (var group in groups.Where(group => group.Rows.Count > 0))
            {
                var count = new Span(new ValueText(group.Rows.Count.ToString(CultureInfo.CurrentCulture))).Class("activity-group__count");
                page.Content.Add(new Div(new UiSection("tasks.group." + group.Group, [new ListPanel("tasks.group." + group.Group, Columns, group.Rows).Modifier("tasks-list")], Glyph(group.Icon, 18), count))
                    .Class("activity-group").Attribute("id", "tasks-group-" + group.Group));
            }

            if (items.Items.Count == TaskSize) page.Content.Add(new P(new Text("activity.limit")).Class("list-notice"));
            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        // A failed task or one that waits for a person needs help; the rest runs or waits its turn.
        private static TaskGroup GroupOf(ActivityItemV1 item) => item.OperationStatusTypeId == OperationStatusType.Failed || item.IsWaiting ? TaskGroup.Help
            : item.OperationStatusTypeId == OperationStatusType.Running ? TaskGroup.Running
            : TaskGroup.Queued;

        // What a task is by its icon; a background job is named by its key and leads to the Schedule, a media list import to its page, a scan names its library.
        private ListRow Row(ActivityItemV1 item, string? csrf)
        {
            var (icon, tone) = item.OperationTypeId switch
            {
                OperationType.LibraryScan => ("scan", UiTone.Success),
                OperationType.Backup => ("shield", UiTone.Success),
                OperationType.AcquisitionSearch => ("search", UiTone.Info),
                OperationType.MediaImport => ("list", UiTone.Accent),
                OperationType.TranscodeBenchmark => ("sliders", UiTone.Accent),
                _ => ("pulse", UiTone.Accent)
            };
            IUiComponent title = item.OperationTypeId switch
            {
                OperationType.Job => new A(new Text("jobs.job." + item.Title)).Class("list-identity__title").Attribute("href", "/Admin/Jobs"),
                OperationType.MediaImport => new A(new Text("operation.type.MediaImport")).Class("list-identity__title").Attribute("href", "/Admin/Import"),
                _ when item.Title.Length > 0 => new Truncated(item.Title),
                _ => new Text("operation.type." + item.OperationTypeId)
            };

            var created = new LocalTime(item.CreatedAt);
            var row = new ListRow([new ListIdentity(new IconTile(Glyph(icon), tone), title, new Text("operation.type." + item.OperationTypeId)), OperationState.Status(item, texts, assets),
                new Span(created).Class("list-value"), Actions(item, csrf)], [ListPanel.Fact("operation.created", new LocalTime(item.CreatedAt))]);
            if (item.OperationStatusTypeId == OperationStatusType.Failed) row.Tinted(UiTone.Danger);
            else if (item.IsWaiting) row.Tinted(UiTone.Warning);
            return row;
        }

        // A failed scan, backup, import or search runs again; a failed background job is run from the Schedule; a failure is put aside once seen.
        private IUiComponent Actions(ActivityItemV1 item, string? csrf)
        {
            var actions = new List<IUiComponent>();
            if (item.OperationTypeId == OperationType.Job) actions.Add(new A(Glyph("pulse", 18)).Class("button button--icon").Attribute("href", "/Admin/Jobs").TextAttribute("aria-label", "jobs.open").TextAttribute("title", "jobs.open"));
            else if (item.CanRetry) actions.Add(Action(item, "Retry", "sync", "activity.retry", csrf));
            if (item.CanRetry) actions.Add(Action(item, "Dismiss", "close", "activity.dismiss", csrf));
            return new Div([.. actions]).Class("list-actions");
        }

        private IUiComponent Action(ActivityItemV1 item, string handler, string icon, string labelKey, string? csrf) =>
            new PostForm(Address + "?handler=" + handler + "&operationId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, [], new Button(Glyph(icon, 18)).IconOnly(texts[labelKey]).Submit().Attribute("title", texts[labelKey]));

        // The book the translation job works on now, read-only: its title leads to the book, the line says where in it the job is.
        private IEnumerable<ListRow> Translation()
        {
            if (_translation is not { Current: { } current } progress) yield break;
            var book = progress.Books.FirstOrDefault(entry => entry.Title == current.Title && entry.Volume == current.Volume);
            IUiComponent title = book is null ? new Truncated(current.Title)
                : new A(new Truncated(current.Title)).Class("list-identity__title").Attribute("href", "/Admin/Works/" + book.WorkId.ToString(CultureInfo.InvariantCulture));
            var state = new Div(new StateChip(new ValueText(LiveEntities.OperationStatus(texts, false, OperationStatusType.Running)), UiTone.Neutral, Glyph("sync", 14)),
                new Span(new ValueText(TranslationProgressView.Where(current, texts))).Class("live-progress__facts")).Class("live-progress list-phase activity-status").Attribute("data-phase", "running");
            var open = new A(Glyph("link", 18)).Class("button button--icon").Attribute("href", "/Admin/Instance/Translation").TextAttribute("aria-label", "translation.progress.open").TextAttribute("title", "translation.progress.open");
            yield return new ListRow([new ListIdentity(new IconTile(Glyph("globe"), UiTone.Accent), title, new Text("tasks.translation")), state, new Span(new ValueText("–")).Class("list-value list-value--dim"), new Div(open).Class("list-actions")]);
        }

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostRetryAsync([BindRequired] long operationId)
        {
            await retry.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), default, HttpContext.RequestAborted);
            return LocalRedirect(Address);
        }

        public async Task<IActionResult> OnPostDismissAsync([BindRequired] long operationId)
        {
            await dismiss.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), default, HttpContext.RequestAborted);
            return LocalRedirect(Address);
        }

        protected override async Task LoadDataAsync()
        {
            _items = await read.ExecuteAsync<PageResult<ActivityItemV1>>(new ReadActivityParametersV1(pageSize: TaskSize, tasksOnly: true), HttpContext.RequestAborted);
            if (texts.Can("instance.settings.read"))
            {
                try
                {
                    _translation = await readTranslation.ExecuteAsync<BookTranslationProgressV1>(cancellationToken: HttpContext.RequestAborted);
                }
                catch (global::System.Security.SecurityException)
                {
                    // The Translation module is off: nothing is translated.
                }
            }
        }
    }
}
