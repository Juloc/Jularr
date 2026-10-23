using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Backend.Service.Admin.Library.V1;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Admin.Wanted;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Work
{
    public enum WorkTab
    {
        Units = 1,
        History = 2,
        Metadata = 3,
        Files = 4
    }

    // One page per title: films and series are managed here, with their seasons, files, searches and history; other types still show their metadata.
    public sealed partial class WorkModel(ReadWorkV1 read, UpdateWorkV1 update, UpdateWorkFranchiseV1 updateFranchise, RefreshWorkMetadataV1 refresh, ReadWorkUnitGroupsV1 readGroups, CreateWorkUnitGroupV1 createGroup,
        DeleteWorkUnitGroupV1 deleteGroup, SearchMetadataV1 search, RematchWorkV1 rematch, ReadWorkManagementV1 readManagement, ReadWantedItemV1 readWanted, UpdateWantedV1 updateWanted, UpdateWantedUnitV1 updateUnit,
        SearchWantedAutomaticallyV1 automatic, ReadWantedReleasesV1 readReleases, MarkWantedReleaseV1 markRelease, ReadActivityEventsV1 readEvents, ReadAcquisitionProfilesV1 profiles, ReadWantedLibrariesV1 libraries,
        ReadAcquisitionTagsV1 tags, ReadAcquisitionTagV1 tag, DeleteLibraryFilesV1 deleteFiles, DeleteDocumentTranslationV1 deleteTranslation, DeleteWorkV1 deleteWork, UpdateWorkEditionV1 updateEdition, UpdateLibraryFileFactsV1 updateFacts, ReadLibraryRenamesV1 readRenames, ApplyLibraryRenamesV1 applyRenames, FrontendAssets assets,
        IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private SearchMetadataDataV1? _matchQuery;
        private IReadOnlyList<MetadataSearchResultV1>? _matches;
        private WorkDetailsV1? _work;
        private UpdateWorkDataV1? _input;
        private UpdateWorkFranchiseDataV1? _franchiseInput;
        private IReadOnlyList<WorkUnitGroupV1>? _groups;
        private CreateWorkUnitGroupDataV1? _groupInput;
        private WorkManagementV1? _management;
        private WantedDetailsV1? _wanted;
        private PageResult<ActivityEventV1>? _events;
        private IReadOnlyList<WantedReleaseV1> _releases = [];
        private IReadOnlyList<AcquisitionProfileDetailsV1> _profiles = [];
        private IReadOnlyList<WantedLibraryV1> _libraries = [];
        private SelectOption[] _tagChoices = [];
        private UpdateWantedDataV1? _choice;
        private long? _choiceWantedId;
        private readonly Dictionary<long, WantedDetailsV1> _platformTargets = [];
        private LibraryRenamePlanV1? _renames;
        private DeleteWorkDataV1? _deleteInput;

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        [BindProperty(SupportsGet = true)]
        public WorkTab Tab { get; set; } = WorkTab.Units;

        [BindProperty(SupportsGet = true)]
        public int HistoryPage { get; set; } = 1;

        // Films and series have the management page; every other type keeps its metadata page until its own layout is built.
        private bool Managed => _work is { MediaTypeId: MediaType.Movie or MediaType.Series } or { MediaTypeId: MediaType.Book or MediaType.Music or MediaType.Game };

        // Searching needs Arr+; Requests alone stay manual.
        private bool CanAcquire => texts.CanAcquire();

        private string Home => "/Admin/Works/" + Id.ToString(CultureInfo.InvariantCulture);

        protected override void Define(Page page)
        {
            if (Managed && _management is { } management)
            {
                DefineManagement(page, management);
                return;
            }

            DefineMetadata(page, standalone: true);
        }

        // The address of a tab, or of a handler that returns to the metadata tab.
        private string Address(WorkTab tab = WorkTab.Units, string? handler = null, int? historyPage = null)
        {
            var values = new List<string>();
            if (tab != WorkTab.Units) values.Add("Tab=" + tab);
            if (handler is not null) values.Add("handler=" + handler);
            if (historyPage is > 1) values.Add("HistoryPage=" + historyPage.Value.ToString(CultureInfo.InvariantCulture));
            return Home + (values.Count == 0 ? "" : "?" + string.Join('&', values));
        }

        // Metadata forms post back to their own tab.
        private string MetadataAction(string? handler = null) => Managed ? Address(WorkTab.Metadata, handler) : handler is null ? Home : Home + "?handler=" + handler;

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        // Metadata

        public async Task<IActionResult> OnPostMatchSearchAsync([BindRequired] MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string? query)
        {
            _matchQuery = new SearchMetadataDataV1(mediaTypeId, query ?? "", null, mediaSubtypeId);
            await LoadDataAsync();
            _matches = await search.ExecuteAsync<BaseDataList<MetadataSearchResultV1>>(new SearchMetadataParametersV1(), _matchQuery, HttpContext.RequestAborted);
            if (_matches.Count == 0) Hint("discover.noResults");
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostMatchAsync([BindRequired] string @namespace, [BindRequired] string externalId, [BindRequired] string title, int? releaseYear, [BindRequired] long version)
        {
            await rematch.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(Id), new RematchWorkDataV1(@namespace, externalId, title, releaseYear, version), HttpContext.RequestAborted);
            return LocalRedirect(MetadataAction());
        }

        public async Task<IActionResult> OnPostAsync(string? canonicalTitle, long version, int? releaseYear, MediaSubtype? mediaSubtypeId)
        {
            _input = new UpdateWorkDataV1(canonicalTitle ?? "", version, releaseYear, mediaSubtypeId);
            await update.ExecuteAsync<UpdateResult>(new WorkParametersV1(Id), _input, HttpContext.RequestAborted);
            return LocalRedirect(MetadataAction());
        }

        public async Task<IActionResult> OnPostFranchiseAsync(string? franchiseName, decimal? franchisePosition)
        {
            _franchiseInput = new UpdateWorkFranchiseDataV1(franchiseName, franchisePosition);
            await updateFranchise.ExecuteAsync<UpdateResult>(new WorkParametersV1(Id), _franchiseInput, HttpContext.RequestAborted);
            return LocalRedirect(MetadataAction());
        }

        public async Task<IActionResult> OnPostGroupAsync(string? name, decimal? firstParentNumber, [BindRequired] decimal firstNumber, decimal? lastParentNumber, [BindRequired] decimal lastNumber)
        {
            _groupInput = new CreateWorkUnitGroupDataV1(name ?? "", firstParentNumber, firstNumber, lastParentNumber, lastNumber);
            await createGroup.ExecuteAsync<CreateWorkUnitGroupResultV1>(new WorkParametersV1(Id), _groupInput, HttpContext.RequestAborted);
            return LocalRedirect(MetadataAction());
        }

        public async Task<IActionResult> OnPostDeleteGroupAsync([BindRequired] long workUnitGroupId)
        {
            await deleteGroup.ExecuteAsync<UpdateResult>(new WorkUnitGroupParametersV1(Id, workUnitGroupId), default, HttpContext.RequestAborted);
            return LocalRedirect(MetadataAction());
        }

        public async Task<IActionResult> OnPostRefreshAsync()
        {
            await refresh.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(Id), default, HttpContext.RequestAborted);
            Hint("work.hint.refreshed");
            return LocalRedirect(Address());
        }

        // Acquisition

        // The title is monitored while it is not paused.
        public async Task<IActionResult> OnPostMonitorAsync([BindRequired] bool isPaused, long? wantedItemId)
        {
            var current = await readWanted.ExecuteAsync<WantedDetailsV1>(new WantedItemParametersV1(await WantedItemIdAsync(wantedItemId)), HttpContext.RequestAborted);
            await updateWanted.ExecuteAsync<UpdateResult>(new UpdateWantedParametersV1(current.Id),
                new UpdateWantedDataV1(isPaused, current.Version, current.AcquisitionProfileId, current.LibraryRootId), HttpContext.RequestAborted);
            Hint(isPaused ? "work.hint.monitorOff" : "work.hint.monitorOn");
            return LocalRedirect(Address(Tab));
        }

        public async Task<IActionResult> OnPostChoiceAsync([BindRequired] bool isPaused, [BindRequired] long version, long? acquisitionProfileId, long? libraryRootId, bool? monitorsNewUnits, long[]? tagIds, long? wantedItemId)
        {
            var targetId = await WantedItemIdAsync(wantedItemId);
            _choiceWantedId = targetId;
            _choice = new UpdateWantedDataV1(isPaused, version, acquisitionProfileId, libraryRootId, monitorsNewUnits, tagIds: tagIds ?? []);
            await updateWanted.ExecuteAsync<UpdateResult>(new UpdateWantedParametersV1(targetId), _choice, HttpContext.RequestAborted);
            return LocalRedirect(Address(Tab));
        }

        public async Task<IActionResult> OnPostAutomaticAsync(int? parentUnitNumber, int? unitNumber, long? wantedItemId, string? language)
        {
            var result = await automatic.ExecuteAsync<AutomaticSearchResultV1>(new WantedItemParametersV1(await WantedItemIdAsync(wantedItemId)), new SearchWantedDataV1(parentUnitNumber, unitNumber, language), HttpContext.RequestAborted);
            HintText(texts["acquisition.outcome." + result.OutcomeKey] + (result.Title is { } title ? ": " + title : ""));
            return LocalRedirect(Address(Tab));
        }

        public async Task<IActionResult> OnPostUnitAsync([BindRequired] long workUnitId, [BindRequired] bool isMonitored)
        {
            await updateUnit.ExecuteAsync<UpdateResult>(new WantedUnitParametersV1(await WantedItemIdAsync(), workUnitId), new UpdateWantedUnitDataV1(isMonitored), HttpContext.RequestAborted);
            Hint(isMonitored ? "work.hint.monitorOn" : "work.hint.monitorOff");
            return LocalRedirect(Address(Tab));
        }

        public async Task<IActionResult> OnPostMarkAsync([BindRequired] long acquisitionReleaseId, [BindRequired] AcquisitionReleaseStatusType acquisitionReleaseStatusTypeId)
        {
            await markRelease.ExecuteAsync<UpdateResult>(new WantedReleaseParametersV1(await WantedItemIdAsync(), acquisitionReleaseId), new MarkWantedReleaseDataV1(acquisitionReleaseStatusTypeId), HttpContext.RequestAborted);
            return LocalRedirect(Address(WorkTab.History));
        }

        // An edition of a book is wanted or not.
        public async Task<IActionResult> OnPostEditionMonitorAsync([BindRequired] long workEditionId, [BindRequired] bool isMonitored)
        {
            await updateEdition.ExecuteAsync<UpdateResult>(new WorkEditionParametersV1(Id, workEditionId), new UpdateWorkEditionDataV1(isMonitored), HttpContext.RequestAborted);
            Hint(isMonitored ? "work.hint.monitorOn" : "work.hint.monitorOff");
            return LocalRedirect(Address(Tab));
        }

        // The title leaves the library; the page it was on is gone, so the list follows.
        public async Task<IActionResult> OnPostDeleteTitleAsync(bool deletesFiles, bool addsExclusion)
        {
            _deleteInput = new DeleteWorkDataV1(deletesFiles, addsExclusion);
            await deleteWork.ExecuteAsync<UpdateResult>(new WorkParametersV1(Id), _deleteInput, HttpContext.RequestAborted);
            Hint("work.hint.titleDeleted");
            return LocalRedirect("/Admin/Works");
        }

        // Files

        public async Task<IActionResult> OnPostDeleteFilesAsync(long[]? storedFileIds)
        {
            if (storedFileIds is not { Length: > 0 })
            {
                Hint("work.hint.nothingChosen");
                return LocalRedirect(Address(Tab));
            }

            var result = await deleteFiles.ExecuteAsync<DeleteLibraryFilesResultV1>(new DeleteLibraryFilesParametersV1(), new DeleteLibraryFilesDataV1(storedFileIds), HttpContext.RequestAborted);
            HintText(string.Format(CultureInfo.CurrentCulture, texts["work.files.deleted"], result.DeletedCount)
                + (result.KeptCount > 0 ? " · " + string.Format(CultureInfo.CurrentCulture, texts["work.files.kept"], result.KeptCount) : ""));
            return LocalRedirect(Address(Tab));
        }

        public async Task<IActionResult> OnPostEditFilesAsync(long[]? storedFileIds, string? qualityKey, string? audioLanguage)
        {
            // The audio tracks of one file may each get a language; a track left unchanged sends nothing.
            var tracks = Request.Form.Where(field => field.Key.StartsWith("trackLanguage-", StringComparison.Ordinal) && !string.IsNullOrEmpty(field.Value.ToString()))
                .Select(field => new { StreamIndex = int.Parse(field.Key["trackLanguage-".Length..], CultureInfo.InvariantCulture), Language = field.Value.ToString() }).ToArray();
            if (storedFileIds is not { Length: > 0 } || string.IsNullOrEmpty(qualityKey) && string.IsNullOrEmpty(audioLanguage) && tracks.Length == 0)
            {
                Hint("work.hint.nothingChosen");
                return LocalRedirect(Address(Tab));
            }

            var result = await updateFacts.ExecuteAsync<UpdateLibraryFileFactsResultV1>(new UpdateLibraryFileFactsParametersV1(),
                new UpdateLibraryFileFactsDataV1(storedFileIds, string.IsNullOrEmpty(qualityKey) ? null : qualityKey, string.IsNullOrEmpty(audioLanguage) ? null : audioLanguage,
                    tracks.Length == 0 ? null : global::System.Text.Json.JsonSerializer.Serialize(tracks)), HttpContext.RequestAborted);
            Hint("work.files.updated", result.UpdatedCount);
            return LocalRedirect(Address(Tab));
        }

        // One language of a book's machine translation goes; the file stays.
        public async Task<IActionResult> OnPostDeleteTranslationAsync([BindRequired] long documentId, [BindRequired] string language)
        {
            await deleteTranslation.ExecuteAsync<UpdateResult>(new DeleteTranslationParametersV1(documentId, language), default, HttpContext.RequestAborted);
            Hint("work.hint.translationDeleted");
            return LocalRedirect(Address(Tab));
        }

        // The title's files move only for the plan that was shown.
        public async Task<IActionResult> OnPostRenameFilesAsync([BindRequired] string fingerprint)
        {
            var result = await applyRenames.ExecuteAsync<ApplyLibraryRenamesResultV1>(new LibraryRenamesParametersV1(), new ApplyLibraryRenamesDataV1(fingerprint, Id), HttpContext.RequestAborted);
            Hint("work.files.renamed", result.RenamedCount, result.SkippedCount);
            return LocalRedirect(Address(Tab));
        }

        // The title's target is found by the title, never taken from the request.
        // A game is wanted per platform: the target asked for must be one of the title's own.
        private async Task<long> WantedItemIdAsync(long? requested = null)
        {
            if (requested is { } target)
            {
                var page = await readManagement.ExecuteAsync<WorkManagementV1>(new WorkParametersV1(Id), HttpContext.RequestAborted);
                return page.Groups.Any(group => group.WantedItemId == target) ? target : throw new KeyNotFoundException("The target does not belong to the title.");
            }

            var work = await read.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(Id), HttpContext.RequestAborted);
            return work.WantedItemId ?? throw new KeyNotFoundException("The title is not wanted.");
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _work = await read.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(Id), ct);
            if (texts.CanGroupUnits() && (!Managed || Tab == WorkTab.Metadata))
            {
                _groups = await readGroups.ExecuteAsync<BaseDataList<WorkUnitGroupV1>>(new WorkParametersV1(Id), ct);
            }

            if (!Managed) return;

            _management = await readManagement.ExecuteAsync<WorkManagementV1>(new WorkParametersV1(Id), ct);
            if (texts.CanManageWanted() && _management.WantedItemId is { } wantedItemId)
            {
                _wanted = await readWanted.ExecuteAsync<WantedDetailsV1>(new WantedItemParametersV1(wantedItemId), ct);
                if (Tab == WorkTab.History) _releases = await readReleases.ExecuteAsync<BaseDataList<WantedReleaseV1>>(new WantedItemParametersV1(wantedItemId), ct);
                if (CanAcquire)
                {
                    _profiles = await profiles.ExecuteAsync<BaseDataList<AcquisitionProfileDetailsV1>>(new ReadAcquisitionProfilesParametersV1(wantedItemId), ct);
                    _libraries = await libraries.ExecuteAsync<BaseDataList<WantedLibraryV1>>(new WantedItemParametersV1(wantedItemId), ct);
                    var page = await tags.ExecuteAsync<PageResult<AcquisitionTagDetailsV1>>(new ReadAcquisitionTagsParametersV1(1), ct);
                    var choices = page.Items.ToList();
                    foreach (var id in (_choice?.TagIds ?? _wanted.TagIds).Where(id => !choices.Any(item => item.Id == id)).Take(100))
                    {
                        choices.Add(await tag.ExecuteAsync<AcquisitionTagDetailsV1>(new AcquisitionTagParametersV1(id), ct));
                    }

                    _tagChoices = choices.Select(item => new SelectOption(item.Id.ToString(CultureInfo.InvariantCulture), item.Label)).ToArray();
                }
            }

            // A game is wanted per platform; each target has its own settings to edit.
            if (_management.MediaTypeId == MediaType.Game && CanAcquire && texts.CanManageWanted())
            {
                foreach (var group in _management.Groups.Where(group => group.WantedItemId is not null))
                {
                    _platformTargets[group.WantedItemId!.Value] = await readWanted.ExecuteAsync<WantedDetailsV1>(new WantedItemParametersV1(group.WantedItemId.Value), ct);
                }
            }

            if (Tab == WorkTab.Files && texts.CanManageStorage()) _renames = await readRenames.ExecuteAsync<LibraryRenamePlanV1>(new ReadLibraryRenamesParametersV1(1, 100, Id), ct);
            if (Tab == WorkTab.History) _events = await readEvents.ExecuteAsync<PageResult<ActivityEventV1>>(new ReadActivityEventsParametersV1(Id, Math.Max(1, HistoryPage)), ct);
        }
    }
}
