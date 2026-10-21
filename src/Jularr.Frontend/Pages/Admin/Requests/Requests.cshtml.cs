using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Request.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Request.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Requests
{
    // Real queue and moderation on the list kit: figures, filter chips with their counts, one row per request with its live state and a drawer of its details with the decision.
    public sealed class RequestsModel(ReadRequestsV1 read, ReadRequestsSummaryV1 readSummary, UpdateRequestV1 update, IAntiforgery antiforgery, FrontendAssets assets, UiText texts) : JularrPage
    {
        private PageResult<RequestDetailsV1>? _requests;
        private RequestsSummaryV1? _summary;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public RequestTabType Tab { get; set; }

        protected override void Define(Page page)
        {
            page.Title("admin.requests.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Requests/Requests.entry.scss");
            page.Asset("Framework/Components/LiveProgress.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            page.Asset("Framework/Components/Drawer.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            page.Content.Add(new ListToolbar(new NavigationTabs([.. Enum.GetValues<RequestTabType>().Select(tab => new NavigationTab("request.tab." + tab, Address(tab: tab), tab == Tab, Count: CountOf(tab), Icon: Glyph(TabIcon(tab), 14)))], "request.tabs", "filters")));
            if (_requests is { } requests)
            {
                var columns = new List<ListColumn> { new("table.work", ListRole.Main), new("table.status", ListRole.Status), new("table.requester", ListRole.Data, 1), new("request.language", ListRole.Data, 1), new("table.requestedAt", ListRole.Data, 2), new("common.actions", ListRole.Actions) };
                var empty = new Div(new IconTile(Glyph("search", 22), UiTone.Neutral), new P(new Text("request.empty")));
                page.Content.Add(new ListPanel("requests.list", columns, [.. requests.Items.Select(item => Row(item, csrf))], empty).Modifier("requests-list"));
                page.Content.AddRange(requests.Items.Select(item => DrawerOf(item, csrf)));

                var total = CountOf(Tab);
                var pages = total is { } count ? Math.Max(1, (count + requests.PageSize - 1) / requests.PageSize) : requests.Page + (requests.Items.Count == requests.PageSize ? 1 : 0);
                if (pages > 1 || requests.Page > 1)
                {
                    page.Content.Add(new ActionDock(assets, "requests.actions", paging: new DockPaging(requests.Page > 1 ? Address(requests.Page - 1, Tab) : null, requests.Page < pages ? Address(requests.Page + 1, Tab) : null,
                        new ValueText(string.Format(CultureInfo.CurrentCulture, texts["dock.page"], requests.Page, pages)))));
                }
            }

            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        private static string TabIcon(RequestTabType tab) => tab switch
        {
            RequestTabType.Open => "clock",
            RequestTabType.Approved => "check",
            RequestTabType.InProgress => "download",
            RequestTabType.Done => "library",
            RequestTabType.Rejected => "close",
            _ => "layers"
        };

        private int? CountOf(RequestTabType tab) => tab switch
        {
            RequestTabType.Open => _summary?.OpenCount,
            RequestTabType.Approved => _summary?.ApprovedCount,
            RequestTabType.InProgress => _summary?.InProgressCount,
            RequestTabType.Done => _summary?.DoneCount,
            RequestTabType.Rejected => _summary?.RejectedCount,
            _ => _summary?.TotalCount
        };

        private IUiComponent TypeTile(RequestDetailsV1 item)
        {
            var (icon, tone) = item.MediaTypeId switch
            {
                MediaType.Movie => ("play", UiTone.Info),
                MediaType.Series => ("layers", UiTone.Accent),
                MediaType.Book => ("book", UiTone.Warning),
                MediaType.Music => ("music", UiTone.Success),
                _ => ("gamepad", UiTone.Danger)
            };

            return new IconTile(Glyph(icon), tone);
        }

        private static string PhaseIcon(AcquisitionRequestStatusType status) => status switch
        {
            AcquisitionRequestStatusType.Searching => "search",
            AcquisitionRequestStatusType.Downloading => "download",
            AcquisitionRequestStatusType.Importing => "sync",
            AcquisitionRequestStatusType.Completed => "check",
            AcquisitionRequestStatusType.Rejected or AcquisitionRequestStatusType.Failed => "alert",
            AcquisitionRequestStatusType.Cancelled => "close",
            _ => "clock"
        };

        // The stream keeps the label, the bar and the numbers current and sets the phase; the pill takes its tone from the phase.
        private IUiComponent Status(RequestDetailsV1 item, bool drawer = false)
        {
            var status = item.AcquisitionRequestStatusTypeId;
            var label = new Span(new ValueText(LiveFacts.Phase(texts, status, null, admin: true))).Class("state-chip__live").Attribute("data-live-text", "phase");
            var parts = new List<IUiComponent>
            {
                new StateChip(label, UiTone.Neutral, Glyph(PhaseIcon(status), 14)),
                new Progress().Attribute("max", "100").Attribute("hidden", "hidden").Attribute("data-live-value", "percent").TextAttribute("aria-label", "live.progress").Class("live-progress__bar"),
                new Span().Class("live-progress__facts").Attribute("data-live-text", "facts").Attribute("hidden", "hidden")
            };

            // Release, indexer, client and why it failed belong to the details.
            if (drawer)
            {
                parts.Add(new Span().Class("live-progress__detail").Attribute("data-live-text", "release").Attribute("hidden", "hidden"));
                parts.Add(new Span().Class("live-progress__detail").Attribute("data-live-text", "source").Attribute("hidden", "hidden"));
                parts.Add(new Span().Class("live-progress__detail live-progress__detail--error").Attribute("data-live-text", "error").Attribute("hidden", "hidden"));
            }

            return new Div([.. parts]).Class("live-progress list-phase requests-status" + (drawer ? " requests-status--drawer" : "")).Attribute("data-live", LiveKeys.AdminRequest(item.Id)).Attribute("data-phase", LiveFacts.Marker(status));
        }

        private ListRow Row(RequestDetailsV1 item, string? csrf)
        {
            var home = "/Admin/Works/" + item.WorkId.ToString(CultureInfo.InvariantCulture);
            var identity = new ListIdentity(TypeTile(item), new A(new Truncated(item.CanonicalTitle)).Class("list-identity__title").Attribute("href", home), new Text("media.type." + item.MediaTypeId));
            var requested = new Span(new ValueText(item.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))).Class("list-value");
            var row = new ListRow([identity, Status(item), new Span(new Truncated(item.RequesterName)).Class("list-value list-value--fill"), new Span(new Truncated(item.RequestedLanguage)).Class("list-value list-value--fill"), requested, RowActions(item, csrf)],
                [ListPanel.Fact("table.requester", new ValueText(item.RequesterName)), ListPanel.Fact("table.requestedAt", requested, Glyph("clock", 14))]);
            if (item.AcquisitionRequestStatusTypeId == AcquisitionRequestStatusType.Open) row.Tinted(UiTone.Warning);
            return row;
        }

        // The decision of an open request comes first, then the details.
        private IUiComponent RowActions(RequestDetailsV1 item, string? csrf)
        {
            var actions = new List<IUiComponent>();
            if (item.AcquisitionRequestStatusTypeId == AcquisitionRequestStatusType.Open)
            {
                actions.Add(Decision(item, AcquisitionRequestStatusType.Approved, csrf, new Button(Glyph("check", 14), new Text("request.approve")).Compact().Soft().Submit()));
                actions.Add(Decision(item, AcquisitionRequestStatusType.Rejected, csrf, new Button(Glyph("close", 14), new Text("request.reject")).Compact().Submit()));
            }

            actions.Add(Drawer.Opener(new Button(Glyph("dots", 18)).IconOnly(texts["requests.details"]), DrawerId(item)));
            return new Div([.. actions]).Class("list-actions");
        }

        private static string DrawerId(RequestDetailsV1 item) => "request-" + item.Id.ToString(CultureInfo.InvariantCulture);

        private IUiComponent DrawerOf(RequestDetailsV1 item, string? csrf)
        {
            var body = new List<IUiComponent>
            {
                Drawer.State("table.status", Status(item, drawer: true)),
                Drawer.Facts(("table.type", new Text("media.type." + item.MediaTypeId)), ("table.requester", new ValueText(item.RequesterName)),
                    ("request.language", new ValueText(item.RequestedLanguage)), ("table.requestedAt", new ValueText(item.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))))
            };

            var footer = new List<IUiComponent>();
            if (item.AcquisitionRequestStatusTypeId == AcquisitionRequestStatusType.Open)
            {
                footer.Add(Decision(item, AcquisitionRequestStatusType.Rejected, csrf, new Button(Glyph("close", 14), new Text("request.reject")).Submit()));
                footer.Add(Decision(item, AcquisitionRequestStatusType.Approved, csrf, new Button(Glyph("check", 14), new Text("request.approve")).Primary().Submit()));
            }

            footer.Add(new A(new Text("requests.openWork")).Class("button").Attribute("href", "/Admin/Works/" + item.WorkId.ToString(CultureInfo.InvariantCulture)));
            return new Drawer(assets, DrawerId(item), [new DrawerPane("details", "requests.details", "requests.details", "info", body, footer, null, item.CanonicalTitle)], sheet: true);
        }

        private IUiComponent Decision(RequestDetailsV1 request, AcquisitionRequestStatusType status, string? csrf, IUiComponent button) =>
            new PostForm(Address(PageNumber, Tab) + "&AcquisitionRequestId=" + request.Id.ToString(CultureInfo.InvariantCulture), csrf,
                [("AcquisitionRequestStatusTypeId", ((byte)status).ToString(CultureInfo.InvariantCulture)), ("Version", request.Version.ToString(CultureInfo.InvariantCulture))], button);

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] long acquisitionRequestId, [BindRequired] AcquisitionRequestStatusType acquisitionRequestStatusTypeId, [BindRequired] long version)
        {
            await update.ExecuteAsync<UpdateResult>(new UpdateRequestParametersV1(acquisitionRequestId), new UpdateRequestDataV1(acquisitionRequestStatusTypeId, version), HttpContext.RequestAborted);
            return LocalRedirect(Address(PageNumber, Tab));
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _requests = await read.ExecuteAsync<PageResult<RequestDetailsV1>>(new ReadRequestsParametersV1(PageNumber, requestTabTypeId: Tab), ct);
            _summary = await readSummary.ExecuteAsync<RequestsSummaryV1>(new ReadRequestsSummaryParametersV1(), ct);
        }

        private static string Address(int pageNumber = 1, RequestTabType tab = RequestTabType.All) => "/Admin/Requests?PageNumber=" + pageNumber + "&Tab=" + tab;
    }
}
