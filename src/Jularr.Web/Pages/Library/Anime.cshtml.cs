using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Franchises;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.MediaFacts;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Presentation;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Tracking;
using Jularr.Web.Features.Watchlist;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Library;

public sealed class AnimeModel(
    AppDbContext db,
    AnimeMetadataService metadataService,
    CurrentAccountContext currentAccount,
    OperationRunner operations,
    EpisodeProgressService episodeProgressService,
    AniListAccountService aniListAccountService,
    FranchiseStore franchises,
    FranchiseService franchiseService,
    WatchlistStore watchlist,
    WatchlistLibraryResolver watchlistLibrary,
    AcquisitionAccessStore requestStore,
    IMediaCapabilityService mediaCapabilities,
    ILogger<AnimeModel> logger,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public Guid AnimeId { get; private set; }
    public string AnimeTitle { get; private set; } = "";
    public string LocalAnimeTitle { get; private set; } = "";
    public AnimeMetadata? Metadata { get; private set; }
    public AnimeLocalMetadata? LocalMetadata { get; private set; }
    public string? CoverImageUrl { get; private set; }
    public string? BannerImageUrl { get; private set; }
    public string SearchQuery { get; private set; } = "";
    public string? MetadataError { get; private set; }
    public IReadOnlyList<AnimeMetadataCandidate> SearchResults { get; private set; } = [];
    public IReadOnlyList<AnimeEpisodeMetadataMapping> EpisodeMappings { get; private set; } = [];
    public IReadOnlyList<EpisodeRow> Episodes { get; private set; } = [];

    // Owner-defined display grouping (#524). Empty for works with no groups, which then render as
    // a plain episode list exactly as before. Never alters episode identity or file paths.
    public IReadOnlyList<PresentationSection<EpisodeRow>> PresentationSections { get; private set; } = [];

    /// <summary>The season rail: the seasons (specials last), or the owner's display groups when the work has some.</summary>
    public IReadOnlyList<AnimeStructureEntry> Structure { get; private set; } = [];

    public AnimeStructureEntry? Selected { get; private set; }

    /// <summary>Season art by entry key; an entry without art shows its number instead.</summary>
    public IReadOnlyDictionary<string, string?> StructureArt { get; private set; } = new Dictionary<string, string?>();

    /// <summary>The episodes of the selected entry, in the chosen order.</summary>
    public IReadOnlyList<EpisodeRow> SelectedEpisodes { get; private set; } = [];

    public AnimeEpisodeSort Sort { get; private set; }
    public AnimeEpisodeLayout Layout { get; private set; }

    /// <summary>The episode the primary action opens, and what that action is called.</summary>
    public EpisodeRow? NextEpisode { get; private set; }
    public AnimePrimaryAction PrimaryAction { get; private set; } = AnimePrimaryAction.Start;

    public int WatchedEpisodeCount { get; private set; }

    /// <summary>The open request for this anime, if any: what is on its way.</summary>
    public AcquisitionRequestStatus? OpenRequest { get; private set; }

    /// <summary>Whether the anime is on the profile's watchlist ("My List"); null without a provider match to identify it.</summary>
    public bool? IsOnWatchlist { get; private set; }

    /// <summary>The address of the request form for more episodes or languages; null when the profile cannot request.</summary>
    public string? RequestHref { get; private set; }

    /// <summary>Related works of the franchise, ready to render as a compact list.</summary>
    public IReadOnlyList<RelatedWork> Related { get; private set; } = [];

    /// <summary>Related works beyond the ones listed; they are on the franchise page.</summary>
    public int RelatedMore { get; private set; }

    /// <summary>Related works listed in the side panel.</summary>
    public const int RelatedLimit = 6;
    public int SuggestedMappingSeason { get; private set; }
    public int SuggestedMappingEpisodeStart { get; private set; } = 1;
    public bool IsOwner => currentAccount.IsOwner;
    public bool ShowContentMetrics { get; private set; }
    public ExternalProgressSummary? ExternalProgress { get; private set; }
    public IReadOnlyList<FranchiseSummary> Franchises { get; private set; } = [];
    public IReadOnlyList<FranchiseRelationGroup> FranchiseGroups { get; private set; } = [];

    /// <summary>
    /// Language availability across this anime's episodes (#426). The hero already states the
    /// episode/season counts, runtime, year and provider status in its own words, so this only
    /// ever renders the language chips (showFacts: false) -- never a second, differently-worded
    /// copy of the same fact.
    /// </summary>
    public MediaFactsStripModel? Facts { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        Guid id,
        string? q,
        string? part,
        string? sort,
        string? view,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Sort = AnimeDetailView.ParseSort(sort);
        Layout = AnimeDetailView.ParseLayout(view);
        var anime = await db.Anime
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (anime is null)
        {
            return NotFound();
        }

        Facts = MediaFactsStripModel.Create(
            await new MediaFactsService(db).GetAnimeFactsAsync(id, cancellationToken),
            Ui,
            showFacts: false);
        AnimeId = anime.Id;
        LocalAnimeTitle = anime.Title;
        Metadata = await metadataService.GetAsync(id, cancellationToken);
        AnimeTitle = Metadata?.PreferredTitle ?? anime.Title;
        if (Metadata is { } franchiseMetadata &&
            !string.IsNullOrWhiteSpace(franchiseMetadata.Provider) &&
            !string.IsNullOrWhiteSpace(franchiseMetadata.ExternalId))
        {
            var identity = new WatchlistIdentity(
                WatchlistMediaType.Anime,
                franchiseMetadata.Provider,
                franchiseMetadata.ExternalId);
            Franchises = await franchises.FindForMemberAsync(identity, cancellationToken);
            FranchiseGroups = await franchiseService.GetRelationGroupsAsync(identity, cancellationToken);
        }

        // Local NFO plot/year is a display fallback only: it is never shown once provider
        // metadata exists, matching the manual > provider > NFO > folder precedence.
        LocalMetadata = Metadata is null
            ? await db.AnimeLocalMetadata
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.AnimeId == id, cancellationToken)
            : null;
        CoverImageUrl = AnimeArtworkStore.ResolvePosterUrl(
            id,
            Metadata?.CoverImageUrl);
        BannerImageUrl = AnimeArtworkStore.ResolveFanartUrl(
            id,
            Metadata?.BannerImageUrl);
        SearchQuery = string.IsNullOrWhiteSpace(q) ? anime.Title : q.Trim();

        var learning = await new LearningConfigurationStore(db, instanceModules).ResolveAsync(
            currentAccount.ProfileId,
            new LearningScopeContext(
                LearningMediaType.Anime,
                WorkKey: id.ToString()),
            cancellationToken);
        ShowContentMetrics =
            learning.IsEnabled(LearningCapability.ContentMetrics);

        if (TempData.TryGetValue("MetadataError", out var metadataError))
        {
            MetadataError = metadataError?.ToString();
        }

        if (IsOwner && !string.IsNullOrWhiteSpace(q))
        {
            try
            {
                SearchResults = await metadataService.SearchAsync(
                    AniListMetadataProvider.ProviderKey,
                    SearchQuery,
                    8,
                    cancellationToken);
            }
            catch (MetadataProviderException exception)
            {
                logger.LogError(exception, "Anime metadata search for {AnimeId} failed", id);
                MetadataError = Ui["library.anime.searchFailed"];
            }
        }

        var episodeRows = await db.Episodes
            .AsNoTracking()
            .Where(x => x.AnimeId == id)
            .OrderBy(x => x.SeasonNumber)
            .ThenBy(x => x.Number)
            .Select(episode => new
            {
                episode.Id,
                episode.SeasonNumber,
                episode.Number,
                episode.Title,
                JapaneseSubtitleTracks = db.SubtitleTracks.Count(
                    x => x.EpisodeId == episode.Id && x.Language == "ja")
            })
            .ToListAsync(cancellationToken);

        if (IsOwner)
        {
            try
            {
                EpisodeMappings = await metadataService.GetEpisodeMappingsAsync(
                    id,
                    cancellationToken);
            }
            catch (AniListAccountException exception)
            {
                logger.LogError(exception, "Loading episode mappings for anime {AnimeId} failed", id);
                MetadataError ??= Ui["library.anime.episodeMappingsLoadFailed"];
                EpisodeMappings = [];
            }

            var firstUnmapped = episodeRows.FirstOrDefault(episode =>
                episode.Number > 0 &&
                !EpisodeMappings.Any(mapping =>
                    mapping.Contains(episode.SeasonNumber, episode.Number)));

            if (firstUnmapped is not null)
            {
                SuggestedMappingSeason = firstUnmapped.SeasonNumber;
                SuggestedMappingEpisodeStart = firstUnmapped.Number;
            }
            else if (episodeRows.Count > 0)
            {
                SuggestedMappingSeason = episodeRows[0].SeasonNumber;
                SuggestedMappingEpisodeStart = Math.Max(1, episodeRows[0].Number);
            }
        }

        var episodeIds = episodeRows.Select(x => x.Id).ToArray();
        List<CoverageRow> coverageRows;

        if (!ShowContentMetrics || episodeIds.Length == 0)
        {
            coverageRows = [];
        }
        else
        {
            coverageRows = await (
                from episodeTerm in db.EpisodeTerms.AsNoTracking()
                join stateValue in LearningQueries.TermStates(db, currentAccount.ProfileId)
                    on episodeTerm.TermId equals stateValue.TermId into states
                from state in states.DefaultIfEmpty()
                where episodeIds.Contains(episodeTerm.EpisodeId)
                select new CoverageRow(
                    episodeTerm.EpisodeId,
                    episodeTerm.Occurrences,
                    state == null ? null : state.State))
                .ToListAsync(cancellationToken);
        }

        var coverageByEpisode = coverageRows
            .GroupBy(x => x.EpisodeId)
            .ToDictionary(
                group => group.Key,
                group => new Coverage(
                    group.Count(),
                    group.Sum(x => x.Occurrences),
                    group.Where(x => x.State is UserTermState.Known or UserTermState.Learning)
                        .Sum(x => x.Occurrences)));

        var progressByEpisode = await episodeProgressService.GetForAnimesAsync(
            [id],
            cancellationToken);
        var mediaByEpisode = await AnimeEpisodeMediaQuery.LoadAsync(db, id, cancellationToken);
        var preferences = await episodeProgressService.GetPreferencesAsync(cancellationToken);

        if (Metadata is { Provider.Length: > 0, ExternalId.Length: > 0 } requested)
        {
            OpenRequest = (await requestStore.FindOpenAsync(
                MediaAcquisitionKind.Anime,
                requested.Provider,
                requested.ExternalId,
                cancellationToken))?.Status;
        }

        Episodes = episodeRows
            .Select(episode =>
            {
                var coverage = coverageByEpisode.GetValueOrDefault(episode.Id, Coverage.Empty);
                var progress = progressByEpisode.GetValueOrDefault(episode.Id);
                var media = mediaByEpisode.GetValueOrDefault(episode.Id) ?? AnimeEpisodeMediaFacts.None;

                return new EpisodeRow(
                    episode.Id,
                    episode.SeasonNumber,
                    episode.Number,
                    episode.Title,
                    coverage.TotalTerms,
                    coverage.TotalOccurrences,
                    coverage.PreparedOccurrences,
                    episode.JapaneseSubtitleTracks,
                    progress?.IsCompleted == true,
                    progress is { IsCompleted: false, ResumePositionMs: > 0 }
                        ? progress.Percent
                        : null,
                    media.RuntimeMinutes ?? Metadata?.EpisodeDurationMinutes,
                    AnimeDetailView.Availability(
                        media,
                        OpenRequest,
                        preferences.PreferredAudioLanguage,
                        preferences.PreferredSubtitleLanguage),
                    AnimeDetailView.Chips(media.AudioLanguages, preferences.PreferredAudioLanguage),
                    AnimeDetailView.Chips(media.SubtitleLanguages, preferences.PreferredSubtitleLanguage));
            })
            .ToArray();

        var presentationGroups = await new PresentationGroupStore(db)
            .ListForWorkAsync(PresentationMediaType.Anime, id, cancellationToken);
        PresentationSections = PresentationGrouping.Arrange(
            presentationGroups,
            Episodes,
            episode => episode.Number,
            Ui["library.presentation.otherHeading"]);

        WatchedEpisodeCount = Episodes.Count(x => x.IsWatched);
        (NextEpisode, PrimaryAction) = AnimeDetailView.ChoosePrimary(
            Episodes,
            episode => episode.SeasonNumber,
            episode => episode.IsWatched,
            episode => episode.ResumePercent);
        BuildStructure(part);

        await LoadRelatedAsync(cancellationToken);
        await LoadWatchlistAndRequestAsync(cancellationToken);

        // Local-only: remote AniList progress is loaded after first paint
        // through OnGetExternalProgressAsync.
        ExternalProgress = await aniListAccountService.GetAnimeProgressSummaryAsync(
            id,
            cancellationToken);

        return Page();
    }

    /// <summary>The address of this page for a season, sort and layout; values that are the default stay out of it.</summary>
    public string PageHref(string? partKey, AnimeEpisodeSort sort, AnimeEpisodeLayout layout)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(partKey))
        {
            parts.Add($"part={Uri.EscapeDataString(partKey)}");
        }

        if (AnimeDetailView.SortName(sort) is { } sortName)
        {
            parts.Add($"sort={sortName}");
        }

        if (AnimeDetailView.LayoutName(layout) is { } layoutName)
        {
            parts.Add($"view={layoutName}");
        }

        var query = parts.Count == 0 ? "" : "?" + string.Join('&', parts);
        return $"/Library/Anime/{AnimeId}{query}#episodes";
    }

    // The season rail, the selected entry and its sorted episodes. An owner-defined display grouping
    // (#524) takes the place of the seasons in the rail, exactly as it did in the old list.
    private void BuildStructure(string? part)
    {
        string? keyOfNext = null;
        IReadOnlyList<EpisodeRow> PickEpisodes(AnimeStructureEntry entry) => entry.SeasonNumber is int season
            ? [.. Episodes.Where(x => x.SeasonNumber == season)]
            : PresentationSections[int.Parse(entry.Key.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture)].Items;

        if (PresentationSections.Count > 0)
        {
            Structure = AnimeDetailView.GroupEntries(
                PresentationSections.Select(section => (section.Name, section.Items.Count)));
            if (NextEpisode is not null)
            {
                var index = PresentationSections
                    .Select((section, position) => (section, position))
                    .FirstOrDefault(x => x.section.Items.Contains(NextEpisode)).position;
                keyOfNext = $"g{index}";
            }
        }
        else
        {
            Structure = AnimeDetailView.SeasonEntries(Episodes.Select(x => x.SeasonNumber));
            keyOfNext = NextEpisode is null ? null : $"s{NextEpisode.SeasonNumber}";
        }

        Selected = AnimeDetailView.SelectEntry(Structure, part, keyOfNext);
        SelectedEpisodes = Selected is null
            ? []
            : AnimeDetailView.Sort(PickEpisodes(Selected), Sort, x => x.Number, x => x.IsWatched);

        StructureArt = Structure.ToDictionary(
            entry => entry.Key,
            entry => entry.SeasonNumber is int season && season > 0
                ? AnimeArtworkStore.ResolveSeasonPosterUrl(AnimeId, season, Metadata?.CoverImageUrl)
                : CoverImageUrl,
            StringComparer.Ordinal);
    }

    private async Task LoadRelatedAsync(CancellationToken cancellationToken)
    {
        if (FranchiseGroups.Count == 0)
        {
            return;
        }

        var matches = await watchlistLibrary.ResolveAsync(
            FranchiseGroups.SelectMany(group => group.Items).Select(item => item.Identity),
            cancellationToken);
        var fallbackHref = Franchises.Count > 0 ? $"/Franchises/{Franchises[0].Id}" : null;

        var all = new List<RelatedWork>();
        foreach (var group in FranchiseGroups)
        {
            foreach (var item in group.Items)
            {
                var match = matches.GetValueOrDefault(item.Identity.Key);
                var href = match?.DetailsUrl ?? fallbackHref;
                if (href is null)
                {
                    continue;
                }

                all.Add(new RelatedWork(
                    item.Title,
                    match is not null && item.Identity.MediaType == WatchlistMediaType.Anime
                        ? AnimeArtworkStore.ResolvePosterUrl(match.MediaId, item.CoverImageUrl)
                        : item.CoverImageUrl,
                    group.GroupKey,
                    Jularr.Web.Features.Watchlist.WatchlistLabels.FormatKey(item.Format),
                    item.Identity.MediaType,
                    item.Year,
                    href,
                    match is not null));
            }
        }

        Related = [.. all.Take(RelatedLimit)];
        RelatedMore = Math.Max(0, all.Count - RelatedLimit);
    }

    private async Task LoadWatchlistAndRequestAsync(CancellationToken cancellationToken)
    {
        if (Metadata is not { Provider.Length: > 0, ExternalId.Length: > 0 } matched)
        {
            return;
        }

        IsOnWatchlist = (await watchlist.GetEffectiveKeysAsync(currentAccount.ProfileId, cancellationToken))
            .Contains(new WatchlistIdentity(WatchlistMediaType.Anime, matched.Provider, matched.ExternalId).Key);

        // Requesting more goes through the same request form Discover uses, and is offered only to
        // profiles whose capability for anime allows requesting (checked again when they submit).
        if (string.Equals(matched.Provider, AniListMetadataProvider.ProviderKey, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(matched.ExternalId, out _) &&
            await mediaCapabilities.GetEffectiveCapabilityAsync(User, WorkMediaType.Anime, cancellationToken) >= MediaCapability.Request)
        {
            RequestHref = Url.Page("/Requests/New", new
            {
                externalId = matched.ExternalId,
                title = AnimeTitle,
                coverImageUrl = matched.CoverImageUrl
            });
        }
    }

    public async Task<IActionResult> OnPostFollowAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (await WatchlistDraftOfAsync(id, cancellationToken) is not { } work)
        {
            return NotFound();
        }

        await watchlist.FollowAsync(currentAccount.ProfileId, work, cancellationToken);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnfollowAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (await WatchlistDraftOfAsync(id, cancellationToken) is not { } work)
        {
            return NotFound();
        }

        await watchlist.UnfollowAsync(currentAccount.ProfileId, work.Identity, cancellationToken);
        return RedirectToPage(new { id });
    }

    // The draft is built from the stored provider match, never from what the browser sends.
    private async Task<WatchlistDraft?> WatchlistDraftOfAsync(Guid id, CancellationToken cancellationToken)
    {
        var metadata = await metadataService.GetAsync(id, cancellationToken);
        if (metadata is null ||
            !WatchlistDraftInput.TryCreate(
                "anime",
                metadata.Provider,
                metadata.ExternalId,
                metadata.PreferredTitle,
                metadata.NativeTitle,
                metadata.CoverImageUrl,
                metadata.Format,
                metadata.Status,
                metadata.SeasonYear,
                out var draft))
        {
            return null;
        }

        return draft;
    }

    public async Task<IActionResult> OnGetExternalProgressAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!await db.Anime.AsNoTracking().AnyAsync(x => x.Id == id, cancellationToken))
        {
            return NotFound();
        }

        var state = await aniListAccountService.GetAnimeProgressStateAsync(
            id,
            cancellationToken);
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        Response.Headers.CacheControl = "no-store";
        return Partial(
            "_ExternalProgressState",
            new ExternalProgressRemoteView(
                ExternalProgressMediaKind.Anime,
                state,
                "SyncAniList",
                ui));
    }

    public async Task<IActionResult> OnPostSyncAniListAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await operations.RunAsync(
            new OperationDescriptor(
                "anilist-anime-progress-sync",
                "AniList",
                "Sync anime progress",
                ProfileId: currentAccount.ProfileId,
                Lane: OperationLane.Normal,
                Retryable: false),
            (_, token) => aniListAccountService.SyncAnimeProgressAsync(
                id,
                token),
            "Anime progress sync completed.",
            cancellationToken);

        TempData["Status"] = result.Message;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostWatchedAsync(
        Guid id,
        Guid episodeId,
        bool watched,
        string? part,
        string? sort,
        string? view,
        CancellationToken cancellationToken)
    {
        var belongsToAnime = await db.Episodes
            .AsNoTracking()
            .AnyAsync(x => x.Id == episodeId && x.AnimeId == id, cancellationToken);

        if (!belongsToAnime)
        {
            return NotFound();
        }

        await episodeProgressService.SetWatchedAsync(
            episodeId,
            watched,
            cancellationToken);

        // Back to the same season, order and layout, at the episode list.
        return RedirectToPage(
            null,
            null,
            new
            {
                id,
                part = string.IsNullOrWhiteSpace(part) ? null : part,
                sort = AnimeDetailView.SortName(AnimeDetailView.ParseSort(sort)),
                view = AnimeDetailView.LayoutName(AnimeDetailView.ParseLayout(view))
            },
            "episodes");
    }

    public async Task<IActionResult> OnPostMatchMetadataAsync(
        Guid id,
        string provider,
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!IsOwner)
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        try
        {
            await operations.RunAsync(
                new OperationDescriptor(
                    "anime-metadata-match",
                    "Anime",
                    "Match anime metadata",
                    ProfileId: currentAccount.ProfileId,
                    Lane: OperationLane.Normal,
                    Retryable: false),
                async (_, token) =>
                {
                    var result = await metadataService.MatchAsync(
                        id,
                        provider,
                        externalId,
                        token);

                    if (!result.Success)
                    {
                        throw new InvalidOperationException(
                            result.Error ?? "Anime metadata could not be matched.");
                    }
                },
                "Anime metadata matched.",
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is MetadataProviderException or InvalidOperationException)
        {
            logger.LogError(exception, "Matching anime metadata for {AnimeId} failed", id);
            TempData["MetadataError"] = Ui["library.anime.metadataMatchFailed"];
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostMatchEpisodeRangeAsync(
        Guid id,
        int seasonNumber,
        int localEpisodeStart,
        int? localEpisodeEnd,
        int remoteEpisodeStart,
        string provider,
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!IsOwner)
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        try
        {
            await operations.RunAsync(
                new OperationDescriptor(
                    "anime-episode-range-match",
                    "Anime",
                    "Match anime episode range",
                    ProfileId: currentAccount.ProfileId,
                    Lane: OperationLane.Normal,
                    Retryable: false),
                async (_, token) =>
                {
                    var result = await metadataService.MatchEpisodeRangeAsync(
                        id,
                        seasonNumber,
                        localEpisodeStart,
                        localEpisodeEnd,
                        remoteEpisodeStart,
                        provider,
                        externalId,
                        token);

                    if (!result.Success)
                    {
                        throw new InvalidOperationException(
                            result.Error ?? "Episode range could not be matched.");
                    }
                },
                "Anime episode range matched.",
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is MetadataProviderException or InvalidOperationException)
        {
            logger.LogError(exception, "Matching anime episode range for {AnimeId} failed", id);
            TempData["MetadataError"] = Ui["library.anime.episodeRangeMatchFailed"];
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveEpisodeMappingAsync(
        Guid id,
        Guid mappingId,
        CancellationToken cancellationToken)
    {
        if (!IsOwner)
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        try
        {
            if (!await metadataService.RemoveEpisodeMappingAsync(
                    id,
                    mappingId,
                    cancellationToken))
            {
                TempData["MetadataError"] = Ui["library.anime.episodeMappingNotFound"];
            }
        }
        catch (AniListAccountException exception)
        {
            logger.LogError(exception, "Removing episode mapping {MappingId} for anime {AnimeId} failed", mappingId, id);
            TempData["MetadataError"] = Ui["library.anime.episodeMappingRemoveFailed"];
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRefreshMetadataAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!IsOwner)
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        try
        {
            await operations.RunAsync(
                new OperationDescriptor(
                    "anime-metadata-refresh",
                    "Anime",
                    "Refresh anime metadata",
                    ProfileId: currentAccount.ProfileId,
                    Lane: OperationLane.Normal,
                    Retryable: false),
                async (_, token) =>
                {
                    if (!await metadataService.RefreshAsync(id, token))
                    {
                        throw new InvalidOperationException(
                            "Metadata could not be refreshed.");
                    }
                },
                "Anime metadata refreshed.",
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is MetadataProviderException or InvalidOperationException)
        {
            logger.LogError(exception, "Refreshing anime metadata for {AnimeId} failed", id);
            TempData["MetadataError"] = Ui["library.anime.metadataRefreshFailed"];
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveMetadataAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!IsOwner)
        {
            return Forbid();
        }

        await metadataService.RemoveAsync(id, cancellationToken);
        return RedirectToPage(new { id });
    }

    private sealed record CoverageRow(
        Guid EpisodeId,
        int Occurrences,
        UserTermState? State);

    private sealed record Coverage(
        int TotalTerms,
        int TotalOccurrences,
        int PreparedOccurrences)
    {
        public static Coverage Empty { get; } = new(0, 0, 0);
    }

    /// <param name="RuntimeMinutes">From the file when it was analysed, otherwise what the provider states per episode.</param>
    public sealed record EpisodeRow(
        Guid Id,
        int SeasonNumber,
        int Number,
        string Title,
        int TotalTerms,
        int TotalOccurrences,
        int PreparedOccurrences,
        int JapaneseSubtitleTracks,
        bool IsWatched,
        int? ResumePercent,
        int? RuntimeMinutes,
        AnimeEpisodeAvailability Availability,
        AnimeLanguageChips Audio,
        AnimeLanguageChips Subtitles)
    {
        public int PreparationPercent => TotalOccurrences == 0
            ? 0
            : (int)Math.Floor((double)PreparedOccurrences / TotalOccurrences * 100);
    }

    /// <summary>One entry of the Related Works panel.</summary>
    /// <param name="GroupKey">The catalog key of the relation group, for example "Sequels &amp; prequels".</param>
    /// <param name="FormatKey">The catalog key of the provider format (TV, Movie, OVA), when it is known.</param>
    public sealed record RelatedWork(
        string Title,
        string? CoverImageUrl,
        string GroupKey,
        string? FormatKey,
        WatchlistMediaType MediaType,
        int? Year,
        string Href,
        bool InLibrary);
}
