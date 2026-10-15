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
    // Shared Work targets, not requests; each opens on its own page.
    public sealed class WantedModel(ReadWantedV1 read, UpdateWantedV1 update, IAntiforgery antiforgery) : JularrPage
    {
        private PageResult<WantedDetailsV1>? _wanted;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public WantedViewType View { get; set; }

        protected override void Define(Page page)
        {
            page.Title("admin.wanted.title");
            page.Navigation("nav.admin");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            page.Content.Add(new NavigationTabs(Enum.GetValues<WantedViewType>()
                .Select(view => new NavigationTab("wanted.view." + view, ViewAddress(view), view == View)).ToArray(), "admin.wanted.title"));
            if (_wanted is { } wanted)
            {
                page.Content.Add(new Table<WantedDetailsV1>(wanted.Items)
                    .Content("table.work", item => new ValueText(item.PlatformName is { } platform ? item.CanonicalTitle + " · " + platform : item.CanonicalTitle))
                    .Content("request.language", item => new ValueText(string.Join(", ", item.RequestedLanguages)))
                    .Content("table.status", item => View == WantedViewType.CutoffUnmet && item.HasMedia && !item.IsPaused ? new Text("wanted.belowCutoff") : WantedStatus.Of(item))
                    .Content("wanted.lastSearch", item => new ValueText(item.LastSearchedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? ""))
                    .Link(item => item.Id, "common.actions", WantedStatus.Home, "wanted.open")
                    .Content("wanted.monitoring", item => new Form<UpdateWantedDataV1>(form =>
                    {
                        form.Hidden(data => data.IsPaused);
                        form.Hidden(data => data.Version);
                        form.Hidden(data => data.AcquisitionProfileId);
                        form.Hidden(data => data.LibraryRootId);
                        form.Submit(item.IsPaused ? "wanted.resume" : "wanted.pause", item.CanonicalTitle, valueHidden: true);
                    }, Address(wanted.Page) + "&handler=Pause&wantedItemId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf,
                        new UpdateWantedDataV1(!item.IsPaused, item.Version, item.AcquisitionProfileId, item.LibraryRootId))));
                if (wanted.Items.Count == 0) page.Content.Add(new P(new Text("wanted.empty")));
                if (wanted.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", Address(wanted.Page - 1)));
                if (wanted.Items.Count == wanted.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", Address(wanted.Page + 1)));
            }

            if (FormError is not null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
        }

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

        protected override async Task LoadDataAsync() =>
            _wanted = await read.ExecuteAsync<PageResult<WantedDetailsV1>>(new ReadWantedParametersV1(PageNumber, wantedViewTypeId: View), HttpContext.RequestAborted);

        private static string ViewAddress(WantedViewType view) => "/Admin/Wanted?PageNumber=1" + (view == WantedViewType.All ? "" : "&View=" + view);

        private string Address(int pageNumber) =>
            "/Admin/Wanted?PageNumber=" + pageNumber.ToString(CultureInfo.InvariantCulture) + (View == WantedViewType.All ? "" : "&View=" + View);
    }
}
