using System.Globalization;
using Jularr.Backend.Data;
using AdminMedia = Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Data.User.Collection.V1;
using Jularr.Backend.Data.User.Discover.V1;
using AdminServices = Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Backend.Service.User.Collection.V1;
using Jularr.Backend.Service.User.Discover.V1;
using Jularr.Backend.Service.User.Request.V1;
using Jularr.Backend.Service.Public.Localization.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.User.Details
{
    // A title's own page: the hero, one row of tabs and the blocks of the Overview, each of which is also a tab of its own ([hero](Details.Hero.cs),
    // [tabs and blocks](Details.Blocks.cs), [units](Details.Units.cs), [versions](Details.Versions.cs)).
    public sealed partial class DetailsModel(ReadDiscoverExternalDetailsV1 readExternal, FavoriteDiscoverCatalogItemV1 favoriteExternal, ReadWorkV1 read, ReadWorkAvailabilityV1 readAvailability, ReadWorkEditionsV1 readEditions, UpdateWatchlistV1 update, CreateRequestV1 request,
        ReadUiLocalesV1 locales, ReadWorkPlatformsV1 readPlatforms, ReadRequestProfilesV1 readRequestProfiles, ReadWorkCollectionsV1 readCollections, AddCollectionWorkV1 addToCollection,
        RemoveCollectionWorkV1 removeFromCollection, ReadWorkSeasonsV1 readSeasons, ReadWorkEpisodesV1 readEpisodes, ReadWorkCreditsV1 readCredits,
        ReadWorkRecommendationsV1 readRecommendations, ReadWorkRelationsV1 readRelations, AdminServices.CreateWorkRelationV1 createRelation, AdminServices.DeleteWorkRelationV1 deleteRelation, SuggestDiscoverV1 suggest,
        ReadDiscoverScopesV1 readScopes, RequestDiscoverCatalogItemV1 requestCatalog, RequestDiscoverItemV1 requestTmdb, UpdateWorkProgressV1 updateProgress, IAntiforgery antiforgery, FrontendAssets assets, UiText texts) : JularrPage
    {
        private IReadOnlyList<DiscoverItemV1> _recommendations = [];
        private IReadOnlyList<DiscoverScopeV1> _scopes = [];
        private IReadOnlyList<WorkCreditV1> _credits = [];
        private IReadOnlyList<WorkSeasonV1> _seasons = [];
        private IReadOnlyList<WorkEpisodeV1> _episodes = [];
        private IReadOnlyList<WorkEditionV1> _editions = [];
        private WorkDetailsV1? _work;
        private DiscoverExternalDetailsV1? _external;
        private DiscoverScopeV1? _externalScope;
        private IReadOnlyList<WorkCollectionV1> _collections = [];
        private PageResult<WorkAvailabilityV1>? _availability;
        private BaseDataList<UiLocaleDetailsV1>? _locales;
        private BaseDataList<GamePlatformV1>? _platforms;
        private BaseDataList<RequestProfileOptionV1>? _requestProfiles;
        private CreateRequestDataV1? _requestInput;
        private string _csrf = "";

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        [BindProperty(SupportsGet = true, Name = "source")]
        public string? ExternalSource { get; set; }

        [BindProperty(SupportsGet = true, Name = "externalId")]
        public string? ExternalId { get; set; }

        [BindProperty(SupportsGet = true, Name = "type")]
        public string? ExternalType { get; set; }

        private bool IsExternal => !string.IsNullOrWhiteSpace(ExternalSource);

        // One bounded page of the title's files.
        [BindProperty(SupportsGet = true, Name = "p")]
        public int PageNumber { get; set; } = 1;

        // The season whose episodes are shown; the first numbered one by default.
        [BindProperty(SupportsGet = true, Name = "season")]
        public int? SeasonNumber { get; set; }

        // The tab, how its episodes are ordered and shown, the volume whose chapters are listed and the version, edition or platform the primary action opens; all in the address.
        [BindProperty(SupportsGet = true, Name = "tab")]
        public string? TabKey { get; set; }

        [BindProperty(SupportsGet = true, Name = "sort")]
        public string? Sort { get; set; }

        [BindProperty(SupportsGet = true, Name = "view")]
        public string? Layout { get; set; }

        [BindProperty(SupportsGet = true, Name = "volume")]
        public string? VolumeKey { get; set; }

        [BindProperty(SupportsGet = true, Name = "version")]
        public string? VersionKey { get; set; }

        protected override void Define(Page page)
        {
            page.Title("details.title", _work?.CanonicalTitle);
            page.Parent("nav.library", "/Library");
            page.Navigation("nav.home");
            page.Asset("Pages/User/Details/Details.entry.scss");
            page.Asset("Framework/Components/Truncated.entry.ts");
            if (TabKey == "files") page.Asset("Framework/Components/InfiniteList.entry.ts");

            // Persisted media identity.
            if (_work is { } work)
            {
                _csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "";
                page.Asset("Framework/Components/MediaHero.entry.ts");
                page.Asset("Framework/Components/SelectLinkMenu.entry.ts");
                page.Asset("Framework/Components/MediaCard.entry.ts");
                var blocks = Blocks(work);
                var shown = blocks.FirstOrDefault(block => block.TabKey == TabKey);

                // A refused action of the page that no form of it shows (a relation added twice) says why above the page.
                if (FormError is { } refused && _requestInput is null && _relationSearch is null) page.Content.Add(new P(ErrorText.Of(refused)).Class("ui-page-error").Attribute("role", "alert"));
                page.Content.Add(Hero(work, page));
                page.Content.Add(new Div(new NavigationTabs([new NavigationTab("details.tab.overview", Address(query => query["tab"] = null), shown is null),
                    .. blocks.Select(block => new NavigationTab(block.LabelKey, Address(query => query["tab"] = block.TabKey), block == shown, false, block.Count))], "details.tabs")).Class("details-tabs"));
                page.Content.Add(new Div(shown is null ? [.. blocks.Select(Preview)] : [shown.Render(false)]).Class("details-panel"));
                if (!IsExternal && texts.CanManageRelations()) page.Content.Add(RelationDialog());
            }
            else if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        // The address of this page with its choices kept; the change sets or clears some of them, and the file page is left unless the change names it.
        private string Address(Action<Dictionary<string, string?>>? change = null)
        {
            var query = new Dictionary<string, string?>
            {
                ["tab"] = TabKey,
                ["season"] = SeasonNumber?.ToString(CultureInfo.InvariantCulture),
                ["sort"] = Sort,
                ["view"] = Layout,
                ["volume"] = VolumeKey,
                ["version"] = VersionKey,
                ["p"] = null
            };
            change?.Invoke(query);
            var parts = query.Where(item => !string.IsNullOrEmpty(item.Value)).Select(item => item.Key + "=" + Uri.EscapeDataString(item.Value!));
            var root = IsExternal
                ? "/Details?source=" + Uri.EscapeDataString(ExternalSource!) + "&externalId=" + Uri.EscapeDataString(ExternalId ?? "")
                    + "&type=" + Uri.EscapeDataString(ExternalType ?? "")
                : "/Details/" + Id.ToString(CultureInfo.InvariantCulture);
            var suffix = string.Join('&', parts);
            return root + (suffix.Length == 0 ? "" : (IsExternal ? "&" : "?") + suffix);
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            if (_external?.Entry.WorkId is { } known)
                return LocalRedirect("/Details/" + known.ToString(CultureInfo.InvariantCulture)
                    + (_external.Entry.SeasonNumber is { } season ? "?season=" + season.ToString(CultureInfo.InvariantCulture) : ""));
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAddToCollectionAsync([BindRequired] long collectionId)
        {
            await addToCollection.ExecuteAsync<UpdateResult>(new CollectionWorkParametersV1(collectionId, Id), default, HttpContext.RequestAborted);
            return RedirectToPage("/User/Details/Details", new { id = Id });
        }

        public async Task<IActionResult> OnPostRemoveFromCollectionAsync([BindRequired] long collectionId)
        {
            await removeFromCollection.ExecuteAsync<UpdateResult>(new CollectionWorkParametersV1(collectionId, Id), default, HttpContext.RequestAborted);
            return RedirectToPage("/User/Details/Details", new { id = Id });
        }

        // The matches for the words of the dialog; the Work itself is never offered as its own relation.
        public async Task<IActionResult> OnPostSearchRelationAsync([BindRequired] WorkRelationType workRelationTypeId, string? query)
        {
            _relationSearch = new WorkRelationSearchData(workRelationTypeId, query ?? "");
            await LoadDataAsync();
            _relationResults = (await suggest.ExecuteAsync<BaseDataList<DiscoverSuggestionV1>>(new DiscoverParametersV1(), new SuggestDiscoverDataV1(query ?? ""), HttpContext.RequestAborted))
                .Where(item => item.WorkId != Id).ToArray();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAddRelationAsync([BindRequired] WorkRelationType workRelationTypeId, long? relatedWorkId, string? @namespace, string? externalId)
        {
            await createRelation.ExecuteAsync<UpdateResult>(new AdminMedia.WorkParametersV1(Id), new AdminMedia.CreateWorkRelationDataV1(workRelationTypeId, relatedWorkId, @namespace, externalId),
                HttpContext.RequestAborted);
            Hint("details.relation.added");
            return LocalRedirect(Address(query => query["tab"] = "related"));
        }

        public async Task<IActionResult> OnPostRemoveRelationAsync([BindRequired] long workRelationId)
        {
            await deleteRelation.ExecuteAsync<UpdateResult>(new AdminMedia.WorkRelationParametersV1(Id, workRelationId), default, HttpContext.RequestAborted);
            Hint("details.relation.removed");
            return LocalRedirect(Address(query => query["tab"] = "related"));
        }

        // A related title that is no Work is requested from its row as Discover would, in the profile's language; the row then shows the Work with its state.
        public async Task<IActionResult> OnPostRequestRelatedAsync([BindRequired] string @namespace, [BindRequired] string externalId)
        {
            var ct = HttpContext.RequestAborted;
            var related = (await readRelations.ExecuteAsync<BaseDataList<WorkRelationV1>>(new WorkParametersV1(Id), ct))
                .FirstOrDefault(relation => relation.RelatedWorkId is null && relation.Namespace == @namespace && relation.ExternalId == externalId) ?? throw new KeyNotFoundException("The relation was not found.");
            var languages = await locales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: ct);
            var language = languages.FirstOrDefault(locale => string.Equals(locale.Locale, texts.Locale, StringComparison.OrdinalIgnoreCase)) ?? languages.First();
            if (@namespace is "tmdb_movie" or "tmdb_tv" && int.TryParse(externalId, NumberStyles.None, CultureInfo.InvariantCulture, out var tmdbId))
            {
                await requestTmdb.ExecuteAsync<CreateRequestResultV1>(new DiscoverItemParametersV1(related.MediaTypeId, tmdbId), new CreateRequestDataV1(language.Id), ct);
            }
            else
            {
                await requestCatalog.ExecuteAsync<CreateRequestResultV1>(new DiscoverParametersV1(), new RequestDiscoverCatalogDataV1(related.MediaTypeId, @namespace, externalId, language.Id, related.MediaSubtypeId), ct);
            }

            Hint("details.relation.requested");
            _requestedRelation = (@namespace, externalId);
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] bool isWatchlisted, string? returnUrl = null)
        {
            await update.ExecuteAsync<UpdateResult>(new WorkParametersV1(Id), new UpdateWatchlistDataV1(isWatchlisted), HttpContext.RequestAborted);
            if (Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
            return RedirectToPage("/User/Details/Details", new { id = Id });
        }

        public async Task<IActionResult> OnPostProgressAsync([BindRequired] ProgressAction action, long? unitId, string? playHref, string? returnUrl)
        {
            await updateProgress.ExecuteAsync<UpdateResult>(new WorkParametersV1(Id), new UpdateWorkProgressDataV1(action, unitId), HttpContext.RequestAborted);

            // Starting over opens the player or the reader at the beginning.
            if (playHref is not null && Url.IsLocalUrl(playHref) && (playHref.StartsWith("/Player/", StringComparison.Ordinal) || playHref.StartsWith("/Read/", StringComparison.Ordinal)))
            {
                return LocalRedirect(playHref);
            }

            if (Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
            return RedirectToPage("/User/Details/Details", new { id = Id });
        }

        public async Task<IActionResult> OnPostRequestAsync([BindRequired] long uiLocaleId, long? gamePlatformId, long? acquisitionProfileId, int? startSeasonNumber)
        {
            _requestInput = new CreateRequestDataV1(uiLocaleId, gamePlatformId, acquisitionProfileId, startSeasonNumber);
            await request.ExecuteAsync<CreateRequestResultV1>(new CreateRequestParametersV1(Id), _requestInput, HttpContext.RequestAborted);
            return RedirectToPage("/User/Requests/Requests");
        }

        public async Task<IActionResult> OnPostFavoriteExternalAsync()
        {
            var scope = await ExternalScopeAsync();
            var result = await favoriteExternal.ExecuteAsync<FavoriteDiscoverResultV1>(new DiscoverParametersV1(),
                new ReadDiscoverCatalogEntryDataV1(scope.MediaTypeId, ExternalSource!, ExternalId!, scope.MediaSubtypeId),
                HttpContext.RequestAborted);
            return LocalRedirect("/Details/" + result.WorkId.ToString(CultureInfo.InvariantCulture));
        }

        public async Task<IActionResult> OnPostRequestExternalAsync([BindRequired] long uiLocaleId, int? startSeasonNumber)
        {
            var scope = await ExternalScopeAsync();
            if (ExternalSource is "tmdb_movie" or "tmdb_tv")
            {
                var tmdbId = int.Parse(ExternalId!, NumberStyles.None, CultureInfo.InvariantCulture);
                await requestTmdb.ExecuteAsync<CreateRequestResultV1>(new DiscoverItemParametersV1(scope.MediaTypeId, tmdbId),
                    new CreateRequestDataV1(uiLocaleId, startSeasonNumber: startSeasonNumber), HttpContext.RequestAborted);
            }
            else
            {
                await requestCatalog.ExecuteAsync<CreateRequestResultV1>(new DiscoverParametersV1(),
                    new RequestDiscoverCatalogDataV1(scope.MediaTypeId, ExternalSource!, ExternalId!, uiLocaleId, scope.MediaSubtypeId, startSeasonNumber),
                    HttpContext.RequestAborted);
            }
            return RedirectToPage("/User/Requests/Requests");
        }

        private async Task<DiscoverScopeV1> ExternalScopeAsync()
        {
            if (string.IsNullOrWhiteSpace(ExternalSource) || string.IsNullOrWhiteSpace(ExternalId) || string.IsNullOrWhiteSpace(ExternalType))
                throw new KeyNotFoundException("Missing external identity.");
            return _externalScope ??= (await readScopes.ExecuteAsync<BaseDataList<DiscoverScopeV1>>(
                new DiscoverParametersV1(), HttpContext.RequestAborted)).FirstOrDefault(scope => scope.Slug == ExternalType)
                ?? throw new KeyNotFoundException("Unknown media category.");
        }

        protected override async Task LoadDataAsync()
        {
            if (IsExternal)
            {
                var scope = await ExternalScopeAsync();
                _external = await readExternal.ExecuteAsync<DiscoverExternalDetailsV1>(new DiscoverParametersV1(),
                    new ReadDiscoverCatalogEntryDataV1(scope.MediaTypeId, ExternalSource!, ExternalId!, scope.MediaSubtypeId),
                    HttpContext.RequestAborted);
                var entry = _external.Entry;
                _seasons = _external.Seasons;
                if (SeasonNumber is null && entry.SeasonNumber is { } mappedSeason) SeasonNumber = mappedSeason;
                _episodes = _external.Episodes;
                if (texts.CanSeeRelations())
                {
                    _relations = _external.Entry.Relations;
                    if (_relations.Count > 0)
                        _scopes = await readScopes.ExecuteAsync<BaseDataList<DiscoverScopeV1>>(new DiscoverParametersV1(), HttpContext.RequestAborted);
                }
                var progress = _external.AniListProgress;
                var total = _external.AniListTotal;
                var percent = progress is { } count && total is > 0 ? (int)Math.Clamp((long)count * 100 / total.Value, 0, 100) : 0;
                var progressState = progress is > 0 && total is > 0 && progress >= total
                    ? ProgressStateType.Finished : progress is > 0 ? ProgressStateType.InProgress : ProgressStateType.NotStarted;
                _work = new WorkDetailsV1(0, scope.MediaTypeId, scope.MediaSubtypeId, entry.Title, false,
                    entry.ReleaseYear, entry.Overview, entry.PosterUrl, entry.BackdropUrl, null, [], null, null,
                    true, null, null, null, entry.TrailerKey, entry.Author, MediaAvailabilityState.Missing,
                    0, 0, progressState, percent, null, null, null, null, null);
                if (texts.CanRequest())
                    _locales = await locales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: HttpContext.RequestAborted);
                return;
            }
            _work = await read.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(Id), HttpContext.RequestAborted);
            if (_work.MediaTypeId == MediaType.Series)
            {
                _seasons = await readSeasons.ExecuteAsync<BaseDataList<WorkSeasonV1>>(new WorkParametersV1(Id), HttpContext.RequestAborted);
                if (SelectedSeason is { } season)
                {
                    _episodes = await readEpisodes.ExecuteAsync<BaseDataList<WorkEpisodeV1>>(new ReadWorkEpisodesParametersV1(Id, season), HttpContext.RequestAborted);
                }
            }

            _availability = await readAvailability.ExecuteAsync<PageResult<WorkAvailabilityV1>>(new ReadWorkAvailabilityParametersV1(Id, page: PageNumber, pageSize: 48), HttpContext.RequestAborted);
            _editions = await readEditions.ExecuteAsync<BaseDataList<WorkEditionV1>>(new WorkParametersV1(Id), HttpContext.RequestAborted);
            _credits = await readCredits.ExecuteAsync<BaseDataList<WorkCreditV1>>(new WorkParametersV1(Id), HttpContext.RequestAborted);
            if (texts.CanSeeRelations())
            {
                _relations = await readRelations.ExecuteAsync<BaseDataList<WorkRelationV1>>(new WorkParametersV1(Id), HttpContext.RequestAborted);
            }

            // The Discover pages of titles that are no Work.
            if (_relations.Any(relation => relation.RelatedWorkId is null))
            {
                _scopes = await readScopes.ExecuteAsync<BaseDataList<DiscoverScopeV1>>(new DiscoverParametersV1(), HttpContext.RequestAborted);
            }

            try
            {
                _recommendations = await readRecommendations.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new WorkParametersV1(Id), default(NoData), HttpContext.RequestAborted);
                if (_recommendations.Count > 0 && _scopes.Count == 0) _scopes = await readScopes.ExecuteAsync<BaseDataList<DiscoverScopeV1>>(new DiscoverParametersV1(), HttpContext.RequestAborted);
            }
            catch (ServiceUnavailableException)
            {
                // The Work stays complete without its provider's suggestions.
            }
            if (texts.CanCollect())
            {
                _collections = await readCollections.ExecuteAsync<BaseDataList<WorkCollectionV1>>(new WorkParametersV1(Id), HttpContext.RequestAborted);
            }

            // A game's platforms: the ones the title is on, listed with those that are here.
            if (_work.MediaTypeId == MediaType.Game)
            {
                _platforms = await readPlatforms.ExecuteAsync<BaseDataList<GamePlatformV1>>(new WorkParametersV1(Id), HttpContext.RequestAborted);
            }

            if (texts.CanRequest())
            {
                _locales = await locales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: HttpContext.RequestAborted);
                _requestProfiles = await readRequestProfiles.ExecuteAsync<BaseDataList<RequestProfileOptionV1>>(new CreateRequestParametersV1(Id), HttpContext.RequestAborted);
            }
        }
    }
}
