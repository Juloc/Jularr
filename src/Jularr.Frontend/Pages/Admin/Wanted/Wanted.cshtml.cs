using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Wanted
{
    public sealed class WantedModel(ReadWantedV1 read, ReadWantedItemV1 readItem, UpdateWantedV1 update, SearchWantedV1 search, GrabWantedReleaseV1 grab,
        ReadWantedReleasesV1 releases, MarkWantedReleaseV1 mark, ReadAcquisitionProfilesV1 profiles, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string SearchTab = "search";
        private const string CurrentTab = "current";
        private const string HistoryTab = "history";
        private PageResult<WantedDetailsV1>? _wanted;
        private WantedDetailsV1? _selected;
        private BaseDataList<WantedReleaseCandidateV1>? _candidates;
        private SearchWantedDataV1? _search;
        private BaseDataList<WantedReleaseV1>? _history;
        private IReadOnlyList<AcquisitionProfileDetailsV1> _profiles = [];

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public long? WantedItemId { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? Tab { get; set; }

        protected override void Define(Page page)
        {
            page.Title("admin.wanted.title");
            page.Navigation("nav.admin");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            // Shared Work targets, not requests; one target opens its acquisition tabs.
            page.Content.Add(new Split(split =>
            {
                if (_wanted is { } wanted)
                {
                    split.Primary.Add(new Table<WantedDetailsV1>(wanted.Items)
                        .Content("table.work", item => new ValueText(item.PlatformName is { } platform ? item.CanonicalTitle + " · " + platform : item.CanonicalTitle))
                        .Content("request.language", item => new ValueText(string.Join(", ", item.RequestedLanguages)))
                        .Content("table.status", Status)
                        .Content("wanted.lastSearch", item => new ValueText(item.LastSearchedAt?.ToString("u", CultureInfo.InvariantCulture) ?? ""))
                        .Link(item => item.Id, "common.actions", id => Address(PageNumber, id, SearchTab), "wanted.open")
                        .Content("wanted.monitoring", item => new Form<UpdateWantedDataV1>(form =>
                        {
                            form.Hidden(data => data.IsPaused);
                            form.Hidden(data => data.Version);
                            form.Hidden(data => data.AcquisitionProfileId);
                            form.Submit(item.IsPaused ? "wanted.resume" : "wanted.pause");
                        }, Address(PageNumber, item.Id, SelectedTab), csrf, new UpdateWantedDataV1(!item.IsPaused, item.Version, item.AcquisitionProfileId))));

                    if (wanted.Items.Count == 0) split.Primary.Add(new P(new Text("wanted.empty")));
                    if (wanted.Page > 1) split.Primary.Add(new A(new Text("common.previous")).Attribute("href", Address(wanted.Page - 1)));
                    if (wanted.Items.Count == wanted.PageSize) split.Primary.Add(new A(new Text("common.next")).Attribute("href", Address(wanted.Page + 1)));
                }

                if (_selected is { } selected)
                {
                    split.Secondary.Add(new P(new ValueText(selected.CanonicalTitle)));
                    split.Secondary.Add(new Tabs(tabs =>
                    {
                        // Searching needs Arr+; Requests alone stay manual.
                        if (CanAcquire)
                        {
                            tabs.Add("wanted.tab.search", Address(PageNumber, selected.Id, SearchTab), SelectedTab == SearchTab, content => DefineSearch(content, selected, csrf));
                        }

                        tabs.Add("wanted.tab.current", Address(PageNumber, selected.Id, CurrentTab), SelectedTab == CurrentTab, content => DefineCurrent(content, selected, csrf));
                        tabs.Add("wanted.tab.history", Address(PageNumber, selected.Id, HistoryTab), SelectedTab == HistoryTab, content => DefineHistory(content, selected, csrf));
                    }));
                }
            }).MasterDetail(_selected is not null, Address(PageNumber)));

            if (Error is not null) page.Content.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] bool isPaused, [BindRequired] long version, long? acquisitionProfileId)
        {
            await update.ExecuteAsync<UpdateResult>(new UpdateWantedParametersV1(SelectedId()), new UpdateWantedDataV1(isPaused, version, acquisitionProfileId), HttpContext.RequestAborted);
            return LocalRedirect(Address(PageNumber, WantedItemId, SelectedTab));
        }

        public async Task<IActionResult> OnPostMarkAsync([BindRequired] long acquisitionReleaseId, [BindRequired] AcquisitionReleaseStatusType acquisitionReleaseStatusTypeId)
        {
            await mark.ExecuteAsync<UpdateResult>(new WantedReleaseParametersV1(SelectedId(), acquisitionReleaseId), new MarkWantedReleaseDataV1(acquisitionReleaseStatusTypeId), HttpContext.RequestAborted);
            return LocalRedirect(Address(PageNumber, WantedItemId, HistoryTab));
        }

        public async Task<IActionResult> OnPostSearchAsync(int? parentUnitNumber, int? unitNumber)
        {
            // Results belong to this response; grabs pick from the server-side search.
            _search = new SearchWantedDataV1(parentUnitNumber, unitNumber);
            await LoadDataAsync();
            _candidates = await search.ExecuteAsync<BaseDataList<WantedReleaseCandidateV1>>(new WantedItemParametersV1(SelectedId()), _search, HttpContext.RequestAborted);
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostGrabAsync(string? candidateId)
        {
            await grab.ExecuteAsync<UpdateResult>(new WantedItemParametersV1(SelectedId()), new GrabWantedReleaseDataV1(candidateId ?? ""), HttpContext.RequestAborted);
            TempData["WantedGrabbed"] = true;
            return LocalRedirect(Address(PageNumber, WantedItemId, HistoryTab));
        }

        protected override async Task LoadDataAsync()
        {
            _wanted = await read.ExecuteAsync<PageResult<WantedDetailsV1>>(new ReadWantedParametersV1(PageNumber), HttpContext.RequestAborted);
            if (WantedItemId is { } id)
            {
                _selected = _wanted.Items.FirstOrDefault(item => item.Id == id)
                    ?? await readItem.ExecuteAsync<WantedDetailsV1>(new WantedItemParametersV1(id), HttpContext.RequestAborted);
                if (SelectedTab == HistoryTab)
                {
                    _history = await releases.ExecuteAsync<BaseDataList<WantedReleaseV1>>(new WantedItemParametersV1(id), HttpContext.RequestAborted);
                }
                else if (SelectedTab == CurrentTab && CanAcquire)
                {
                    _profiles = await profiles.ExecuteAsync<BaseDataList<AcquisitionProfileDetailsV1>>(new ReadAcquisitionProfilesParametersV1(), HttpContext.RequestAborted);
                }
            }
        }

        private void DefineSearch(List<IUiComponent> content, WantedDetailsV1 selected, string? csrf)
        {
            // Series searches may narrow to a season or episode, Manga searches to a volume.
            content.Add(new Form<SearchWantedDataV1>(form =>
            {
                if (selected.MediaTypeId == MediaType.Series)
                {
                    form.Number(data => data.ParentUnitNumber, "wanted.search.season");
                    form.Number(data => data.UnitNumber, "wanted.search.episode");
                }
                else if (selected.MediaTypeId == MediaType.Manga)
                {
                    form.Number(data => data.UnitNumber, "wanted.search.volume");
                }

                form.Submit("wanted.search.start");
            }, Address(PageNumber, selected.Id, SearchTab, "Search"), csrf, _search ?? new SearchWantedDataV1()));
            if (_candidates is not { } candidates)
            {
                return;
            }

            // Every candidate stays visible with its decision; rejected ones cannot be taken.
            if (candidates.Count == 0)
            {
                content.Add(new P(new Text("wanted.search.empty")).Attribute("role", "status"));
                return;
            }

            content.Add(new Table<WantedReleaseCandidateV1>(candidates)
                .Resource(item => item.ReleaseDecisionTypeId, "wanted.search.decision", decision => "acquisition.decision." + decision)
                .Text(item => item.Score, "wanted.search.score")
                .Text(item => item.Title, "wanted.search.release")
                .Text(item => item.ConnectionName, "wanted.search.indexer")
                .Content("wanted.search.age", item => new ValueText(Age(item.PublishedAt)))
                .Content("wanted.search.size", item => new ValueText(Size(item.SizeBytes)))
                .Text(item => item.QualityKey, "wanted.search.quality")
                .Content("wanted.search.reason", item => Reason("acquisition.reason." + item.ReasonCode, item.ReasonValue))
                .Content("common.actions", item => item.ReleaseDecisionTypeId == ReleaseDecisionType.Rejected
                    ? new Span()
                    : new Form<GrabWantedReleaseDataV1>(form =>
                    {
                        form.Hidden(data => data.CandidateId);
                        form.Submit("wanted.search.grab");
                    }, Address(PageNumber, selected.Id, SearchTab, "Grab"), csrf, new GrabWantedReleaseDataV1(item.CandidateId))));
        }

        private void DefineCurrent(List<IUiComponent> content, WantedDetailsV1 selected, string? csrf)
        {
            content.Add(new P(new Text("table.status"), new ValueText(": "), Status(selected)));
            content.Add(new P(new Text("request.language"), new ValueText(": " + string.Join(", ", selected.RequestedLanguages))));
            if (selected.LastSearchedAt is { } searchedAt)
            {
                content.Add(new P(new Text("wanted.lastSearch"), new ValueText(": " + searchedAt.ToString("u", CultureInfo.InvariantCulture))));
            }

            // Without a choice the media type default applies.
            var options = _profiles.Where(profile => profile.MediaTypeId == selected.MediaTypeId).ToArray();
            if (options.Length > 0)
            {
                content.Add(new Form<UpdateWantedDataV1>(form =>
                {
                    form.Hidden(data => data.IsPaused);
                    form.Hidden(data => data.Version);
                    form.Select(data => data.AcquisitionProfileId, "wanted.profile", [new SelectOption("", texts["wanted.profile.default"]),
                        .. options.Select(profile => new SelectOption(profile.Id.ToString(CultureInfo.InvariantCulture), profile.Name))]);
                    form.Submit("common.save");
                }, Address(PageNumber, selected.Id, CurrentTab), csrf, new UpdateWantedDataV1(selected.IsPaused, selected.Version, selected.AcquisitionProfileId)));
            }
        }

        private void DefineHistory(List<IUiComponent> content, WantedDetailsV1 selected, string? csrf)
        {
            if (TempData["WantedGrabbed"] is true) content.Add(new P(new Text("wanted.grabQueued")).Attribute("role", "status"));
            if (_history is not { } history)
            {
                return;
            }

            if (history.Count == 0)
            {
                content.Add(new P(new Text("wanted.history.empty")));
                return;
            }

            content.Add(new Table<WantedReleaseV1>(history)
                .Text(item => item.Title, "wanted.search.release")
                .Text(item => item.ConnectionName, "wanted.search.indexer")
                .Text(item => item.QualityKey, "wanted.search.quality")
                .Resource(item => item.AcquisitionReleaseStatusTypeId, "table.status", status => "acquisition.release." + status)
                .Content("wanted.search.reason", item => item.ReasonKey is { } reason ? new Text("acquisition.outcome." + reason) : new Span())
                .Content("wanted.progress", item => new ValueText(item.ProgressPercent?.ToString(CultureInfo.InvariantCulture) ?? ""))
                .Content("wanted.history.date", item => new ValueText(item.CreatedAt.ToString("u", CultureInfo.InvariantCulture)))
                .Content("common.actions", item => item.AcquisitionReleaseStatusTypeId == AcquisitionReleaseStatusType.Grabbed
                    ? new Span()
                    : new Form<MarkWantedReleaseDataV1>(form =>
                    {
                        form.Hidden(data => data.AcquisitionReleaseStatusTypeId);
                        form.Submit(item.IsBlocked ? "wanted.release.unblock" : "wanted.release.block");
                    }, Address(PageNumber, selected.Id, HistoryTab, "Mark") + "&acquisitionReleaseId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf,
                        new MarkWantedReleaseDataV1(item.IsBlocked ? AcquisitionReleaseStatusType.Allowed : AcquisitionReleaseStatusType.Blocked))));
        }

        private static IUiComponent Status(WantedDetailsV1 item)
        {
            // Paused and available targets need no further acquisition.
            if (item.IsPaused) return new Text("wanted.paused");
            if (item.HasMedia) return new Text("wanted.available");
            if (item.IsDownloading)
            {
                return item.ProgressPercent is { } percent
                    ? new Span(new Text("wanted.downloading"), new ValueText(" " + percent.ToString(CultureInfo.InvariantCulture) + "%"))
                    : new Text("wanted.downloading");
            }

            return new Text(item.OutcomeKey is { } outcome ? "acquisition.outcome." + outcome : "wanted.requested");
        }

        private static IUiComponent Reason(string key, string? value) => value is null ? new Text(key) : new Span(new Text(key), new ValueText(" · " + value));

        private static string Age(DateTime? publishedAt) =>
            publishedAt is { } date ? Math.Max(0, (int)(DateTime.UtcNow - date).TotalDays).ToString(CultureInfo.InvariantCulture) : "";

        private static string Size(long? bytes) =>
            bytes is { } size ? (size / 1024d / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture) : "";

        private bool CanAcquire => texts.Capabilities?.CanAcquire is true;

        private string SelectedTab => Tab is HistoryTab ? Tab : Tab is CurrentTab || !CanAcquire ? CurrentTab : SearchTab;

        private long SelectedId() => WantedItemId ?? throw new KeyNotFoundException("No wanted item was selected.");

        private static string Address(int pageNumber, long? wantedItemId = null, string? tab = null, string? handler = null)
        {
            var address = "/Admin/Wanted?PageNumber=" + pageNumber.ToString(CultureInfo.InvariantCulture);
            if (wantedItemId is { } id) address += "&WantedItemId=" + id.ToString(CultureInfo.InvariantCulture) + "&Tab=" + tab;
            return handler is null ? address : address + "&handler=" + handler;
        }
    }
}
