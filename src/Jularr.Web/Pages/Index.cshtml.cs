using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Calendar;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Reading;
using Jularr.Web.Features.Recommendations;
using Jularr.Web.Features.Watchlist;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages;

/// <summary>
/// Home (docs/mockups/home/SPEC.md): a hero carousel of the profile's own media, one merged
/// Continue row, the local "For you" row and recently discovered episodes. Every read is local —
/// the database, the local release cache and the local recommendation engine; no provider calls.
/// </summary>
public sealed class IndexModel(
    AppDbContext db,
    CurrentAccountContext currentAccount,
    EpisodeProgressService progress,
    MediaRecommendationService? recommendations = null,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    /// <summary>
    /// The Home media-type filters, parsed from <c>?type=</c> the same way as Discover's
    /// <c>category</c>. Home no longer renders a chip row (the approved mockup has none), but links
    /// such as <c>/?type=manga</c> keep narrowing the Continue rows and the hero.
    /// </summary>
    public static readonly IReadOnlyList<HomeTypeChip> TypeChips =
    [
        new(DiscoveryCategory.All, "all", "home.filter.all"),
        new(DiscoveryCategory.Anime, "anime", "home.filter.anime"),
        new(DiscoveryCategory.Manga, "manga", "home.filter.manga"),
        new(DiscoveryCategory.LightNovel, "novels", "home.filter.novels"),
        new(DiscoveryCategory.Book, "books", "home.filter.books")
    ];

    /// <summary>Most slides the hero carousel holds.</summary>
    public const int HeroLimit = 6;

    /// <summary>
    /// How long the active hero segment takes to fill before the hero advances. Rendered as the
    /// hero's <c>data-interval-ms</c>; the script reads it on every frame and has no default of its own.
    /// </summary>
    public const int HeroIntervalMs = 30_000;

    /// <summary>Most slides one source (resume, reading, up next, watchlist) may contribute, so the hero stays mixed.</summary>
    public const int HeroPerSourceLimit = 2;

    /// <summary>A watchlist release counts as "new" this many days after it came out.</summary>
    public const int HeroRecentDays = 14;

    /// <summary>Upcoming watchlist releases are considered this many days ahead.</summary>
    public const int HeroUpcomingDays = 30;

    /// <summary>Most posters in the "For you" row.</summary>
    public const int ForYouLimit = 12;

    public IReadOnlyList<HomeEpisode> RecentEpisodes { get; private set; } = [];
    public IReadOnlyList<ContinueWatchingItem> ContinueWatching { get; private set; } = [];

    /// <summary>
    /// Most recently read unfinished Novels, Books and Manga of the current
    /// profile, newest first, each with its exact reader resume URL.
    /// </summary>
    public IReadOnlyList<ContinueReadingItem> ContinueReading { get; private set; } = [];

    /// <summary>
    /// The hero carousel, in this order: in-progress episodes (newest first), in-progress reading,
    /// up-next episodes, then new or upcoming releases of followed watchlist works. Only the
    /// profile's own media; empty means no hero at all.
    /// </summary>
    public IReadOnlyList<HomeHeroSlide> Hero { get; private set; } = [];

    /// <summary>The single Continue row: watching and reading tiles merged newest first.</summary>
    public IReadOnlyList<HomeContinueTile> ContinueTiles { get; private set; } = [];

    /// <summary>Posters from the local recommendation engine (#428), one per canonical work.</summary>
    public IReadOnlyList<HomePosterItem> ForYou { get; private set; } = [];

    /// <summary>The active Home media-type filter, from the <c>type</c> query parameter.</summary>
    public DiscoveryCategory ActiveType { get; private set; } = DiscoveryCategory.All;

    /// <summary>Discover link for the Continue Watching heading; anime is the only Continue Watching medium.</summary>
    public string ContinueWatchingDiscoverUrl => "/Discover?category=anime&mode=my-list";

    /// <summary>
    /// Discover link for the Continue Reading heading. Matches the active
    /// filter so a filtered row always points at the same category in
    /// Discover; unset (all media) when no specific filter is active.
    /// </summary>
    public string ContinueReadingDiscoverUrl => ActiveType switch
    {
        DiscoveryCategory.Manga => "/Discover?category=manga&mode=my-list",
        DiscoveryCategory.LightNovel => "/Discover?category=light-novel&mode=my-list",
        DiscoveryCategory.Book => "/Discover?category=book&mode=my-list",
        _ => "/Discover?mode=my-list"
    };

    /// <summary>The merged row keeps the Continue Watching heading and link while anything is being watched.</summary>
    public string ContinueHeading => ContinueWatching.Count > 0
        ? Ui["home.continueWatching"]
        : Ui["home.continueReading"];

    public string ContinueDiscoverUrl => ContinueWatching.Count > 0
        ? ContinueWatchingDiscoverUrl
        : ContinueReadingDiscoverUrl;

    public IReadOnlyList<PlaybackHistoryItem> PlaybackHistory { get; private set; } = [];

    /// <summary>False on a manager-only instance: nothing on the page links into the player.</summary>
    public bool PlaybackEnabled { get; private set; } = true;

    /// <summary>A recently added episode opens the player where there is one, otherwise the title it belongs to.</summary>
    public string EpisodeHref(HomeEpisode episode) => PlaybackEnabled ? $"/Library/Episode/{episode.Id}" : $"/Library/Anime/{episode.AnimeId}";
    public int PlaybackHistoryLimit => EpisodeProgressService.HistoryLimit;
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    /// <summary>
    /// Resolved ContentMetrics capability for the Anime media type. Controls
    /// preparation percentages on recently discovered episode cards.
    /// </summary>
    public bool ShowContentMetrics { get; private set; }

    public async Task<IActionResult> OnPostClearHistoryAsync(CancellationToken cancellationToken)
    {
        await progress.ClearHistoryAsync(cancellationToken);

        var ui = await new UiTranslationCatalogStore(db).LoadProfileBundleAsync(
            currentAccount.ProfileId,
            cancellationToken);
        TempData["Status"] = ui["home.history.cleared"];
        return RedirectToPage();
    }

    public async Task OnGetAsync(CancellationToken cancellationToken, string? type = null)
    {
        Ui = await new UiTranslationCatalogStore(db).LoadProfileBundleAsync(
            currentAccount.ProfileId,
            cancellationToken);

        ActiveType = DiscoveryRequest.ParseCategory(type);
        var instance = instanceModules is null
            ? InstanceModuleSettings.Default
            : await instanceModules.GetAsync(cancellationToken);
        var animeEnabled = instance.IsEnabled(InstanceModule.Anime);
        PlaybackEnabled = instance.IsEnabled(InstanceModule.Playback);

        // Continue watching and the playback history only exist where something plays; every link of them opens the player.
        ContinueWatching = animeEnabled
            && PlaybackEnabled
            && (ActiveType is DiscoveryCategory.All or DiscoveryCategory.Anime)
                ? await progress.GetContinueWatchingAsync(cancellationToken: cancellationToken)
                : [];
        PlaybackHistory = animeEnabled && PlaybackEnabled
            ? await progress.GetHistoryAsync(cancellationToken)
            : [];

        var continueReading = await new ContinueReadingQuery(db).GetAsync(
            currentAccount.ProfileId,
            cancellationToken: cancellationToken);
        ContinueReading = FilterContinueReading(
            continueReading.Where(item => IsReadingEnabled(instance, item.Kind)).ToArray(),
            ActiveType);

        var anime = await LoadAnimeArtworkAsync(
            ContinueWatching.Select(item => item.AnimeId).Distinct().ToArray(),
            cancellationToken);
        ContinueTiles = BuildContinueTiles(anime);
        var ownSlides = BuildWatchingSlides(anime, ContinueReading).ToArray();
        Hero = ownSlides.Length >= HeroLimit
            ? ownSlides.Take(HeroLimit).ToArray()
            : [.. ownSlides, .. (await LoadWatchlistSlidesAsync(cancellationToken)).Take(HeroLimit - ownSlides.Length)];
        ForYou = await LoadForYouAsync(cancellationToken);

        var animeLearning = await new LearningConfigurationStore(db, instanceModules).ResolveAsync(
            currentAccount.ProfileId,
            new LearningScopeContext(LearningMediaType.Anime),
            cancellationToken);
        ShowContentMetrics = animeEnabled
            && animeLearning.IsEnabled(LearningCapability.ContentMetrics);

        var recentEpisodes = animeEnabled
            ? await (
                from episode in db.Episodes.AsNoTracking()
                join animeRow in db.Anime.AsNoTracking() on episode.AnimeId equals animeRow.Id
                join metadataValue in db.AnimeMetadata.AsNoTracking()
                    on animeRow.Id equals metadataValue.AnimeId into metadataRows
                from metadata in metadataRows.DefaultIfEmpty()
                orderby episode.DiscoveredAt descending
                select new HomeEpisode(
                    episode.Id,
                    animeRow.Id,
                    metadata == null ? animeRow.Title : metadata.PreferredTitle,
                    episode.SeasonNumber,
                    episode.Number,
                    0,
                    0,
                    metadata == null ? null : metadata.CoverImageUrl))
                .Take(10)
                .ToListAsync(cancellationToken)
            : new List<HomeEpisode>();

        // Vocabulary coverage is only computed when the resolved Anime scope
        // shows content metrics; otherwise Home never touches learning tables.
        var coverage = ShowContentMetrics
            ? await LoadCoverageAsync(
                recentEpisodes.Select(x => x.Id).ToArray(),
                cancellationToken)
            : new Dictionary<Guid, (int Total, int Prepared)>();

        RecentEpisodes = recentEpisodes
            .Select(row =>
            {
                coverage.TryGetValue(row.Id, out var totals);
                return row with
                {
                    TotalOccurrences = totals.Total,
                    PreparedOccurrences = totals.Prepared,
                    CoverImageUrl = AnimeArtworkStore.ResolvePosterUrl(
                        row.AnimeId,
                        row.CoverImageUrl)
                };
            })
            .ToArray();
    }

    /// <summary>"S01 · Episode 4 · 32 min left" — the caption of a watching tile.</summary>
    public string WatchingCaption(ContinueWatchingItem item) =>
        RemainingText(item) is { } remaining ? $"{EpisodeLabel(item)} · {remaining}" : EpisodeLabel(item);

    private string EpisodeLabel(ContinueWatchingItem item) => Ui.Format(
        "home.continueWatching.episode",
        ("season", item.SeasonNumber.ToString("00")),
        ("episode", item.EpisodeNumber));

    /// <summary>"22 min left" for a partly watched episode; null when nothing has been watched yet.</summary>
    private string? RemainingText(ContinueWatchingItem item)
    {
        if (item.ResumePositionMs <= 0)
        {
            return null;
        }

        return item.RemainingMs is { } remainingMs
            ? Ui.Format(
                "home.continueWatching.remaining",
                ("minutes", Math.Max(1, (int)Math.Ceiling(remainingMs / 60000d))))
            : Ui["home.continueWatching.resume"];
    }

    private string ReadingPosition(ContinueReadingItem item) =>
        item.PageNumber is int page && item.PageCount is int pages
            ? Ui.Format(
                "home.continueReading.chapterPage",
                ("chapter", item.ChapterLabel),
                ("page", page),
                ("pages", pages))
            : Ui.Format("home.continueReading.chapter", ("chapter", item.ChapterLabel));

    private string ReadingKindLabel(ContinueReadingKind kind) => kind switch
    {
        ContinueReadingKind.Book => Ui["home.continueReading.kind.book"],
        ContinueReadingKind.Manga => Ui["home.continueReading.kind.manga"],
        _ => Ui["home.continueReading.kind.novel"]
    };

    private static string ReadingDetailsUrl(ContinueReadingItem item) => item.Kind switch
    {
        ContinueReadingKind.Book => $"/Books/Library/{item.WorkId}",
        ContinueReadingKind.Manga => $"/Manga/Series/{item.WorkId}",
        _ => $"/Novels/Work/{item.WorkId}"
    };

    /// <summary>Backdrop and hero facts for the series in Continue Watching, read from local metadata only.</summary>
    private async Task<IReadOnlyDictionary<Guid, AnimeHeroFacts>> LoadAnimeArtworkAsync(
        Guid[] animeIds,
        CancellationToken cancellationToken)
    {
        if (animeIds.Length == 0)
        {
            return new Dictionary<Guid, AnimeHeroFacts>();
        }

        var metadata = await db.AnimeMetadata.AsNoTracking()
            .Where(row => animeIds.Contains(row.AnimeId))
            .Select(row => new { row.AnimeId, row.BannerImageUrl, row.Description, row.SeasonYear, row.AverageScore })
            .ToListAsync(cancellationToken);
        var seasons = await db.Episodes.AsNoTracking()
            .Where(episode => animeIds.Contains(episode.AnimeId) && episode.SeasonNumber > 0)
            .GroupBy(episode => episode.AnimeId)
            .Select(group => new { AnimeId = group.Key, Count = group.Select(episode => episode.SeasonNumber).Distinct().Count() })
            .ToDictionaryAsync(row => row.AnimeId, row => row.Count, cancellationToken);
        var byId = metadata.ToDictionary(row => row.AnimeId);

        return animeIds.ToDictionary(
            id => id,
            id =>
            {
                byId.TryGetValue(id, out var row);
                // A wide backdrop is the cached fanart, else the provider banner; never the poster.
                var backdrop = AnimeArtworkStore.ResolveFanartUrl(id, row?.BannerImageUrl);
                return new AnimeHeroFacts(
                    string.IsNullOrWhiteSpace(backdrop) ? null : backdrop,
                    string.IsNullOrWhiteSpace(row?.Description) ? null : row.Description.Trim(),
                    row?.SeasonYear,
                    row?.AverageScore,
                    seasons.GetValueOrDefault(id));
            });
    }

    /// <summary>"2024 · Anime · 2 seasons · ★ 8.7" — only the facts Jularr actually has.</summary>
    private string AnimeMeta(AnimeHeroFacts facts)
    {
        var culture = ReleaseCalendarPresenter.CultureFor(Ui.Locale);
        return string.Join(" · ", new[]
        {
            facts.Year?.ToString(CultureInfo.InvariantCulture),
            Ui["calendar.media.anime"],
            facts.Seasons > 1 ? Ui.Format("home.spotlight.seasons", ("count", facts.Seasons)) : null,
            facts.Score is int score && score > 0 ? $"★ {(score / 10d).ToString("0.0", culture)}" : null
        }.Where(part => !string.IsNullOrEmpty(part)));
    }

    private HomeContinueTile[] BuildContinueTiles(IReadOnlyDictionary<Guid, AnimeHeroFacts> anime)
    {
        var watching = ContinueWatching.Select(item =>
        {
            var backdrop = anime.GetValueOrDefault(item.AnimeId)?.Backdrop;
            return (At: item.UpdatedAt, Tile: new HomeContinueTile(
                item.AnimeTitle,
                WatchingCaption(item),
                $"/Library/Episode/{item.EpisodeId}",
                item.ResumePositionMs > 0 ? item.Percent : null,
                backdrop ?? item.CoverImageUrl,
                backdrop is not null,
                Ui.Format("home.continueWatching.progressAria", ("percent", item.Percent))));
        });
        var reading = ContinueReading.Select(item => (At: item.LastReadAt, Tile: new HomeContinueTile(
            item.Title,
            ReadingPosition(item),
            item.ResumeUrl,
            item.ProgressPercent,
            item.CoverImageUrl,
            false,
            Ui.Format("home.continueReading.progressAria", ("percent", item.ProgressPercent)))));

        return watching.Concat(reading)
            .OrderByDescending(row => row.At)
            .Select(row => row.Tile)
            .ToArray();
    }

    /// <summary>In-progress episodes, then in-progress reading, then up-next episodes (each newest first).</summary>
    private IEnumerable<HomeHeroSlide> BuildWatchingSlides(
        IReadOnlyDictionary<Guid, AnimeHeroFacts> anime,
        IReadOnlyList<ContinueReadingItem> reading)
    {
        HomeHeroSlide Watching(ContinueWatchingItem item, bool upNext)
        {
            var facts = anime.GetValueOrDefault(item.AnimeId) ?? AnimeHeroFacts.None;
            var episode = string.IsNullOrWhiteSpace(item.EpisodeTitle)
                ? EpisodeLabel(item)
                : $"{EpisodeLabel(item)} – {item.EpisodeTitle.Trim()}";
            return new HomeHeroSlide(
                upNext ? Ui["home.continueWatching.upNext"] : Ui["home.continueWatching.eyebrow"],
                item.AnimeTitle,
                AnimeMeta(facts),
                episode,
                facts.Description,
                upNext || item.ResumePositionMs <= 0 ? null : item.Percent,
                upNext ? null : RemainingText(item),
                facts.Backdrop ?? item.CoverImageUrl,
                facts.Backdrop is not null,
                $"/Library/Episode/{item.EpisodeId}",
                upNext ? Ui["home.spotlight.play"] : Ui["home.spotlight.continue"],
                true,
                $"/Library/Anime/{item.AnimeId}",
                Ui["home.spotlight.details"],
                HomeHeroSecondary.Details);
        }

        var recent = ContinueWatching.OrderByDescending(item => item.UpdatedAt).DistinctBy(item => item.AnimeId).ToArray();
        var resume = recent.Where(item => item.Kind == ContinueWatchingKind.Resume).Take(HeroPerSourceLimit).Select(item => Watching(item, false));
        var readingSlides = reading.Take(HeroPerSourceLimit).Select(item => new HomeHeroSlide(
            Ui["home.continueReading.eyebrow"],
            item.Title,
            ReadingKindLabel(item.Kind),
            ReadingPosition(item),
            null,
            item.ProgressPercent,
            Ui.Format("home.continueReading.progressAria", ("percent", item.ProgressPercent)),
            item.CoverImageUrl,
            false,
            item.ResumeUrl,
            Ui["home.spotlight.read"],
            false,
            ReadingDetailsUrl(item),
            Ui["home.spotlight.details"],
            HomeHeroSecondary.Details));
        var upNext = recent.Where(item => item.Kind == ContinueWatchingKind.UpNext).Take(HeroPerSourceLimit).Select(item => Watching(item, true));

        return resume.Concat(readingSlides).Concat(upNext);
    }

    /// <summary>
    /// The newest release of each followed work (released in the last two weeks, newest first),
    /// then the nearest upcoming ones. Reads only the local release cache — no provider calls.
    /// </summary>
    private async Task<IReadOnlyList<HomeHeroSlide>> LoadWatchlistSlidesAsync(CancellationToken cancellationToken)
    {
        ReleaseMediaType? mediaType = ActiveType switch
        {
            DiscoveryCategory.Anime => ReleaseMediaType.Anime,
            DiscoveryCategory.Manga => ReleaseMediaType.Manga,
            DiscoveryCategory.LightNovel => ReleaseMediaType.LightNovel,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(currentAccount.ProfileId) || ActiveType == DiscoveryCategory.Book)
        {
            return [];
        }

        var zone = CalendarTimeZone.Resolve(HttpContext?.Request.Cookies[CalendarTimeZone.CookieName]);
        var presenter = new ReleaseCalendarPresenter(Ui, zone, DateTimeOffset.UtcNow, PlaybackEnabled);
        var source = new WatchlistReleaseEventSource(
            new ReleaseCalendarCacheStore(db),
            new WatchlistStore(db),
            new WatchlistLibraryResolver(db));
        var events = await source.GetEventsAsync(
            new ReleaseEventQuery(
                presenter.Today.AddDays(-HeroRecentDays),
                presenter.Today.AddDays(HeroUpcomingDays),
                zone,
                presenter.Now,
                MediaType: mediaType,
                ProfileId: currentAccount.ProfileId),
            cancellationToken);

        var instance = instanceModules is null
            ? InstanceModuleSettings.Default
            : await instanceModules.GetAsync(cancellationToken);

        return events
            .Where(release => instance.IsEnabled(ReleaseInstanceModules.For(release.MediaType)))
            .Select(release => (Release: release, Day: release.Date.Period(zone)?.Start))
            .Where(row => row.Day is not null)
            .OrderBy(row => row.Day <= presenter.Today ? 0 : 1)
            .ThenBy(row => row.Day <= presenter.Today
                ? presenter.Today.DayNumber - row.Day!.Value.DayNumber
                : row.Day!.Value.DayNumber - presenter.Today.DayNumber)
            .DistinctBy(row => row.Release.MediaId)
            .Take(HeroPerSourceLimit)
            .Select(row => new HomeHeroSlide(
                row.Day <= presenter.Today ? Ui["home.spotlight.watchlist"] : Ui["home.spotlight.watchlistUpcoming"],
                row.Release.Title,
                presenter.MediaLabel(row.Release.MediaType),
                string.Join(
                    " · ",
                    new[] { presenter.UnitLabel(row.Release), presenter.DateLabel(row.Release.Date) }
                        .Where(text => !string.IsNullOrEmpty(text))),
                null,
                null,
                null,
                row.Release.CoverImageUrl,
                false,
                presenter.Href(row.Release) ?? "/Watchlist",
                Ui["home.spotlight.details"],
                false,
                "/Calendar",
                Ui["nav.calendar"],
                HomeHeroSecondary.Calendar))
            .ToArray();
    }

    /// <summary>
    /// The local "For you" posters: every recommendation shelf's works in the engine's order, one
    /// poster per canonical work. The engine composes local data only (#428), so this stays local-first.
    /// </summary>
    private async Task<IReadOnlyList<HomePosterItem>> LoadForYouAsync(CancellationToken cancellationToken)
    {
        if (recommendations is null || string.IsNullOrWhiteSpace(currentAccount.ProfileId))
        {
            return [];
        }

        var result = await recommendations.GetForProfileAsync(
            currentAccount.User,
            currentAccount.ProfileId,
            cancellationToken);

        return result.Shelves
            .SelectMany(shelf => shelf.Items)
            .DistinctBy(item => item.Candidate.CatalogId ?? item.Candidate.Id)
            .Take(ForYouLimit)
            .Select(item => new HomePosterItem(item.Candidate.Title, item.Candidate.CoverImageUrl, item.Candidate.Href))
            .ToArray();
    }

    /// <summary>Keeps only the reading items matching the active Home filter; "All" and "Anime" keep everything (Anime has no reading row of its own).</summary>
    private static bool IsReadingEnabled(
        InstanceModuleSettings instance,
        ContinueReadingKind kind) =>
        kind switch
        {
            ContinueReadingKind.Book => instance.IsEnabled(InstanceModule.Book),
            ContinueReadingKind.Manga => instance.IsEnabled(InstanceModule.Manga),
            _ => instance.IsEnabled(InstanceModule.Novel)
        };

    private static IReadOnlyList<ContinueReadingItem> FilterContinueReading(
        IReadOnlyList<ContinueReadingItem> items,
        DiscoveryCategory activeType) =>
        activeType switch
        {
            DiscoveryCategory.Manga => items.Where(x => x.Kind == ContinueReadingKind.Manga).ToArray(),
            DiscoveryCategory.LightNovel => items.Where(x => x.Kind == ContinueReadingKind.Novel).ToArray(),
            DiscoveryCategory.Book => items.Where(x => x.Kind == ContinueReadingKind.Book).ToArray(),
            DiscoveryCategory.Anime => [],
            _ => items
        };

    private async Task<Dictionary<Guid, (int Total, int Prepared)>> LoadCoverageAsync(
        Guid[] episodeIds,
        CancellationToken cancellationToken)
    {
        if (episodeIds.Length == 0)
        {
            return [];
        }

        var totals = await db.EpisodeTerms
            .AsNoTracking()
            .Where(x => episodeIds.Contains(x.EpisodeId))
            .GroupBy(x => x.EpisodeId)
            .Select(group => new
            {
                EpisodeId = group.Key,
                Total = group.Sum(x => x.Occurrences)
            })
            .ToDictionaryAsync(x => x.EpisodeId, x => x.Total, cancellationToken);

        var prepared = await (
            from episodeTerm in db.EpisodeTerms.AsNoTracking()
            join state in LearningQueries.TermStates(db, currentAccount.ProfileId)
                    .Where(x =>
                        x.State == UserTermState.Known
                        || x.State == UserTermState.Learning)
                on episodeTerm.TermId equals state.TermId
            where episodeIds.Contains(episodeTerm.EpisodeId)
            group episodeTerm by episodeTerm.EpisodeId
            into episodeGroup
            select new
            {
                EpisodeId = episodeGroup.Key,
                Prepared = episodeGroup.Sum(x => x.Occurrences)
            })
            .ToDictionaryAsync(x => x.EpisodeId, x => x.Prepared, cancellationToken);

        return totals.ToDictionary(
            x => x.Key,
            x => (x.Value, prepared.GetValueOrDefault(x.Key)));
    }

    private sealed record AnimeHeroFacts(string? Backdrop, string? Description, int? Year, int? Score, int Seasons)
    {
        public static AnimeHeroFacts None { get; } = new(null, null, null, null, 0);
    }

    /// <summary>The small square secondary action of a hero slide.</summary>
    public enum HomeHeroSecondary
    {
        Details,
        Calendar
    }

    /// <summary>
    /// One hero slide; every text is already localized. <see cref="ImageIsBackdrop"/> is true for a
    /// real wide backdrop (full-bleed); otherwise <see cref="ImageUrl"/> is a poster/cover that is
    /// shown sharp on the left over a background blurred from the same image.
    /// </summary>
    public sealed record HomeHeroSlide(
        string Label,
        string Title,
        string? Meta,
        string? Subtitle,
        string? Description,
        int? ProgressPercent,
        string? ProgressText,
        string? ImageUrl,
        bool ImageIsBackdrop,
        string PrimaryHref,
        string PrimaryLabel,
        bool PrimaryIsPlay,
        string SecondaryHref,
        string SecondaryLabel,
        HomeHeroSecondary SecondaryIcon);

    /// <summary>One landscape tile of the Continue row.</summary>
    public sealed record HomeContinueTile(
        string Title,
        string Caption,
        string Href,
        int? ProgressPercent,
        string? ImageUrl,
        bool ImageIsBackdrop,
        string ProgressAria);

    /// <summary>One text-free poster of the "For you" row; the title is its accessible name.</summary>
    public sealed record HomePosterItem(string Title, string? ImageUrl, string Href);

    /// <summary>One Home media-type filter: its Discover category, the <c>?type=</c> query value, and its label key.</summary>
    public sealed record HomeTypeChip(DiscoveryCategory Category, string QueryValue, string LabelKey);

    /// <summary>Home's own link for a filter; the default ("all") keeps the plain root URL.</summary>
    public static string ChipHref(HomeTypeChip chip) =>
        chip.QueryValue == "all" ? "/" : $"/?type={chip.QueryValue}";

    public sealed record HomeEpisode(
        Guid Id,
        Guid AnimeId,
        string AnimeTitle,
        int SeasonNumber,
        int Number,
        int TotalOccurrences,
        int PreparedOccurrences,
        string? CoverImageUrl)
    {
        public int PreparationPercent => TotalOccurrences == 0
            ? 0
            : (int)Math.Floor((double)PreparedOccurrences / TotalOccurrences * 100);
    }
}
