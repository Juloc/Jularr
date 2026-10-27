using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Library.V1;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Works
{
    // The library as a list: filters, columns kept in this browser, in-row monitoring and search, and the mass editor.
    public sealed partial class WorksModel(ReadWorksV1 read, ReadWorkExclusionsV1 readExclusions, DeleteWorkExclusionV1 deleteExclusion, CreateWorkV1 create, SearchMetadataV1 search, ImportMetadataWorkV1 import,
        ReadLibraryOverviewV1 readLibraries, ReadAcquisitionProfilesV1 readProfiles, UpdateWorksMonitoringV1 updateMonitoring, UpdateWorksProfileV1 updateProfile, UpdateWorksLibraryV1 updateLibrary,
        QueueWorksSearchV1 queueSearch, ReadWorksSearchRunV1 readSearch, ReadWorksSearchResultsV1 readSearchResults, CancelWorksSearchV1 cancelSearch,
        RefreshWorksMetadataV1 refreshWorks, DeleteWorksV1 deleteWorks, ReadWorksSummaryV1 readSummary, FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        // What one request may change: the mass editor's batch; searches and file removals take fewer.
        private const int PageSize = 50;
        private const int ListedFailures = 12;
        private const string ViewCookie = "jularr-works-view";

        private PageResult<WorkListItemV1>? _works;
        private WorksSummaryV1? _summary;
        private PageResult<WorkExclusionV1>? _exclusions;
        private IReadOnlyList<LibraryOverviewV1> _libraries = [];
        private IReadOnlyList<AcquisitionProfileDetailsV1> _profiles = [];
        private CreateWorkDataV1? _input;
        private SearchMetadataDataV1? _search;
        private BaseDataList<MetadataSearchResultV1>? _results;
        private bool _bulkFailed;
        private WorksViewType _view;
        private SearchRunDetailsV1? _searchRun;
        private PageResult<WorkBulkOutcomeV1>? _searchResults;

        [BindProperty(SupportsGet = true, Name = "searchJob")]
        public long? SearchJob { get; set; }

        [BindProperty(SupportsGet = true, Name = "searchPage")]
        public int SearchPage { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        // The excluded titles page on their own.
        [BindProperty(SupportsGet = true, Name = "exclusions")]
        public int ExclusionPage { get; set; } = 1;

        // Filters and order live in the address, so a list can be shared, reloaded and paged.
        [BindProperty(SupportsGet = true, Name = "type")]
        public MediaType? TypeFilter { get; set; }

        [BindProperty(SupportsGet = true, Name = "monitored")]
        public bool? MonitoredFilter { get; set; }

        [BindProperty(SupportsGet = true, Name = "status")]
        public WorkListStatus? StatusFilter { get; set; }

        [BindProperty(SupportsGet = true, Name = "library")]
        public long? LibraryFilter { get; set; }

        [BindProperty(SupportsGet = true, Name = "profile")]
        public long? ProfileFilter { get; set; }

        // The header search leads here with a part of the title.
        [BindProperty(SupportsGet = true, Name = "q")]
        public string? QueryFilter { get; set; }

        // The view is chosen by its address and then kept in a cookie of this browser, so the next visit starts in it.
        [BindProperty(SupportsGet = true, Name = "view")]
        public WorksViewType? ViewChoice { get; set; }

        [BindProperty(SupportsGet = true, Name = "sort")]
        public WorkSortType Sort { get; set; }

        [BindProperty(SupportsGet = true, Name = "desc")]
        public bool Descending { get; set; }

        private bool CanManageWanted => texts.CanManageWanted();

        private bool CanAcquire => texts.CanAcquire();

        private bool CanManageMedia => texts.CanManageMedia();

        protected override void Define(Page page)
        {
            page.Title("admin.works.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Work/Work.entry.scss");
            page.Asset("Pages/Admin/Work/Work.entry.ts");
            page.Asset("Pages/Admin/Works/Works.entry.scss");
            page.Asset("Pages/Admin/Works/Works.entry.ts");
            page.Asset("Framework/Components/Drawer.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            page.Asset("Framework/Components/MediaCard.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            if (FormError is not null && _bulkFailed) page.Content.Add(new P(ErrorText.Of(FormError)).Class("ui-page-error").Attribute("role", "alert"));
            if (TempData["WorksReport"] is string report) page.Content.Add(Report(report));

            // One row of filter chips with the title field and the view switch, then the titles in the chosen view; the chosen titles belong to one form that the dock's bulk zone and the change drawer post.
            page.NoPageRow();
            page.Content.Add(Toolbar());
            if (CanAcquire) page.Content.Add(SearchControls(csrf));
            if (_searchRun is not null) page.Content.Add(SearchPanel(csrf));
            if (_works is { } works)
            {
                page.Content.Add(Titles(works.Items, csrf).Paged(works.Items.Count, PageSize, primary: true));
                if (_view == WorksViewType.Table) page.Content.Add(new ColumnPicker());
                if (works.Items.Count > 0 && CanManageMedia) page.Content.Add(DeleteDialog(csrf));
                if (works.Items.Count > 0 && Selectable)
                {
                    page.Content.Add(new FormElement(new Input().Attribute("type", "hidden").Attribute("name", "__RequestVerificationToken").Attribute("value", csrf ?? ""))
                        .Attribute("id", BulkForm).Attribute("method", "post").Attribute("action", Address("Bulk")));
                    if (CanManageWanted) page.Content.Add(ChangeDrawer());
                }
            }

            page.Content.Add(FilterDrawer());

            // Titles deleted with an exclusion stay out of requests until the exclusion is removed.
            if (_exclusions is { } exclusions && (exclusions.Items.Count > 0 || ExclusionPage > 1))
            {
                page.Content.Add(new UiSection("admin.works.exclusions", [new Table<WorkExclusionV1>(exclusions.Items)
                    .Text(item => item.Title, "media.title")
                    .Content("media.type", item => new Text(MediaTypeLabel.Key(item.MediaTypeId, null)))
                    .Content("common.actions", item => new PostForm(Address("RemoveExclusion"), csrf, [("workExclusionId", item.Id.ToString(CultureInfo.InvariantCulture))],
                        new Button(new Text("admin.works.exclusion.remove")).Submit())).Paged(key: "exclusions")]));
            }

            page.Content.Add(AddDialog(csrf));
            page.Content.Add(Dock(_works));
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
            if (_results.Count == 0) Hint("admin.works.noMetadata");
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostImportAsync([BindRequired] MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string? @namespace, string? externalId)
        {
            var work = await import.ExecuteAsync<WorkDetailsV1>(new ImportMetadataWorkDataV1(mediaTypeId, externalId ?? "", mediaSubtypeId, @namespace), HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Work/Work", new { id = work.Id });
        }

        public async Task<IActionResult> OnPostRemoveExclusionAsync([BindRequired] long workExclusionId)
        {
            await deleteExclusion.ExecuteAsync<UpdateResult>(new WorkExclusionParametersV1(workExclusionId), default, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        // The switch of one row: the same rule as on the title's own page.
        public async Task<IActionResult> OnPostMonitorAsync([BindRequired] long workId, [BindRequired] bool isMonitored)
        {
            _bulkFailed = true;
            var outcomes = await updateMonitoring.ExecuteAsync<BaseDataList<WorkBulkOutcomeV1>>(new WorksBulkParametersV1(), new UpdateWorksMonitoringDataV1([workId], isMonitored), HttpContext.RequestAborted);
            return Reported(isMonitored ? "monitor" : "unmonitor", outcomes);
        }

        // The search of one row, as the title's own page searches.
        public async Task<IActionResult> OnPostSearchWorkAsync([BindRequired] long workId)
        {
            _bulkFailed = true;
            return await QueueSearchAsync(new QueueWorksSearchDataV1([workId]));
        }

        // The mass editor: the chosen titles take what was set, or are searched or refreshed.
        public async Task<IActionResult> OnPostBulkAsync(string? action, long[]? workIds, string? monitoring, string? profileId, string? libraryId)
        {
            _bulkFailed = true;
            var ids = workIds ?? [];
            var token = HttpContext.RequestAborted;
            if (ids.Length == 0) return Reported(action ?? "apply", new BaseDataList<WorkBulkOutcomeV1>([]));
            switch (action)
            {
                case "search":
                    return await QueueSearchAsync(new QueueWorksSearchDataV1(ids));
                case "refresh":
                    return Reported("refresh", await refreshWorks.ExecuteAsync<BaseDataList<WorkBulkOutcomeV1>>(new WorksBulkParametersV1(), new HeavyWorksDataV1(ids), token));
            }

            // Each setting that was chosen is applied in turn; the report holds the titles that could not take one.
            var outcomes = new List<WorkBulkOutcomeV1>();
            if (monitoring is "on" or "off")
            {
                outcomes.AddRange(await updateMonitoring.ExecuteAsync<BaseDataList<WorkBulkOutcomeV1>>(new WorksBulkParametersV1(), new UpdateWorksMonitoringDataV1(ids, monitoring == "on"), token));
            }

            if (long.TryParse(profileId, NumberStyles.None, CultureInfo.InvariantCulture, out var profile))
            {
                outcomes.AddRange(await updateProfile.ExecuteAsync<BaseDataList<WorkBulkOutcomeV1>>(new WorksBulkParametersV1(), new UpdateWorksProfileDataV1(ids, profile == 0 ? null : profile), token));
            }

            if (long.TryParse(libraryId, NumberStyles.None, CultureInfo.InvariantCulture, out var library))
            {
                outcomes.AddRange(await updateLibrary.ExecuteAsync<BaseDataList<WorkBulkOutcomeV1>>(new WorksBulkParametersV1(), new UpdateWorksLibraryDataV1(ids, library == 0 ? null : library), token));
            }

            return Reported("apply", new BaseDataList<WorkBulkOutcomeV1>(outcomes), outcomes.Count == 0 ? 0 : ids.Length);
        }

        public async Task<IActionResult> OnPostDeleteAsync(long[]? workIds, bool deletesFiles, bool addsExclusion)
        {
            _bulkFailed = true;
            var ids = workIds ?? [];
            if (ids.Length == 0) return Reported("delete", new BaseDataList<WorkBulkOutcomeV1>([]));
            return Reported("delete", await deleteWorks.ExecuteAsync<BaseDataList<WorkBulkOutcomeV1>>(new WorksBulkParametersV1(), new DeleteWorksDataV1(ids, deletesFiles, addsExclusion), HttpContext.RequestAborted));
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _view = ResolveView();
            _exclusions = await readExclusions.ExecuteAsync<PageResult<WorkExclusionV1>>(new ReadWorkExclusionsParametersV1(Math.Max(1, ExclusionPage)), ct);
            _works = await read.ExecuteAsync<PageResult<WorkListItemV1>>(new ReadWorksParametersV1(PageNumber, PageSize, TypeFilter, MonitoredFilter, StatusFilter, LibraryFilter, ProfileFilter, QueryFilter, Sort, Descending), ct);
            _summary = await readSummary.ExecuteAsync<WorksSummaryV1>(new ReadWorksParametersV1(1, PageSize, TypeFilter, null, null, LibraryFilter, ProfileFilter, QueryFilter), ct);
            _libraries = await readLibraries.ExecuteAsync<BaseDataList<LibraryOverviewV1>>(new ReadLibraryOverviewParametersV1(), ct);

            // Profiles belong to Arr+.
            if (CanAcquire)
            {
                _profiles = await readProfiles.ExecuteAsync<BaseDataList<AcquisitionProfileDetailsV1>>(new ReadAcquisitionProfilesParametersV1(), ct);
                _searchRun = (await readSearch.ExecuteAsync<BaseDataList<SearchRunDetailsV1>>(new ReadSearchRunParametersV1(SearchJob), ct)).FirstOrDefault();
                if (_searchRun is { } run && SearchJob is not null)
                {
                    _searchResults = await readSearchResults.ExecuteAsync<PageResult<WorkBulkOutcomeV1>>(new SearchRunParametersV1(run.OperationId, SearchPage), ct);
                }
            }
        }

        // The address names a view and the cookie remembers it; a first visit shows the table.
        private WorksViewType ResolveView()
        {
            if (ViewChoice is { } chosen && Enum.IsDefined(chosen))
            {
                Response.Cookies.Append(ViewCookie, chosen.ToString(), new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = Request.IsHttps, Path = "/" });
                return chosen;
            }

            return Enum.TryParse<WorksViewType>(Request.Cookies[ViewCookie], out var saved) && Enum.IsDefined(saved) ? saved : WorksViewType.Table;
        }

        // What happened to each chosen title is shown once, after the list is shown again; the counts say how many, the titles that stayed say why.
        private IActionResult Reported(string action, BaseDataList<WorkBulkOutcomeV1> outcomes, int? total = null)
        {
            var failed = outcomes.Where(outcome => outcome.OutcomeKey is not ("done" or "grabbed" or "unchanged")).ToArray();
            var done = outcomes.Count(outcome => outcome.OutcomeKey is "done" or "grabbed");
            TempData["WorksReport"] = string.Join('\n', [action, done.ToString(CultureInfo.InvariantCulture), (total ?? outcomes.Count).ToString(CultureInfo.InvariantCulture),
                .. failed.Take(ListedFailures).Select(outcome => outcome.OutcomeKey + "\t" + (outcome.CanonicalTitle.Length > 60 ? outcome.CanonicalTitle[..60] : outcome.CanonicalTitle))]);
            return LocalRedirect(Address());
        }
    }
}
