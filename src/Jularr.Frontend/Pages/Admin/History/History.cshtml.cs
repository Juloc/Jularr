using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.History
{
    // What happened to the media, newest first; a Work's page opens its own history.
    public sealed class HistoryModel(ReadActivityEventsV1 read, UiText texts) : JularrPage
    {
        private PageResult<ActivityEventV1>? _events;

        [BindProperty(SupportsGet = true)]
        public long? WorkId { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            var work = WorkId is not null ? _events?.Items.FirstOrDefault()?.WorkTitle : null;
            page.Title("admin.history.title", work);
            if (work is not null) page.Parent("admin.history.title", "/Admin/History");
            page.Navigation("nav.admin");
            if (_events is not { } events)
            {
                return;
            }

            page.Content.Add(new Table<ActivityEventV1>(events.Items)
                .Text(item => item.OccurredAt, "history.when")
                .Resource(item => item.ActivityEventTypeId, "history.event", type => "history.event." + type)
                .Content("table.work", item => new Div(
                    item.WorkId is { } workId ? new A(new ValueText(item.WorkTitle)).Attribute("href", "/Admin/Works/" + workId.ToString(CultureInfo.InvariantCulture)) : new ValueText(item.WorkTitle),
                    new Span(new ValueText(MediaUnitLabel.Format(texts, item.WorkUnitTypeId, item.UnitNumber, item.ParentUnitNumber) ?? ""))))
                .Content("history.release", item => new Div(new ValueText(item.ReleaseTitle ?? ""), new Span(new ValueText(item.QualityKey ?? ""))))
                .Content("history.source", item => new ValueText(string.Join(" · ", new[] { item.IndexerName, item.ClientName }.OfType<string>())))
                .Content("history.file", item => new Div(
                    item.PreviousPath is { } before ? new Span(new ValueText(before + " → ")) : new Span(),
                    new ValueText(item.Path ?? ""),
                    item.ReasonKey is { } reason ? new Span(new Text("acquisition.outcome." + reason)) : new Span()))
                .Content("history.by", item => new ValueText(item.AccountName ?? texts["history.byJularr"])));
            if (events.Items.Count == 0) page.Content.Add(new P(new Text("history.empty")));
            if (events.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", Address(events.Page - 1)));
            if (events.Items.Count == events.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", Address(events.Page + 1)));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _events = await read.ExecuteAsync<PageResult<ActivityEventV1>>(new ReadActivityEventsParametersV1(WorkId, Math.Max(1, PageNumber)), HttpContext.RequestAborted);
        }

        private string Address(int page) =>
            "/Admin/History?PageNumber=" + page.ToString(CultureInfo.InvariantCulture) + (WorkId is { } work ? "&WorkId=" + work.ToString(CultureInfo.InvariantCulture) : "");
    }
}
