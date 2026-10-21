using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Wanted
{
    // Shared Work targets, not requests; each opens on its own page. Rows are chosen for a priority or a search of their own.
    public sealed class WantedModel(ReadWantedV1 read, UpdateWantedV1 update, UpdateWantedPriorityV1 updatePriority, StartWantedSearchV1 startSearch, IAntiforgery antiforgery, UiText texts, FrontendAssets assets) : JularrPage
    {
        private const string BulkForm = "wanted-bulk";
        private PageResult<WantedDetailsV1>? _wanted;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public WantedViewType View { get; set; }

        protected override void Define(Page page)
        {
            page.Title("admin.wanted.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Wanted/Wanted.entry.scss");
            page.Asset("Pages/Admin/Wanted/Wanted.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var manage = texts.CanManageWanted();
            var acquire = texts.CanAcquire();

            page.Content.Add(new NavigationTabs(Enum.GetValues<WantedViewType>()
                .Select(view => new NavigationTab("wanted.view." + view, ViewAddress(view), view == View)).ToArray(), "admin.wanted.title"));
            if (TempData["WantedBulk"] is string done) page.Content.Add(new P(new Text(done)).Attribute("role", "status"));
            if (_wanted is { } wanted)
            {
                if (manage && wanted.Items.Count > 0) page.Content.Add(BulkBar(acquire, csrf));
                var table = new Table<WantedDetailsV1>(wanted.Items);
                if (manage) table = table.Content("wanted.select", Select);
                table = table
                    .Content("table.work", item => new Div(new Truncated(item.PlatformName is { } platform ? item.CanonicalTitle + " · " + platform : item.CanonicalTitle)).Class("wanted-title"))
                    .Content("request.language", item => new ValueText(string.Join(", ", item.RequestedLanguages)))
                    .Content("wanted.priority", Priority)
                    .Content("table.status", item => View == WantedViewType.CutoffUnmet && item.HasMedia && !item.IsPaused ? new Text("wanted.belowCutoff") : WantedStatus.Of(item))
                    .Content("wanted.lastSearch", item => new ValueText(item.LastSearchedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? ""))
                    .Content("common.actions", item => Actions(item, acquire, manage, wanted.Page, csrf));
                page.Content.Add(new Div(table).Class("wanted-list"));
                if (wanted.Items.Count == 0) page.Content.Add(new P(new Text("wanted.empty")));
                if (wanted.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", Address(wanted.Page - 1)));
                if (wanted.Items.Count == wanted.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", Address(wanted.Page + 1)));
            }

            if (FormError is not null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
        }

        // The box joins the bulk form below the chips, so no form sits inside another.
        private IUiComponent Select(WantedDetailsV1 item) => new Input().Attribute("type", "checkbox").Attribute("name", "wantedItemIds")
            .Attribute("value", item.Id.ToString(CultureInfo.InvariantCulture)).Attribute("form", BulkForm).TextAttribute("aria-label", "wanted.select");

        // High targets are searched first, Normal ones after them; nothing waits.
        private static IUiComponent Priority(WantedDetailsV1 item) => item.WantedPriorityTypeId == WantedPriorityType.High
            ? new Tag(new Text("wanted.priority.High")).Accent()
            : new Span(new Text("wanted.priority.Normal")).Class("wanted-priority");

        // The one bar of the page: select all and how many, a priority for the chosen rows, and the search of the chosen rows or of every one that is due.
        private IUiComponent BulkBar(bool acquire, string? csrf)
        {
            var selected = new Span(new ValueText("")).Class("wanted-bulk__count").Attribute("data-wanted-count", "").Attribute("data-template", texts["wanted.selected"]).Attribute("hidden", "hidden");
            var controls = new List<IUiComponent>
            {
                new Label(new Input().Attribute("type", "checkbox").Attribute("data-wanted-all", "").TextAttribute("aria-label", "wanted.selectAll")).Class("wanted-bulk__all"),
                selected,
                new Span(new Text("wanted.priority")).Class("wanted-bulk__label"),
                PriorityButton(WantedPriorityType.High),
                PriorityButton(WantedPriorityType.Normal)
            };
            if (acquire)
            {
                controls.Add(new Button(new Icon("search", assets, 16), new Span(new Text("wanted.search.start")).Attribute("data-wanted-search-label", "")).Compact().Primary().Submit()
                    .Attribute("name", "bulk").Attribute("value", "Search").Attribute("data-label-all", texts["wanted.search.all"]).Attribute("data-label-selected", texts["wanted.search.selected"])
                    .Attribute("data-wanted-search", ""));
            }

            return new Framework.PostForm(Address(PageNumber, "Bulk"), csrf, [], [.. controls]).Class("wanted-bulk").Attribute("id", BulkForm);
        }

        private IUiComponent PriorityButton(WantedPriorityType type) => new Button(new Text("wanted.priority." + type)).Compact().Submit().Attribute("name", "bulk").Attribute("value", type.ToString())
            .Attribute("data-wanted-priority", "");

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
                actions.Add(new Framework.PostForm(Address(pageNumber, "Pause") + "&wantedItemId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf,
                    [("isPaused", (!item.IsPaused).ToString().ToLowerInvariant()), ("version", item.Version.ToString(CultureInfo.InvariantCulture)),
                        ("acquisitionProfileId", item.AcquisitionProfileId?.ToString(CultureInfo.InvariantCulture) ?? ""), ("libraryRootId", item.LibraryRootId?.ToString(CultureInfo.InvariantCulture) ?? "")],
                    new Button(new Icon(item.IsPaused ? "play" : "pause", assets, 18)).IconOnly(label).Submit().Attribute("title", label)));
            }

            return new Div([.. actions]).Class("wanted-actions");
        }

        private IUiComponent IconLink(string icon, string labelKey, string href, string name) =>
            new A(new Icon(icon, assets, 18)).Class("button button--icon").Attribute("href", href).Attribute("aria-label", texts[labelKey] + " " + name).Attribute("title", texts[labelKey] + " " + name);

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

        protected override async Task LoadDataAsync() =>
            _wanted = await read.ExecuteAsync<PageResult<WantedDetailsV1>>(new ReadWantedParametersV1(PageNumber, wantedViewTypeId: View), HttpContext.RequestAborted);

        private static string ViewAddress(WantedViewType view) => "/Admin/Wanted?PageNumber=1" + (view == WantedViewType.All ? "" : "&View=" + view);

        private string Address(int pageNumber, string? handler = null) =>
            "/Admin/Wanted?PageNumber=" + pageNumber.ToString(CultureInfo.InvariantCulture) + (View == WantedViewType.All ? "" : "&View=" + View) + (handler is null ? "" : "&handler=" + handler);

        private sealed class Label(params IUiComponent[] children) : HtmlElement("label", children);
    }
}
