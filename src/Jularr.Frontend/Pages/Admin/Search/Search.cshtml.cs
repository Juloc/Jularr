using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Search
{
    // Prowlarr's search across all indexers: the bar on top (query with its type and tokens, indexers, categories), then the releases of every indexer as one
    // table (cards on a phone) that is sorted, filtered and chosen from in the browser. The page answers at once; the releases follow into a skeleton of their place.
    // Without script the form asks for the releases in the page itself (inline=true).
    public sealed partial class SearchModel(ReadIndexerSearchSourcesV1 readSources, SearchIndexersV1 search, ReadIndexerReleaseFileV1 readFile, DownloadIndexerReleaseV1 download,
        RecognizeIndexerReleaseV1 recognize, ReadAcquisitionProfilesV1 readProfiles, GrabIndexerReleaseV1 grab, FindManualImportWorksV1 findWorks, IAntiforgery antiforgery, UiText texts, FrontendAssets assets) : JularrPage
    {
        private const int PageSize = 100;
        private IReadOnlyList<IndexerSearchSourceV1> _sources = [];
        private IndexerSearchResultV1? _result;

        [BindProperty(SupportsGet = true, Name = "q")]
        public string? Query { get; set; }

        [BindProperty(SupportsGet = true, Name = "type")]
        public IndexerSearchType Type { get; set; } = IndexerSearchType.Search;

        [BindProperty(SupportsGet = true, Name = "indexer")]
        public long[] Indexers { get; set; } = [];

        [BindProperty(SupportsGet = true, Name = "cat")]
        public int[] Categories { get; set; } = [];

        [BindProperty(SupportsGet = true, Name = "offset")]
        public int Offset { get; set; }

        [BindProperty(SupportsGet = true, Name = "inline")]
        public bool Inline { get; set; }

        // A search was asked for: the form was sent (an empty query lists the newest releases, as in Prowlarr).
        private bool Searched => Request.Query.ContainsKey("q");

        protected override void Define(Page page)
        {
            page.Title("admin.search.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Search/Search.entry.ts");
            page.Asset("Pages/Admin/Search/Search.entry.scss");
            page.Asset("Framework/Components/SelectLinkMenu.entry.ts");
            page.NoPageRow();
            if (_sources.Count == 0)
            {
                page.Content.Add(new Div(new IconTile(new Icon("search", assets, 22), UiTone.Neutral), new P(new Text("search.noIndexers")),
                    new A(new Text("search.noIndexers.link")).Class("button button--primary").Attribute("href", "/Admin/Acquisition/Sources")).Class("search-empty").Attribute("role", "status"));
                return;
            }

            page.Content.Add(Bar());
            if (!Searched) return;
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            page.Content.Add(Toolbar());
            page.Content.Add(_result is { } result ? Results(result, csrf) : Skeleton());
            page.Content.Add(Footer());
            if (_pickDialog is not null) page.Content.Add(_pickDialog);
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            if (_sources.Count > 0) _profiles = await readProfiles.ExecuteAsync<BaseDataList<AcquisitionProfileDetailsV1>>(new ReadAcquisitionProfilesParametersV1(), HttpContext.RequestAborted);
            if (Searched && Inline && _sources.Count > 0) _result = await SearchAsync();
            if (Pick is { Length: > 0 } && _sources.Count > 0) _pickDialog = await PickDialogAsync(antiforgery.GetAndStoreTokens(HttpContext).RequestToken, false);
            PreparePage();
            return Page();
        }

        // The releases of one page of every indexer, as the part that replaces the skeleton; "Mehr laden" asks the next page (rows only).
        public async Task<IActionResult> OnGetResultsAsync(bool rows)
        {
            await LoadDataAsync();
            var result = await SearchAsync();
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            return Fragment(rows ? new Div(Rows(result, csrf), Cards(result, csrf), Chips(result)).Attribute("data-search-more", "") : Results(result, csrf));
        }

        // The .nzb or .torrent of a release, fetched by Jularr; the indexer's link and key stay on the server.
        public async Task<IActionResult> OnPostFileAsync(string releaseId)
        {
            var file = await readFile.ExecuteAsync<IndexerReleaseFileV1>(new IndexerReleaseParametersV1(releaseId ?? ""), new NoData(), HttpContext.RequestAborted);
            return File(file.Bytes, file.ContentType, file.FileName);
        }

        // Prowlarr's download: the release to the downloader without a work. The script asks for JSON and marks the row; without script the hint follows the redirect.
        public async Task<IActionResult> OnPostDownloadAsync(string releaseId, string? returnUrl)
        {
            var script = Request.Headers.Accept.ToString().Contains("application/json", StringComparison.Ordinal);
            try
            {
                await download.ExecuteAsync<UpdateResult>(new IndexerReleaseParametersV1(releaseId ?? ""), new NoData(), HttpContext.RequestAborted);
            }
            catch (Exception exception) when (script && exception is not OperationCanceledException)
            {
                var error = ServiceError.Describe(exception, HttpContext, HttpContext.RequestServices.GetRequiredService<ILogger<JularrPage>>());
                return new JsonResult(new { ok = false, message = texts[error.FieldResourceKey ?? error.ResourceKey] }) { StatusCode = error.StatusCode };
            }

            if (script) return new JsonResult(new { ok = true, message = texts["search.download.sent"] });
            Hint("search.download.sent");
            return LocalRedirect(returnUrl is { Length: > 0 } local && local.StartsWith("/Admin/Search", StringComparison.Ordinal) ? local : "/Admin/Search");
        }

        protected override async Task LoadDataAsync() =>
            _sources = await readSources.ExecuteAsync<BaseDataList<IndexerSearchSourceV1>>(new IndexerSearchParametersV1(), HttpContext.RequestAborted);

        private Task<IndexerSearchResultV1> SearchAsync() => search.ExecuteAsync<IndexerSearchResultV1>(new IndexerSearchParametersV1(),
            new IndexerSearchDataV1(Query, Type, Indexers, Categories, PageSize, Math.Clamp(Offset, 0, 10_000)), HttpContext.RequestAborted);

        // The address of the same search, for the part that loads afterwards, the next page and the way back after a post.
        private string Address(string? handler, int offset = 0)
        {
            var parts = new List<string>();
            if (handler is not null) parts.Add("handler=" + handler);
            parts.Add("q=" + Uri.EscapeDataString(Query ?? ""));
            parts.Add("type=" + Type);
            parts.AddRange(Indexers.Select(id => "indexer=" + Number(id)));
            parts.AddRange(Categories.Select(category => "cat=" + Number(category)));
            if (offset > 0) parts.Add("offset=" + Number(offset));
            return "/Admin/Search?" + string.Join('&', parts);
        }

        // A fragment is the HTML of its component alone, never cached.
        private ContentResult Fragment(IUiComponent component)
        {
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            component.WriteTo(writer, HtmlEncoder.Default, texts);
            Response.Headers.CacheControl = "no-store";
            return Content(writer.ToString(), "text/html");
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
