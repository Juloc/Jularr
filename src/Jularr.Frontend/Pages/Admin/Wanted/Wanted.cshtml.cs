using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Wanted
{
    // Shared Work targets, not requests, on the list kit; each opens on its own page. Rows are chosen for a priority or a search of their own, in the dock's bulk zone.
    public sealed class WantedModel(ReadWantedV1 read, ReadWantedSummaryV1 readSummary, UpdateWantedV1 update, UpdateWantedPriorityV1 updatePriority, StartWantedSearchV1 startSearch, IAntiforgery antiforgery, UiText texts, FrontendAssets assets) : JularrPage
    {
        // The form that the chosen rows belong to; the dock's buttons carry it.
        private const string BulkForm = "wanted-bulk";
        private PageResult<WantedDetailsV1>? _wanted;
        private WantedSummaryV1? _summary;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public WantedViewType View { get; set; }

        protected override void Define(Page page)
        {
            page.Title("admin.wanted.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Wanted/Wanted.entry.scss");
            page.Asset("Framework/Components/Truncated.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var manage = texts.CanManageWanted();
            var acquire = texts.CanAcquire();

            if (TempData["WantedBulk"] is string done) page.Content.Add(new P(new Text(done)).Class("list-notice").Attribute("role", "status"));
            page.Content.Add(new ListToolbar(new NavigationTabs([.. Enum.GetValues<WantedViewType>().Select(view => new NavigationTab("wanted.view." + view, ViewAddress(view), view == View, CountOf(view), Glyph(ViewIcon(view), 14)))], "wanted.views", "filters")));
            if (_wanted is { } wanted)
            {
                var empty = new Div(new IconTile(Glyph("search", 22), UiTone.Neutral), new P(new Text("wanted.empty")));
                var columns = new List<ListColumn> { new("table.work", ListRole.Main, Head: manage ? ListIdentity.SelectAllHead(new Text("table.work"), "wanted.selectAll") : null), new("table.status", ListRole.Status), new("wanted.priority"), new("request.language", ListRole.Data, 2), new("wanted.lastSearch", ListRole.Data, 1), new("common.actions", ListRole.Actions) };
                page.Content.Add(new ListPanel("wanted.list", columns, [.. wanted.Items.Select(item => Row(item, acquire, manage, wanted.Page, csrf))], empty).Modifier("wanted-list"));
                if (manage && wanted.Items.Count > 0) page.Content.Add(BulkFormElement(csrf));
                page.Content.Add(Dock(wanted, manage, acquire));
            }

            if (FormError is not null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
        }

        private static string ViewIcon(WantedViewType view) => view switch
        {
            WantedViewType.Missing => "alert",
            WantedViewType.CutoffUnmet => "sliders",
            _ => "layers"
        };

        private int? CountOf(WantedViewType view) => view switch
        {
            WantedViewType.Missing => _summary?.MissingCount,
            WantedViewType.CutoffUnmet => _summary?.CutoffUnmetCount,
            _ => _summary?.TotalCount
        };

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        // The icon and tone of what a target is: its media type.
        private IUiComponent TypeTile(WantedDetailsV1 item)
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

        // The state of a target as its pill says it: paused, below its cutoff, available, on its way or still to find.
        private IUiComponent State(WantedDetailsV1 item)
        {
            var (tone, icon) = item.IsPaused ? (UiTone.Warning, "pause")
                : View == WantedViewType.CutoffUnmet && item.HasMedia ? (UiTone.Info, "sliders")
                : item.HasMedia ? (UiTone.Success, "check")
                : item.IsDownloading ? (UiTone.Info, "download")
                : item.OutcomeKey is not null ? (UiTone.Warning, "alert")
                : (UiTone.Accent, "clock");
            IUiComponent label = View == WantedViewType.CutoffUnmet && item.HasMedia && !item.IsPaused ? new Text("wanted.belowCutoff") : WantedStatus.Of(item);
            return new StateChip(label, tone, Glyph(icon, 14));
        }

        // High targets are searched first, Normal ones after them; nothing waits. Only the exception is marked: a normal target shows nothing.
        private static IUiComponent Priority(WantedDetailsV1 item) => item.WantedPriorityTypeId == WantedPriorityType.High
            ? new Tag(new Text("wanted.priority.High")).Accent()
            : new Span();

        private ListRow Row(WantedDetailsV1 item, bool acquire, bool manage, int pageNumber, string? csrf)
        {
            var name = item.PlatformName is { } platform ? item.CanonicalTitle + " · " + platform : item.CanonicalTitle;
            var identity = new ListIdentity(TypeTile(item), new A(new Truncated(name)).Class("list-identity__title").Attribute("href", WantedStatus.Home(item.Id)), new Text(MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId)),
                select: manage ? ListIdentity.Select("wantedItemIds", item.Id.ToString(CultureInfo.InvariantCulture), BulkForm, "wanted.select") : null);
            var languages = string.Join(", ", item.RequestedLanguages);
            var searched = item.LastSearchedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            var row = new ListRow([identity, State(item), Priority(item), languages.Length > 0 ? new Span(new Truncated(languages)).Class("list-value list-value--fill") : Dim(),
                searched is not null ? new Span(new ValueText(searched)).Class("list-value") : Dim(), Actions(item, acquire, manage, pageNumber, csrf)],
                [.. item.WantedPriorityTypeId == WantedPriorityType.High ? new[] { ListPanel.Fact("wanted.priority", Priority(item)) } : Array.Empty<IUiComponent>(), ListPanel.Fact("wanted.lastSearch", searched is not null ? new ValueText(searched) : new ValueText("–"), Glyph("clock", 14))]);
            if (item.IsPaused) row.Tinted(UiTone.Warning);
            return row;
        }

        private static IUiComponent Dim() => new Span(new ValueText("–")).Class("list-value list-value--dim");

        // The chosen rows belong to this form; the dock's buttons name it.
        private IUiComponent BulkFormElement(string? csrf) =>
            new PostForm(Address(PageNumber, "Bulk"), csrf, []).Attribute("id", BulkForm);

        // A search of every target that is due is the page's action; a priority or a search of the chosen rows is the bulk zone's.
        private ActionDock Dock(PageResult<WantedDetailsV1> wanted, bool manage, bool acquire)
        {
            var actions = new List<IUiComponent>();
            if (acquire && wanted.Items.Count > 0) actions.Add(BulkButton("Search", "search", "wanted.search.all", true));

            var bulk = new List<IUiComponent>();
            if (manage)
            {
                bulk.Add(BulkButton(nameof(WantedPriorityType.High), "star", "wanted.priority.setHigh", false));
                bulk.Add(BulkButton(nameof(WantedPriorityType.Normal), "list", "wanted.priority.setNormal", false));
                if (acquire) bulk.Add(BulkButton("Search", "search", "wanted.search.selected", true));
            }

            DockPaging? paging = null;
            var total = CountOf(View);
            var pages = total is { } count ? Math.Max(1, (count + wanted.PageSize - 1) / wanted.PageSize) : wanted.Page + (wanted.Items.Count == wanted.PageSize ? 1 : 0);
            if (pages > 1 || wanted.Page > 1)
            {
                paging = new DockPaging(wanted.Page > 1 ? Address(wanted.Page - 1) : null, wanted.Page < pages ? Address(wanted.Page + 1) : null,
                    new ValueText(string.Format(CultureInfo.CurrentCulture, texts["dock.page"], wanted.Page, pages)));
            }

            return new ActionDock(assets, "wanted.actions", actions, bulk, paging);
        }

        private IUiComponent BulkButton(string value, string icon, string labelKey, bool primary)
        {
            var button = new Button(Glyph(icon, 16), new Span(new Text(labelKey)).Class("action-dock__text"));
            if (primary) button.Primary();
            return button.Submit().Attribute("form", BulkForm).Attribute("name", "bulk").Attribute("value", value);
        }

        // Search by hand, the target's own page, and pausing; a paused target is not searched.
        private IUiComponent Actions(WantedDetailsV1 item, bool acquire, bool manage, int pageNumber, string? csrf)
        {
            var actions = new List<IUiComponent>();
            var name = item.CanonicalTitle;
            if (acquire) actions.Add(IconLink("search", "wanted.searchManual", WantedStatus.Home(item.Id) + "/Search", name));
            actions.Add(IconLink("pencil", "wanted.edit", WantedStatus.Home(item.Id), name));
            if (manage)
            {
                var label = texts[item.IsPaused ? "wanted.resume" : "wanted.pause"] + " " + name;
                actions.Add(new PostForm(Address(pageNumber, "Pause") + "&wantedItemId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf,
                    [("isPaused", (!item.IsPaused).ToString().ToLowerInvariant()), ("version", item.Version.ToString(CultureInfo.InvariantCulture)),
                        ("acquisitionProfileId", item.AcquisitionProfileId?.ToString(CultureInfo.InvariantCulture) ?? ""), ("libraryRootId", item.LibraryRootId?.ToString(CultureInfo.InvariantCulture) ?? "")],
                    new Button(Glyph(item.IsPaused ? "play" : "pause", 18)).IconOnly(label).Submit().Attribute("title", label)));
            }

            return new Div([.. actions]).Class("list-actions");
        }

        private IUiComponent IconLink(string icon, string labelKey, string href, string name) =>
            new A(Glyph(icon, 18)).Class("button button--icon").Attribute("href", href).Attribute("aria-label", texts[labelKey] + " " + name).Attribute("title", texts[labelKey] + " " + name);

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostPauseAsync([BindRequired] long wantedItemId, [BindRequired] bool isPaused, [BindRequired] long version, long? acquisitionProfileId,
            long? libraryRootId)
        {
            await update.ExecuteAsync<UpdateResult>(new UpdateWantedParametersV1(wantedItemId), new UpdateWantedDataV1(isPaused, version, acquisitionProfileId, libraryRootId),
                HttpContext.RequestAborted);
            return LocalRedirect(Address(PageNumber));
        }

        // The chosen rows take a priority, or are searched now; with none chosen a priority changes nothing and the search takes every target that is due.
        public async Task<IActionResult> OnPostBulkAsync(long[]? wantedItemIds, [BindRequired] string bulk)
        {
            if (bulk == "Search")
            {
                await startSearch.ExecuteAsync<UpdateResult>(new StartWantedSearchParametersV1(), new StartWantedSearchDataV1(wantedItemIds ?? []), HttpContext.RequestAborted);
                TempData["WantedBulk"] = "wanted.search.started";
            }
            else if (Enum.TryParse<WantedPriorityType>(bulk, out var priority) && Enum.IsDefined(priority) && wantedItemIds is { Length: > 0 })
            {
                await updatePriority.ExecuteAsync<UpdateResult>(new UpdateWantedPriorityParametersV1(), new UpdateWantedPriorityDataV1(wantedItemIds, priority), HttpContext.RequestAborted);
                TempData["WantedBulk"] = "wanted.priority.done";
            }

            return LocalRedirect(Address(PageNumber));
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _wanted = await read.ExecuteAsync<PageResult<WantedDetailsV1>>(new ReadWantedParametersV1(PageNumber, wantedViewTypeId: View), ct);
            _summary = await readSummary.ExecuteAsync<WantedSummaryV1>(new ReadWantedParametersV1(), ct);
        }

        private static string ViewAddress(WantedViewType view) => "/Admin/Wanted?PageNumber=1" + (view == WantedViewType.All ? "" : "&View=" + view);

        private string Address(int pageNumber, string? handler = null) =>
            "/Admin/Wanted?PageNumber=" + pageNumber.ToString(CultureInfo.InvariantCulture) + (View == WantedViewType.All ? "" : "&View=" + View) + (handler is null ? "" : "&handler=" + handler);
    }
}
