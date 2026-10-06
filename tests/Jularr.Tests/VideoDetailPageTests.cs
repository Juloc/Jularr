using System.Net;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Progress;

namespace Jularr.Tests;

/// <summary>
/// The canonical Movie and Series detail pages (docs/mockups/movie-detail, anime-series-detail) rendered end to end over
/// Work, WorkEpisode, MediaAsset/StoredFile/MediaTrack, the profile's MediaProgress and the shared request lifecycle.
/// </summary>
[TestClass]
public sealed class VideoDetailPageTests
{
    private const string Profile = VideoDetailPageTestHost.Profile;

    private static async Task<Work> AddTitleAsync(VideoDetailPageTestHost host, WorkMediaType type, string title, int year, string? tmdbId = null)
    {
        var work = await new LibraryCanonicalSeed(host.Db).AddWorkAsync(type, title, year);
        if (tmdbId is not null)
        {
            host.Db.Add(new WorkExternalIdentity { WorkId = work.Id, MediaType = type, Provider = "tmdb", ExternalId = tmdbId, IsPrimary = true });
            await host.Db.SaveChangesAsync();
        }

        return work;
    }

    private static Task OpenRequestAsync(VideoDetailPageTestHost host, MediaAcquisitionKind kind, string tmdbId, string title, AcquisitionRequestStatus status, string? payload = null) =>
        new AcquisitionAccessStore(host.Db).CreateAsync(new AcquisitionRequestDraft(kind, "tmdb", tmdbId, title, null, null, payload), "someone-else", status, "owner", CancellationToken.None);

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(from >= 0, $"'{start}' not found.");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.IsTrue(to > from, $"'{end}' not found after '{start}'.");
        return text[from..to];
    }

    // ---- Movie ---------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AnAvailableMovieShowsPlayItsLanguagesAndQualityAndNoRequest()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", 2024, "603");
        await new LibraryCanonicalSeed(host.Db).AddVideoAsync(movie, null, audio: ["ger", "jpn"], subtitles: ["eng"], durationSeconds: 6720, width: 3840, height: 2160, dynamicRange: "HDR10");
        host.Db.Add(new ProfilePlaybackPreferences { ProfileId = Profile, PreferredAudioLanguage = "de" });
        await host.Db.SaveChangesAsync();

        var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}");

        var hero = Between(html, "<section class=\"ad-hero", "</section>");
        StringAssert.Contains(hero, "Moon Empire");
        StringAssert.Contains(hero, $"href=\"/Library/Watch/{movie.Id}\"");
        StringAssert.Contains(hero, ">Play<");
        StringAssert.Contains(hero, "1h 52m");
        StringAssert.Contains(hero, "ad-hero-strip");
        Assert.IsFalse(hero.Contains("data-dc-card-request", StringComparison.Ordinal), "Something playable offers Play, not Request.");
        Assert.IsFalse(html.Contains("data-dc-rq", StringComparison.Ordinal), "No Request dialog is needed for an available title.");

        var versions = Between(html, "<section class=\"vd-card\"", "</section>");
        StringAssert.Contains(versions, "Versions & languages");
        StringAssert.Contains(versions, "ad-lang is-preferred\" title=\"Preferred language\">DE<");
        StringAssert.Contains(versions, ">JA<");
        StringAssert.Contains(versions, ">EN<");
        StringAssert.Contains(versions, "Available in your preferred language");
        StringAssert.Contains(versions, "<li>4K</li>");
        StringAssert.Contains(versions, "<li>HDR</li>");
        StringAssert.Contains(versions, "View all");
        Assert.IsFalse(html.Contains(".mkv", StringComparison.Ordinal), "No file name or path reaches the page.");
        foreach (var unbuilt in new[] { "Trailer", "Cast &", "Bonus", "More Like This", "role=\"tablist\"" })
        {
            Assert.IsFalse(html.Contains(unbuilt, StringComparison.Ordinal), $"'{unbuilt}' has no persisted data and must not render as an empty block.");
        }
    }

    [TestMethod]
    public async Task AMovieOnlyAvailableInOtherLanguagesSaysSo()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", 2024);
        await new LibraryCanonicalSeed(host.Db).AddVideoAsync(movie, null, audio: ["jpn"], subtitles: ["eng"]);
        host.Db.Add(new ProfilePlaybackPreferences { ProfileId = Profile, PreferredAudioLanguage = "de", PreferredSubtitleLanguage = "de" });
        await host.Db.SaveChangesAsync();

        var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}");

        StringAssert.Contains(html, "vd-language-state is-other");
        StringAssert.Contains(html, "Only available in other languages");
    }

    [TestMethod]
    public async Task AMovieResumesFromItsSavedPositionAndOffersWatchAgainOnceFinished()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var seed = new LibraryCanonicalSeed(host.Db);
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", 2024);
        await seed.AddVideoAsync(movie, null, durationSeconds: 6720);

        StringAssert.Contains(await host.GetOkAsync($"/Library/Movie/{movie.Id}"), ">Play<");

        await seed.SetProgressAsync(Profile, movie, null, 45 * 60 * 1000, 6720 * 1000, completed: false, DateTime.UtcNow);
        StringAssert.Contains(await host.GetOkAsync($"/Library/Movie/{movie.Id}"), "Continue watching");

        await seed.SetProgressAsync(Profile, movie, null, 0, 6720 * 1000, completed: true, DateTime.UtcNow);
        StringAssert.Contains(await host.GetOkAsync($"/Library/Movie/{movie.Id}"), "Watch again");
    }

    [TestMethod]
    public async Task AnUnavailableRequestableMovieOffersTheSharedRequestDialogWithItsProviderIdentity()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", 2024, "603");

        var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}");

        var hero = Between(html, "<section class=\"ad-hero", "</section>");
        StringAssert.Contains(hero, "data-dc-card-request");
        StringAssert.Contains(hero, ">Request<");
        Assert.IsFalse(html.Contains(">Add<", StringComparison.Ordinal), "The only acquisition action is Request.");
        StringAssert.Contains(html, "data-request-host");
        StringAssert.Contains(html, "data-dc-category=\"movie\"");
        StringAssert.Contains(html, "data-dc-provider=\"tmdb\"");
        StringAssert.Contains(html, "data-dc-external-id=\"603\"");
        StringAssert.Contains(html, "data-dc-meta=\"2024 · Movie\"");
        StringAssert.Contains(html, "data-resolve-url=\"/Discover?handler=Resolve\"");
        StringAssert.Contains(html, "data-request-url=\"/Discover?handler=Request\"");
        StringAssert.Contains(html, "name=\"__RequestVerificationToken\"");
        StringAssert.Contains(html, "<dialog class=\"dc-rq\"");
        Assert.IsFalse(html.Contains("Versions & languages", StringComparison.Ordinal), "Nothing is playable, so there are no versions.");
    }

    [TestMethod]
    public async Task AMovieWithAnOpenRequestShowsTheLiveRequestStateInsteadOfARequestButton()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", 2024, "603");
        await OpenRequestAsync(host, MediaAcquisitionKind.Movie, "603", "Moon Empire", AcquisitionRequestStatus.Downloading);

        var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}");

        var hero = Between(html, "<section class=\"ad-hero", "</section>");
        StringAssert.Contains(hero, "href=\"/Requests\"");
        StringAssert.Contains(hero, "Getting movie");
        Assert.IsFalse(html.Contains("data-dc-card-request", StringComparison.Ordinal), "A title is requested once at a time.");
        Assert.IsFalse(html.Contains("data-dc-rq", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AnUnavailableMovieThatCannotBeRequestedSaysItIsNotAvailable()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Browse);
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", 2024, "603");
        var noIdentity = await AddTitleAsync(host, WorkMediaType.Movie, "Unmatched", 2020);

        foreach (var id in new[] { movie.Id, noIdentity.Id })
        {
            var html = await host.GetOkAsync($"/Library/Movie/{id}");
            StringAssert.Contains(Between(html, "<section class=\"ad-hero", "</section>"), "Not available");
            Assert.IsFalse(html.Contains("data-dc-card-request", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task RelatedWorksAreCanonicalRelationsOfVisibleTypesOnly()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", 2024);
        var sequel = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire II", 2026);
        var show = await AddTitleAsync(host, WorkMediaType.Series, "Moon Chronicles", 2025);
        var anime = await AddTitleAsync(host, WorkMediaType.Anime, "Moon Anime", 2025);
        host.Db.AddRange(
            new WorkRelation { FromWorkId = movie.Id, ToWorkId = sequel.Id, RelationType = WorkRelationType.Sequel, Source = "test" },
            new WorkRelation { FromWorkId = show.Id, ToWorkId = movie.Id, RelationType = WorkRelationType.SpinOff, Source = "test" },
            new WorkRelation { FromWorkId = movie.Id, ToWorkId = anime.Id, RelationType = WorkRelationType.Adaptation, Source = "test" });
        await host.Db.SaveChangesAsync();
        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Anime, MediaCapability.Hidden);

        var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}");

        var related = Between(html, "<section class=\"ad-related\"", "</section>");
        StringAssert.Contains(related, "Related works");
        StringAssert.Contains(related, $"href=\"/Library/Movie/{sequel.Id}\"");
        StringAssert.Contains(related, "Sequels & prequels");
        StringAssert.Contains(related, $"href=\"/Library/Series/{show.Id}\"");
        StringAssert.Contains(related, "Spin-offs");
        Assert.IsFalse(related.Contains("Moon Anime", StringComparison.Ordinal), "A media type the profile cannot browse stays out.");

        var lonely = await AddTitleAsync(host, WorkMediaType.Movie, "Lonely", 2019);
        Assert.IsFalse((await host.GetOkAsync($"/Library/Movie/{lonely.Id}")).Contains("Related works", StringComparison.Ordinal), "No relations, no panel.");
    }

    // ---- Series --------------------------------------------------------------------------------------------------------

    private sealed record SeededSeries(Work Work, WorkEpisode S1E1, WorkEpisode S1E2, WorkEpisode S1E3, WorkEpisode S2E1, WorkEpisode Special);

    /// <summary>Season 1 with a watched episode, one in progress and one without a file; season 2 and a special without files.</summary>
    private static async Task<SeededSeries> SeedSeriesAsync(VideoDetailPageTestHost host)
    {
        var seed = new LibraryCanonicalSeed(host.Db);
        var work = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor", 2021, "1399");
        var s1e1 = await seed.AddEpisodeAsync(work, 1, 1);
        var s1e2 = await seed.AddEpisodeAsync(work, 1, 2);
        var s1e3 = await seed.AddEpisodeAsync(work, 1, 3);
        var s2e1 = await seed.AddEpisodeAsync(work, 2, 1);
        var special = await seed.AddEpisodeAsync(work, 0, 1);
        await seed.AddVideoAsync(work, s1e1, audio: ["ger", "jpn"], durationSeconds: 1440);
        await seed.AddVideoAsync(work, s1e2, audio: ["ger"], subtitles: ["eng"], durationSeconds: 1440);
        await seed.SetProgressAsync(Profile, work, s1e1, 0, 1_440_000, completed: true, DateTime.UtcNow.AddHours(-2));
        await seed.SetProgressAsync(Profile, work, s1e2, 600_000, 1_440_000, completed: false, DateTime.UtcNow.AddHours(-1));
        host.Db.Add(new ProfilePlaybackPreferences { ProfileId = Profile, PreferredAudioLanguage = "de" });
        await host.Db.SaveChangesAsync();
        return new SeededSeries(work, s1e1, s1e2, s1e3, s2e1, special);
    }

    [TestMethod]
    public async Task APartiallyAvailableSeriesContinuesTheNextPlayableEpisodeAndRequestsTheMissingOnesThroughTheSharedFlow()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var series = await SeedSeriesAsync(host);

        var html = await host.GetOkAsync($"/Library/Series/{series.Work.Id}");

        var hero = Between(html, "<section class=\"ad-hero", "</section>");
        StringAssert.Contains(hero, "Continue watching");
        StringAssert.Contains(hero, "S01 E02");
        StringAssert.Contains(hero, $"href=\"/Library/Watch/{series.Work.Id}/{series.S1E2.Id}\"");
        StringAssert.Contains(hero, "2 of 5 episodes available");
        StringAssert.Contains(hero, "1 of 5 episodes watched");
        StringAssert.Contains(hero, "2 seasons + Specials", "Season and structure summary sits inside the hero.");
        StringAssert.Contains(hero, "Request all missing episodes");
        StringAssert.Contains(hero, $"data-dc-preselect=\"{series.Special.Id},{series.S1E3.Id},{series.S2E1.Id}\"");
        Assert.IsFalse(hero.Contains("ad-hero-poster", StringComparison.Ordinal), "No second poster inside the hero.");

        var rail = Between(html, "<nav class=\"ad-seasons\"", "</nav>");
        StringAssert.Contains(rail, "Season 1");
        StringAssert.Contains(rail, "3 episodes");
        StringAssert.Contains(rail, "Season 2");
        StringAssert.Contains(rail, "Specials");
        Assert.IsTrue(rail.IndexOf("Season 2", StringComparison.Ordinal) < rail.IndexOf("Specials", StringComparison.Ordinal), "The specials come last.");
        StringAssert.Contains(rail, "ad-season is-selected");
        StringAssert.Contains(rail, "season=0");

        var episodes = Between(html, "<section class=\"ad-episodes\"", "</section>");
        StringAssert.Contains(episodes, "S01 E01");
        StringAssert.Contains(episodes, "S01 E02");
        StringAssert.Contains(episodes, "S01 E03");
        Assert.IsFalse(episodes.Contains("S02 E01", StringComparison.Ordinal), "Only the selected season is listed.");
        StringAssert.Contains(episodes, $"href=\"/Library/Watch/{series.Work.Id}/{series.S1E1.Id}\"");
        Assert.IsFalse(episodes.Contains($"/Library/Watch/{series.Work.Id}/{series.S1E3.Id}", StringComparison.Ordinal), "An episode without a file has no player link.");
        StringAssert.Contains(episodes, "ad-ep-available is-watched");
        StringAssert.Contains(episodes, "ad-ep-unavailable");
        StringAssert.Contains(episodes, "Not available");
        StringAssert.Contains(episodes, "aria-valuenow=\"42\"");
        StringAssert.Contains(episodes, $"data-dc-preselect=\"{series.S1E3.Id}\"");
        StringAssert.Contains(episodes, "Request this episode");
        StringAssert.Matches(episodes, new System.Text.RegularExpressions.Regex("ad-lang is-preferred\"\\s+title=\"Preferred language\">DE<"));
        StringAssert.Contains(html, "data-dc-category=\"tv\"");
        StringAssert.Contains(html, "data-dc-external-id=\"1399\"");
    }

    [TestMethod]
    public async Task TheSelectedSeasonComesFromTheAddressAndFallsBackToTheOneOfTheNextEpisode()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var series = await SeedSeriesAsync(host);

        var second = await host.GetOkAsync($"/Library/Series/{series.Work.Id}?season=2");
        var episodes = Between(second, "<section class=\"ad-episodes\"", "</section>");
        StringAssert.Contains(episodes, "S02 E01");
        Assert.IsFalse(episodes.Contains("S01 E01", StringComparison.Ordinal));
        StringAssert.Contains(episodes, "Season 2 · 1 episode");

        var specials = await host.GetOkAsync($"/Library/Series/{series.Work.Id}?season=0");
        StringAssert.Contains(Between(specials, "<section class=\"ad-episodes\"", "</section>"), "S00 E01");

        var unknown = await host.GetOkAsync($"/Library/Series/{series.Work.Id}?season=99");
        StringAssert.Contains(Between(unknown, "<section class=\"ad-episodes\"", "</section>"), "S01 E01");

        var newestFirst = await host.GetOkAsync($"/Library/Series/{series.Work.Id}?sort=desc&view=list");
        var list = Between(newestFirst, "<ul class=\"ad-episode-list", "</ul>");
        StringAssert.Contains(list, "is-list");
        Assert.IsTrue(list.IndexOf("S01 E03", StringComparison.Ordinal) < list.IndexOf("S01 E01", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EpisodesShowTheStateOfTheOpenRequestOnlyWhereItsScopeReachesThem()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var series = await SeedSeriesAsync(host);
        var scope = new VideoRequestPayload(series.Work.Id, "Dark Harbor", 2021, VideoRequestScope.Custom, [series.S1E3.Id], false);
        await OpenRequestAsync(host, MediaAcquisitionKind.Tv, "1399", "Dark Harbor", AcquisitionRequestStatus.Approved, scope.Serialize());

        var html = await host.GetOkAsync($"/Library/Series/{series.Work.Id}");

        var episodes = Between(html, "<section class=\"ad-episodes\"", "</section>");
        var third = Between(episodes, "S01 E03", "</article>");
        StringAssert.Contains(third, "ad-state-requested");
        Assert.IsFalse(episodes.Contains("data-dc-preselect", StringComparison.Ordinal), "An open request closes the request actions.");
        var otherSeason = Between(await host.GetOkAsync($"/Library/Series/{series.Work.Id}?season=2"), "S02 E01", "</article>");
        StringAssert.Contains(otherSeason, "ad-state-unavailable", "The request does not include season 2.");
        StringAssert.Contains(Between(html, "<section class=\"ad-hero", "</section>"), "href=\"/Requests\"");
    }

    [TestMethod]
    public async Task ARequestWithoutAReadablePayloadCoversTheWholeSeriesLikeTheExecutorTreatsIt()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var series = await SeedSeriesAsync(host);
        await OpenRequestAsync(host, MediaAcquisitionKind.Tv, "1399", "Dark Harbor", AcquisitionRequestStatus.Approved);

        var html = await host.GetOkAsync($"/Library/Series/{series.Work.Id}");

        StringAssert.Contains(Between(html, "S01 E03", "</article>"), "ad-state-requested");
    }

    [TestMethod]
    public async Task RelatedWorksReadFromTheViewedWorksSideOfTheEdge()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire II", 2026);
        var first = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", 2024);
        host.Db.Add(new WorkRelation { FromWorkId = first.Id, ToWorkId = movie.Id, RelationType = WorkRelationType.Source, Source = "test" });
        await host.Db.SaveChangesAsync();

        var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}");

        StringAssert.Contains(Between(html, "<section class=\"ad-related\"", "</section>"), "Adaptations");
    }

    [TestMethod]
    public async Task OnlyTheEpisodeTheDownloadIsOnReadsDownloading()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var series = await SeedSeriesAsync(host);
        var scope = new VideoRequestPayload(series.Work.Id, "Dark Harbor", 2021, VideoRequestScope.AllCurrentAndFuture, [], true, ActiveWorkEpisodeId: series.S1E3.Id);
        await OpenRequestAsync(host, MediaAcquisitionKind.Tv, "1399", "Dark Harbor", AcquisitionRequestStatus.Downloading, scope.Serialize());

        var html = await host.GetOkAsync($"/Library/Series/{series.Work.Id}");

        StringAssert.Contains(Between(html, "S01 E03", "</article>"), "ad-state-downloading");
        StringAssert.Contains(Between(await host.GetOkAsync($"/Library/Series/{series.Work.Id}?season=2"), "S02 E01", "</article>"), "ad-state-requested");
    }

    [TestMethod]
    public async Task ASeriesWithoutFilesOffersRequestAndAnEpisodeInOnlyOtherLanguagesIsMarked()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var seed = new LibraryCanonicalSeed(host.Db);
        var work = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor", 2021, "1399");
        var episode = await seed.AddEpisodeAsync(work, 1, 1);
        await seed.AddEpisodeAsync(work, 1, 2);

        var html = await host.GetOkAsync($"/Library/Series/{work.Id}");

        var hero = Between(html, "<section class=\"ad-hero", "</section>");
        StringAssert.Contains(hero, ">Request<");
        Assert.IsFalse(hero.Contains("Continue watching", StringComparison.Ordinal));
        Assert.IsFalse(hero.Contains("Request missing episodes", StringComparison.Ordinal), "The hero action already is Request.");

        await seed.AddVideoAsync(work, episode, audio: ["jpn"]);
        host.Db.Add(new ProfilePlaybackPreferences { ProfileId = Profile, PreferredAudioLanguage = "de" });
        await host.Db.SaveChangesAsync();

        var withFile = await host.GetOkAsync($"/Library/Series/{work.Id}");
        StringAssert.Contains(Between(withFile, "S01 E01", "</article>"), "ad-state-other-language");
        StringAssert.Contains(withFile, ">Start watching<", "A Series with no history and a local first episode starts watching it (Instant Play matrix).");
    }

    [TestMethod]
    public async Task ASeriesWithoutAnyEpisodeStructureStillRendersItsHero()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var work = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor", 2021, "1399");

        var html = await host.GetOkAsync($"/Library/Series/{work.Id}");

        StringAssert.Contains(html, "ad-main no-rail no-side");
        StringAssert.Contains(html, "No episodes in the library yet.");
        StringAssert.Contains(Between(html, "<section class=\"ad-hero", "</section>"), ">Request<");
    }

    // ---- Authorization and identity ------------------------------------------------------------------------------------

    [TestMethod]
    public async Task UnknownIdsAndWorksOfAnotherMediaTypeAreNotFound()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", 2024);
        var series = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor", 2021);
        var anime = await AddTitleAsync(host, WorkMediaType.Anime, "Starfall", 2020);

        foreach (var path in new[]
        {
            $"/Library/Movie/{Guid.NewGuid()}",
            $"/Library/Series/{Guid.NewGuid()}",
            $"/Library/Movie/{series.Id}",
            $"/Library/Series/{movie.Id}",
            $"/Library/Movie/{anime.Id}",
            $"/Library/Series/{anime.Id}",
            "/Library/Movie/not-an-id"
        })
        {
            Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync(path)).Status, path);
        }
    }

    [TestMethod]
    public async Task APageOpensOnlyForProfilesThatMayBrowseItsMediaType()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", 2024);
        var series = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor", 2021);
        foreach (var type in WorkMediaTypes.All.Where(type => type != WorkMediaType.Movie))
        {
            await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, type, MediaCapability.Hidden);
        }

        Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync($"/Library/Movie/{movie.Id}")).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync($"/Library/Series/{series.Id}")).Status, "A Movie-only profile has no Series pages.");
        Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync($"/Library/Series/{series.Id}", asOwner: true)).Status, "The owner is unrestricted.");

        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Hidden);
        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Anime, MediaCapability.Browse);

        Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync($"/Library/Movie/{movie.Id}")).Status, "An Anime profile has no Movie pages.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync($"/Library/Series/{series.Id}")).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync($"/Library/Watch/{movie.Id}")).Status);
    }

    [TestMethod]
    public async Task ProgressIsPerProfile()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var seed = new LibraryCanonicalSeed(host.Db);
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", 2024);
        await seed.AddVideoAsync(movie, null, durationSeconds: 6720);
        await seed.SetProgressAsync("someone-else", movie, null, 45 * 60 * 1000, 6720 * 1000, completed: false, DateTime.UtcNow);

        var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}");

        StringAssert.Contains(html, ">Play<");
        Assert.IsFalse(html.Contains("Continue watching", StringComparison.Ordinal), "Another profile's progress is not mine.");
    }

    // ---- Instant Play hero action --------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AMissingTitleWithAnApprovedRequestThatDoesNotCoverItStartsThroughTheIntentFormNotADeadButton()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        await host.MakeAcquisitionReadyAsync();
        var work = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor", 2021, "1399");
        var seed = new LibraryCanonicalSeed(host.Db);
        var first = await seed.AddEpisodeAsync(work, 1, 1);
        var second = await seed.AddEpisodeAsync(work, 1, 2);
        var scope = new VideoRequestPayload(work.Id, "Dark Harbor", 2021, VideoRequestScope.Custom, [second.Id], MonitorFuture: false);
        await OpenRequestAsync(host, MediaAcquisitionKind.Tv, "1399", "Dark Harbor", AcquisitionRequestStatus.Approved, scope.Serialize());

        var html = await host.GetOkAsync($"/Library/Series/{work.Id}", asOwner: true);

        var hero = Between(html, "<section class=\"ad-hero", "</section>");
        StringAssert.Contains(hero, "method=\"post\"");
        StringAssert.Contains(hero, "handler=Start");
        StringAssert.Contains(hero, $"name=\"episodeId\" value=\"{first.Id}\"");
        StringAssert.Contains(hero, ">Start watching<");
        Assert.IsFalse(hero.Contains("data-dc-card-request", StringComparison.Ordinal), "No button may depend on a dialog the page does not render.");
    }
}
