using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Admin.Wanted;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.WantedSearch
{
    // The manual search of one target, or of its season, episode or volume, as a dialog over the page that opened it and as this page. Every source is asked on its own request
    // (the script of the panel starts them together); the sources' releases appear in the places of their skeletons, the rejected ones with their reason.
    public sealed partial class WantedSearchModel(ReadWantedItemV1 readItem, ReadWantedSearchSourcesV1 readSources, SearchWantedV1 search, GrabWantedReleaseV1 grab, IAntiforgery antiforgery,
        UiText texts, FrontendAssets assets) : JularrPage
    {
        private WantedDetailsV1? _item;
        private SearchWantedDataV1? _search;
        private IReadOnlyList<WantedReleaseCandidateV1>? _candidates;
        private IReadOnlyList<WantedSearchSourceV1> _sources = [];

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        protected override void Define(Page page)
        {
            if (_item is not { } item) return;
            page.Title("wanted.tab.search");
            page.Parent("admin.wanted.title", "/Admin/Wanted");
            page.ParentName(item.CanonicalTitle, WantedStatus.Home(Id));
            page.Navigation("nav.admin");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            page.Content.Add(new Div(new A(new Text("common.back")).Class("button").Attribute("href", WantedStatus.Home(Id))).Class("page-actions"));
            if (FormError is not null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
            page.Content.Add(Panel(item, csrf, null, true));
        }

        // The panel the script drives; the form without script searches every source in one request and shows the releases in the same table.
        public async Task<IActionResult> OnGetAsync(int? parentUnitNumber, int? unitNumber)
        {
            _search = new SearchWantedDataV1(parentUnitNumber, unitNumber);
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostSearchAsync(int? parentUnitNumber, int? unitNumber)
        {
            _search = new SearchWantedDataV1(parentUnitNumber, unitNumber);
            await LoadDataAsync();
            _candidates = await search.ExecuteAsync<BaseDataList<WantedReleaseCandidateV1>>(new WantedItemParametersV1(Id), _search, HttpContext.RequestAborted);
            PreparePage();
            return Page();
        }

        // The dialog: its own head with the form that searches again, and the panel; it starts searching when the script inserts it.
        public async Task<IActionResult> OnGetDialogAsync(int? parentUnitNumber, int? unitNumber, string? returnUrl)
        {
            _search = new SearchWantedDataV1(parentUnitNumber, unitNumber);
            await LoadDataAsync();
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            return Fragment(Dialog(_item!, csrf, LocalAddress(returnUrl)));
        }

        // One source: its rows, or the reason it gave none, as the body that replaces the source's skeleton.
        public async Task<IActionResult> OnPostSourceAsync(long connectionId, int? parentUnitNumber, int? unitNumber, string? returnUrl)
        {
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            try
            {
                var candidates = await search.ExecuteAsync<BaseDataList<WantedReleaseCandidateV1>>(new WantedItemParametersV1(Id),
                    new SearchWantedDataV1(parentUnitNumber, unitNumber, null, connectionId), HttpContext.RequestAborted);
                return Fragment(SourceBody(connectionId, candidates, csrf, LocalAddress(returnUrl)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var error = ServiceError.Describe(exception, HttpContext, HttpContext.RequestServices.GetRequiredService<ILogger<JularrPage>>());

                // A missing right or session is the page's answer, not a source's.
                if (error.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden or StatusCodes.Status404NotFound) throw;
                return Fragment(SourceFailure(connectionId, error));
            }
        }

        public async Task<IActionResult> OnPostGrabAsync(string? candidateId, bool startsNow, bool overridesRejection, string? returnUrl)
        {
            var result = await grab.ExecuteAsync<GrabWantedReleaseResultV1>(new WantedItemParametersV1(Id), new GrabWantedReleaseDataV1(candidateId ?? "", startsNow, overridesRejection), HttpContext.RequestAborted);
            Hint(!startsNow ? "wanted.grabQueued" : result.IsPrioritized ? "wanted.grabStarted" : "wanted.grabNotPrioritized");
            return LocalRedirect(LocalAddress(returnUrl) ?? WantedStatus.Home(Id));
        }

        protected override async Task LoadDataAsync()
        {
            _item = await readItem.ExecuteAsync<WantedDetailsV1>(new WantedItemParametersV1(Id), HttpContext.RequestAborted);
            _sources = await readSources.ExecuteAsync<BaseDataList<WantedSearchSourceV1>>(new WantedItemParametersV1(Id), new SearchWantedDataV1(), HttpContext.RequestAborted);
        }

        // A fragment is the HTML of its component alone, never cached.
        private ContentResult Fragment(IUiComponent component)
        {
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            component.WriteTo(writer, HtmlEncoder.Default, texts);
            Response.Headers.CacheControl = "no-store";
            return Content(writer.ToString(), "text/html");
        }

        // Only an address of this site; anything else leaves the grab on the target's page.
        private static string? LocalAddress(string? address) => address is { Length: > 0 } && address[0] == '/' && !address.StartsWith("//", StringComparison.Ordinal) && !address.Contains('\\') ? address : null;
    }
}
