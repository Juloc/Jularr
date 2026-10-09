using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Calendar;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Franchises;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Reading;
using Jularr.Web.Features.Recommendations;
using Jularr.Web.Features.Shell;
using Jularr.Web.Features.Watchlist;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages;

/// <summary>
/// Home (docs/mockups/home/SPEC.md): a hero carousel of the profile's own media, one merged
/// Continue row across Anime, Series, Movies and reading, the local "For you" row and the titles
/// most recently added to the library. Every read is local — the database, the local release cache
/// and the local recommendation engine; no provider calls.
/// </summary>
public sealed partial class IndexModel(
    IDiscoveryFeed coordinator,
    DiscoveryShelfService shelves,
    TmdbDiscoveryProvider tmdb,
    AppDbContext db,
    NovelImportService novels,
    NovelMetadataService novelMetadata,
    CurrentAccountContext account,
    OperationRunner operations,
    AcquisitionRequestService requests,
    AcquisitionAccessStore requestStore,
    VideoRequestScopeResolver scopes,
    WatchlistStore watchlist,
    FranchiseService franchiseService,
    MediaRecommendationService recommendations,
    IInstanceModuleService instanceModules,
    VideoProgressService videoProgress,
    ILogger<IndexModel> logger,
    IAppShellService? shell = null) : PageModel
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

    /// <summary>Most titles in the "Recently discovered" row.</summary>
    public const int RecentLimit = 12;

    /// <summary>The newest additions to the library across Anime, Series and Movies, one card per title.</summary>
    public IReadOnlyList<HomeRecentTitle> RecentTitles { get; private set; } = [];

    /// <summary>Resumable and up-next video of the profile across every visible video type, newest first.</summary>
    public IReadOnlyList<HomeContinueVideo> ContinueWatching { get; private set; } = [];

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

    /// <summary>Discover link for the Continue Watching heading, narrowed to the active video filter.</summary>
    public string ContinueWatchingDiscoverUrl => ActiveType switch
    {
        DiscoveryCategory.Anime => "/?category=anime&mode=my-list",
        DiscoveryCategory.Movie => "/?category=movie&mode=my-list",
        DiscoveryCategory.Series => "/?category=series&mode=my-list",
        _ => "/?mode=my-list"
    };

    /// <summary>
    /// Discover link for the Continue Reading heading. Matches the active
    /// filter so a filtered row always points at the same category in
    /// Discover; unset (all media) when no specific filter is active.
    /// </summary>
    public string ContinueReadingDiscoverUrl => ActiveType switch
    {
        DiscoveryCategory.Manga => "/?category=manga&mode=my-list",
        DiscoveryCategory.LightNovel => "/?category=light-novel&mode=my-list",
        DiscoveryCategory.Book => "/?category=book&mode=my-list",
        _ => "/?mode=my-list"
    };

    /// <summary>The merged row keeps the Continue Watching heading and link while anything is being watched.</summary>
    public string ContinueHeading => ContinueWatching.Count > 0
        ? Ui["home.continueWatching"]
        : Ui["home.continueReading"];

    public string ContinueDiscoverUrl => ContinueWatching.Count > 0
        ? ContinueWatchingDiscoverUrl
        : ContinueReadingDiscoverUrl;

    public IReadOnlyList<HomePlaybackEntry> PlaybackHistory { get; private set; } = [];

    /// <summary>False on a manager-only instance: nothing on the page links into the player.</summary>
    public bool PlaybackEnabled { get; private set; } = true;

    public int PlaybackHistoryLimit => VideoProgressService.HistoryLimit;
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    /// <summary>True when Home has nothing of the profile's or of the library to show: the new-user empty state.</summary>
    public bool IsEmpty => Hero.Count == 0 && ContinueTiles.Count == 0 && ForYou.Count == 0 && RecentTitles.Count == 0;

    /// <summary>Whether the profile may add library folders, so the empty state may link to them.</summary>
    public bool CanManageStorage => account.Can(JularrPolicies.AdminSystem);

    public async Task<IActionResult> OnPostClearHistoryAsync(CancellationToken cancellationToken)
    {
        await videoProgress.ClearHistoryAsync(account.ProfileId, cancellationToken);

        var ui = await new UiTranslationCatalogStore(db).LoadProfileBundleAsync(
            account.ProfileId,
            cancellationToken);
        TempData["Status"] = ui["home.history.cleared"];
        return RedirectToPage();
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadDiscoverAsync(cancellationToken);

        // Typing or a filter turns the surface into results only, so nothing of Home is read for it.
        if (Query.IsLanding)
        {
            await LoadHomeAsync(Query.Category, cancellationToken);
        }
        else
        {
            ActiveType = Query.Category;
        }

        InitialBody = Query.IsSearch ? null : await TryBuildInitialBodyAsync(cancellationToken);
    }

    /// <summary>
    /// The titles of the page when the sources already have an answer for it, fresh, stale or from the local snapshot: the first response is then useful by
    /// itself, with no skeleton and no second request, and the browser only asks for what has been renewed since. Null when no source has anything to show
    /// yet (the very first load, a type that was never browsed), where the page keeps its placeholder and fetches the body. It never waits for a provider.
    /// </summary>
    public DiscoverBodyView? InitialBody { get; private set; }

    private async Task<DiscoverBodyView?> TryBuildInitialBodyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var audience = await LoadAudienceAsync(cancellationToken);
            var wait = DiscoveryWait.None;
            var body = Query.IsLanding ? await BuildLandingAsync(audience, wait, cancellationToken) : await BuildResultsAsync(audience, wait, cancellationToken);
            return body.State == DiscoverBodyState.Sections && body.Sections.Any(section => section.State == DiscoverySectionState.Ready) ? body : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The body handler builds it again after first paint, as it always did.
            logger.LogWarning(exception, "The discovery titles could not be prepared for the first response.");
            return null;
        }
    }

    /// <summary>The hero, the Continue cards and the playback history of the landing, for the media type the bar has selected.</summary>
    public async Task LoadHomeAsync(DiscoveryCategory activeType, CancellationToken cancellationToken)
    {
        ActiveType = activeType;
        var instance = await instanceModules.GetAsync(cancellationToken);
        PlaybackEnabled = instance.IsEnabled(InstanceModule.Playback);
        var access = await shell!.GetMediaAccessAsync(account.User, cancellationToken);
        var videoTypes = LibraryBrowse.VideoMediaTypes.Where(access.IsVisible).ToArray();
        var videoQuery = new HomeVideoQuery(db, videoProgress);

        // Resuming and the playback history only exist where something plays; every link of them opens the player.
        if (PlaybackEnabled)
        {
            var continueTypes = videoTypes.Where(mediaType => MatchesFilter(ActiveType, mediaType)).ToArray();
            ContinueWatching = await videoQuery.GetContinueAsync(account.ProfileId, continueTypes, VideoProgressService.ContinueWatchingLimit, cancellationToken);
            PlaybackHistory = await videoQuery.GetHistoryAsync(account.ProfileId, videoTypes, cancellationToken);
        }

        var continueReading = await new ContinueReadingQuery(db).GetAsync(account.ProfileId, cancellationToken: cancellationToken);
        ContinueReading = FilterContinueReading(continueReading.Where(item => IsReadingEnabled(instance, item.Kind)).ToArray(), ActiveType);

        ContinueTiles = BuildContinueTiles();
        var watchlistSlides = await LoadWatchlistSlidesAsync(cancellationToken);
        ForYou = await LoadForYouAsync(cancellationToken);

        var recent = await videoQuery.GetRecentlyAddedAsync(account.ProfileId, videoTypes, RecentLimit, cancellationToken);
        RecentTitles = [.. recent.Select(item => new HomeRecentTitle(item.Title, RecentSubtitle(item)))];

        // The Hero is a pool of useful candidates of several classes, not the first Continue item: Continue or Resume leads, then what is newly
        // available in the library, then a personalized recommendation, then what the followed works released. The classes alternate, so a Continue
        // item never monopolizes the Hero while it still comes first.
        Hero = RotateClasses(
            [
                [.. BuildWatchingSlides(ContinueReading)],
                [.. RecentTitles.Where(title => MatchesFilter(ActiveType, title.Title.MediaType)).Take(HeroPerSourceLimit).Select(NewlyAvailableSlide)],
                [.. (ActiveType == DiscoveryCategory.All ? ForYou : []).Take(HeroPerSourceLimit).Select(RecommendedSlide)],
                [.. watchlistSlides]
            ],
            HeroLimit);
    }

    /// <summary>Takes one slide of every class in priority order, then the next of each, until the Hero is full; a class that has no more simply drops out.</summary>
    public static IReadOnlyList<HomeHeroSlide> RotateClasses(IReadOnlyList<IReadOnlyList<HomeHeroSlide>> classes, int limit)
    {
        var result = new List<HomeHeroSlide>(limit);
        for (var round = 0; result.Count < limit; round++)
        {
            var added = false;
            foreach (var slides in classes)
            {
                if (round >= slides.Count || result.Count >= limit)
                {
                    continue;
                }

                added = true;

                // A title that already leads the Hero in another class (a series being continued that is also newly added) is shown once.
                if (!result.Any(existing => string.Equals(existing.Title, slides[round].Title, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(slides[round]);
                }
            }

            if (!added)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>A title that is newly available in the library: a Movie plays directly, a series opens its Detail where the next episode is the primary action.</summary>
    private HomeHeroSlide NewlyAvailableSlide(HomeRecentTitle recent)
    {
        var title = recent.Title;
        var plays = PlaybackEnabled && title.MediaType == WorkMediaType.Movie;
        var image = title.BackdropUrl ?? title.PosterUrl;
        return new HomeHeroSlide(
            Ui["home.hero.newlyAvailable"],
            title.Title,
            VideoMeta(title),
            recent.Subtitle,
            title.Description,
            null,
            null,
            image,
            title.BackdropUrl is not null,
            plays ? $"/Library/Watch/{title.WorkId}" : title.DetailHref,
            plays ? Ui["home.spotlight.play"] : Ui["home.spotlight.open"],
            plays,
            title.DetailHref,
            Ui["home.spotlight.details"],
            HomeHeroSecondary.Details);
    }

    /// <summary>A personalized recommendation: the title page is the primary action, because nothing of it is playable yet.</summary>
    private HomeHeroSlide RecommendedSlide(HomePosterItem item) =>
        new(
            Ui["home.hero.recommended"],
            item.Title,
            null,
            null,
            null,
            null,
            null,
            item.ImageUrl,
            false,
            item.Href,
            Ui["home.spotlight.open"],
            false,
            item.Href,
            Ui["home.spotlight.details"],
            HomeHeroSecondary.Details);

    /// <summary>"S01 · Episode 4 · 32 min left" for an episode, "32 min left" for a Movie — the caption of a watching tile.</summary>
    public string WatchingCaption(HomeContinueVideo item) => string.Join(" · ", new[] { EpisodeLabel(item.SeasonNumber, item.EpisodeNumber), RemainingText(item) }.Where(part => part is not null));

    /// <summary>"S01 · Episode 4" for an episode; null for a Movie, which has no episode.</summary>
    public string? EpisodeLabel(int? seasonNumber, int? episodeNumber) => seasonNumber is { } season && episodeNumber is { } episode
        ? Ui.Format("home.continueWatching.episode", ("season", season.ToString("00")), ("episode", episode))
        : null;

    /// <summary>Whether a video type belongs to the active Home filter; the reading filters show no video.</summary>
    private static bool MatchesFilter(DiscoveryCategory active, WorkMediaType mediaType) => active switch
    {
        DiscoveryCategory.All => true,
        DiscoveryCategory.Anime => mediaType == WorkMediaType.Anime,
        DiscoveryCategory.Movie => mediaType == WorkMediaType.Movie,
        DiscoveryCategory.Series => mediaType == WorkMediaType.Series,
        _ => false
    };

    /// <summary>The episode of an added episodic title; the year of a Movie, else its media type.</summary>
    private string RecentSubtitle(HomeRecentVideo item) => EpisodeLabel(item.SeasonNumber, item.EpisodeNumber)
        ?? item.Title.Year?.ToString(CultureInfo.InvariantCulture)
        ?? MediaLabel(item.Title.MediaType);

    private string MediaLabel(WorkMediaType mediaType) => mediaType switch
    {
        WorkMediaType.Movie => Ui["calendar.media.movie"],
        WorkMediaType.Series => Ui["calendar.media.tv"],
        _ => Ui["calendar.media.anime"]
    };

    /// <summary>"22 min left" for a partly watched title; null when nothing has been watched yet.</summary>
    private string? RemainingText(HomeContinueVideo item)
    {
        if (item.ResumePositionMs <= 0)
        {
            return null;
        }

        return item.RemainingMs is { } remainingMs
            ? Ui.Format("home.continueWatching.remaining", ("minutes", Math.Max(1, (int)Math.Ceiling(remainingMs / 60000d))))
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

    /// <summary>"2024 · Anime · 2 seasons · ★ 8.7" — only the facts Jularr actually has.</summary>
    private string VideoMeta(HomeVideoTitle title)
    {
        var culture = ReleaseCalendarPresenter.CultureFor(Ui.Locale);
        return string.Join(" · ", new[]
        {
            title.Year?.ToString(CultureInfo.InvariantCulture),
            MediaLabel(title.MediaType),
            title.SeasonCount > 1 ? Ui.Format("home.spotlight.seasons", ("count", title.SeasonCount)) : null,
            title.Score is int score && score > 0 ? $"★ {(score / 10d).ToString("0.0", culture)}" : null
        }.Where(part => !string.IsNullOrEmpty(part)));
    }

    private HomeContinueTile[] BuildContinueTiles()
    {
        var watching = ContinueWatching.Select(item =>
        {
            // The tile is a poster card: the backdrop is only the stand-in of a title that has no poster.
            var backdrop = item.Title.BackdropUrl;
            return (At: item.UpdatedAt, Tile: new HomeContinueTile(
                item.Title.Title,
                WatchingCaption(item),
                item.PlayHref,
                item.ResumePositionMs > 0 ? item.Percent : null,
                item.PosterUrl ?? backdrop,
                item.PosterUrl is null && backdrop is not null,
                Ui.Format("home.continueWatching.progressAria", ("percent", item.Percent)),
                "play"));
        });
        var reading = ContinueReading.Select(item => (At: item.LastReadAt, Tile: new HomeContinueTile(
            item.Title,
            ReadingPosition(item),
            item.ResumeUrl,
            item.ProgressPercent,
            item.CoverImageUrl,
            false,
            Ui.Format("home.continueReading.progressAria", ("percent", item.ProgressPercent)),
            "read")));

        return watching.Concat(reading)
            .OrderByDescending(row => row.At)
            .Select(row => row.Tile)
            .ToArray();
    }

    /// <summary>In-progress video, then in-progress reading, then up-next episodes (each newest first).</summary>
    private IEnumerable<HomeHeroSlide> BuildWatchingSlides(IReadOnlyList<ContinueReadingItem> reading)
    {
        HomeHeroSlide Watching(HomeContinueVideo item, bool upNext)
        {
            var episodeLabel = EpisodeLabel(item.SeasonNumber, item.EpisodeNumber);
            var subtitle = string.IsNullOrWhiteSpace(item.EpisodeTitle) ? episodeLabel : $"{episodeLabel} – {item.EpisodeTitle.Trim()}";
            var backdrop = item.Title.BackdropUrl;
            return new HomeHeroSlide(
                upNext ? Ui["home.continueWatching.upNext"] : Ui["home.continueWatching.eyebrow"],
                item.Title.Title,
                VideoMeta(item.Title),
                subtitle,
                item.Title.Description,
                upNext || item.ResumePositionMs <= 0 ? null : item.Percent,
                upNext ? null : RemainingText(item),
                backdrop ?? item.PosterUrl,
                backdrop is not null,
                item.PlayHref,
                upNext ? Ui["home.spotlight.play"] : Ui["home.spotlight.continue"],
                true,
                item.Title.DetailHref,
                Ui["home.spotlight.details"],
                HomeHeroSecondary.Details);
        }

        var recent = ContinueWatching.OrderByDescending(item => item.UpdatedAt).DistinctBy(item => item.Title.WorkId).ToArray();
        var resume = recent.Where(item => item.Kind == VideoContinueWatchingKind.Resume).Take(HeroPerSourceLimit).Select(item => Watching(item, false));
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
        var upNext = recent.Where(item => item.Kind == VideoContinueWatchingKind.UpNext).Take(HeroPerSourceLimit).Select(item => Watching(item, true));

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
        if (string.IsNullOrWhiteSpace(account.ProfileId) || ActiveType is DiscoveryCategory.Book or DiscoveryCategory.Movie or DiscoveryCategory.Series)
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
                ProfileId: account.ProfileId),
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
        if (recommendations is null || string.IsNullOrWhiteSpace(account.ProfileId))
        {
            return [];
        }

        var result = await recommendations.GetForProfileAsync(
            account.User,
            account.ProfileId,
            cancellationToken);

        return result.Shelves
            .SelectMany(shelf => shelf.Items)
            .DistinctBy(item => item.Candidate.CatalogId ?? item.Candidate.Id)
            .Take(ForYouLimit)
            .Select(item => new HomePosterItem(item.Candidate.Title, item.Candidate.CoverImageUrl, item.Candidate.Href))
            .ToArray();
    }

    /// <summary>Whether the instance has the module of a reading medium enabled.</summary>
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
            DiscoveryCategory.Anime or DiscoveryCategory.Movie or DiscoveryCategory.Series => [],
            _ => items
        };

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
        string ProgressAria,
        string ActionIcon);

    /// <summary>One text-free poster of the "For you" row; the title is its accessible name.</summary>
    public sealed record HomePosterItem(string Title, string? ImageUrl, string Href);

    /// <summary>One Home media-type filter: its Discover category, the <c>?type=</c> query value, and its label key.</summary>
    public sealed record HomeTypeChip(DiscoveryCategory Category, string QueryValue, string LabelKey);

    /// <summary>Home's own link for a filter; the default ("all") keeps the plain root URL.</summary>
    public static string ChipHref(HomeTypeChip chip) =>
        chip.QueryValue == "all" ? "/" : $"/?type={chip.QueryValue}";

    /// <summary>A title that is newly available in the library; the Hero offers it as one of its candidates.</summary>
    public sealed record HomeRecentTitle(HomeVideoTitle Title, string Subtitle);
}
