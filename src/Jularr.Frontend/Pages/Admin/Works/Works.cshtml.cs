using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Works
{
    public sealed class WorksModel(ReadWorksV1 read, CreateWorkV1 create, SearchMetadataV1 search, ImportMetadataWorkV1 import, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private PageResult<WorkDetailsV1>? _works;
        private CreateWorkDataV1? _input;
        private SearchMetadataDataV1? _search;
        private BaseDataList<MetadataSearchResultV1>? _results;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("admin.works.title");
            page.Navigation("nav.admin");
            page.Content.Add(AdminNavigation.Works);

            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var mediaTypes = Enum.GetValues<MediaType>().Select(type => new SelectOption(((byte)type).ToString(CultureInfo.InvariantCulture), texts["media.type." + type]));
            SelectOption[] subtypes = [new SelectOption("", texts["media.subtype.none"]), .. Enum.GetValues<MediaSubtype>()
                .Select(subtype => new SelectOption(((byte)subtype).ToString(CultureInfo.InvariantCulture), texts["media.subtype." + subtype]))];

            // TMDB lookup creates Works with their provider identity and episodes.
            var metadataTypes = new[] { MediaType.Movie, MediaType.Series, MediaType.Book }.Select(type => new SelectOption(((byte)type).ToString(CultureInfo.InvariantCulture), texts["media.type." + type]));
            page.Content.Add(new Form<SearchMetadataDataV1>(form =>
            {
                form.Select(item => item.MediaTypeId, "media.type", metadataTypes);
                form.Select(item => item.MediaSubtypeId, "media.subtype", subtypes);
                form.Text(item => item.Query, "media.title");
                form.Number(item => item.ReleaseYear, "media.year");
                form.Submit("admin.works.searchMetadata");
            }, "/Admin/Works?handler=Search", csrf, _search, _search is null ? null : FormError));
            if (_results is { } results && _search is { } searched)
            {
                if (results.Count == 0) page.Content.Add(new P(new Text("admin.works.noMetadata")).Attribute("role", "status"));
                else page.Content.Add(new Table<MetadataSearchResultV1>(results)
                    .Text(item => item.Title, "media.title")
                    .Text(item => item.OriginalTitle, "admin.works.originalTitle")
                    .Text(item => item.Author, "media.author")
                    .Text(item => item.ReleaseYear, "media.year")
                    .Content("common.actions", item => item.WorkId is { } workId
                        ? new A(new Text("common.open")).Attribute("href", "/Admin/Works/" + workId.ToString(CultureInfo.InvariantCulture))
                        : new Form<ImportMetadataWorkDataV1>(form =>
                        {
                            form.Hidden(data => data.MediaTypeId);
                            form.Hidden(data => data.MediaSubtypeId);
                            form.Hidden(data => data.ExternalId);
                            form.Submit("admin.works.import");
                        }, "/Admin/Works?handler=Import", csrf, new ImportMetadataWorkDataV1(searched.MediaTypeId, item.ExternalId, searched.MediaSubtypeId))));
            }

            // Manual canonical creation.
            page.Content.Add(new Form<CreateWorkDataV1>(form =>
            {
                form.Select(item => item.MediaTypeId, "media.type", mediaTypes);
                form.Select(item => item.MediaSubtypeId, "media.subtype", subtypes);
                form.Text(item => item.CanonicalTitle, "media.title");
                form.Number(item => item.ReleaseYear, "media.year");
                form.Submit("media.create");
            }, "/Admin/Works", csrf, _input, _search is null ? FormError : null));

            // Real catalog rows.
            if (_works is { } works)
            {
                page.Content.Add(new Table<WorkDetailsV1>(works.Items)
                    .Text(item => item.CanonicalTitle, "media.title")
                    .Content("media.type", item => new Text(MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId)))
                    .Link(item => item.Id, "common.open", id => "/Admin/Works/" + id.ToString(CultureInfo.InvariantCulture), "common.open"));

                if (works.Items.Count == 0) page.Content.Add(new P(new Text("media.empty")));
                if (works.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", "/Admin/Works?pageNumber=" + (works.Page - 1)));
                if (works.Items.Count == works.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", "/Admin/Works?pageNumber=" + (works.Page + 1)));
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string? canonicalTitle, int? releaseYear)
        {
            _input = new CreateWorkDataV1(mediaTypeId, canonicalTitle ?? "", releaseYear, mediaSubtypeId);
            var work = await create.ExecuteAsync<WorkDetailsV1>(_input, HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Work/Work", new { id = work.Id });
        }

        public async Task<IActionResult> OnPostSearchAsync([BindRequired] MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string? query, int? releaseYear)
        {
            // Results belong to this response.
            _search = new SearchMetadataDataV1(mediaTypeId, query ?? "", releaseYear, mediaSubtypeId);
            await LoadDataAsync();
            _results = await search.ExecuteAsync<BaseDataList<MetadataSearchResultV1>>(_search, HttpContext.RequestAborted);
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostImportAsync([BindRequired] MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string? externalId)
        {
            var work = await import.ExecuteAsync<WorkDetailsV1>(new ImportMetadataWorkDataV1(mediaTypeId, externalId ?? "", mediaSubtypeId), HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Work/Work", new { id = work.Id });
        }

        protected override async Task LoadDataAsync()
        {
            _works = await read.ExecuteAsync<PageResult<WorkDetailsV1>>(new ReadWorksParametersV1(PageNumber), HttpContext.RequestAborted);
        }
    }
}
