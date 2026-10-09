using Jularr.Web.Data;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Progress;
using Jularr.Web.Pages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// Home reads canonical video state for Movies, Series and Anime alike: Continue Watching and the hero from the profile's
/// MediaProgress, the recently added row from the canonical Work/WorkEpisode/MediaAsset rows. Everything is profile-scoped.
/// </summary>
[TestClass]
public sealed class HomeVideoCanonicalTests
{
    private const string Alice = "alice";
    private const string Bob = "bob";

    [TestMethod]
    public async Task ContinueSpansMoviesSeriesAndAnimeWithTheirOwnPlayerRoutes()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Quiet Harbor", 2023);
        await seed.AddVideoAsync(movie, null, durationSeconds: 7440);
        var series = await seed.AddWorkAsync(WorkMediaType.Series, "The Long Winter", 2022);
        var seriesEpisodes = new[] { await seed.AddEpisodeAsync(series, 1, 1), await seed.AddEpisodeAsync(series, 1, 2), await seed.AddEpisodeAsync(series, 1, 3) };
        foreach (var episode in seriesEpisodes)
        {
            await seed.AddVideoAsync(series, episode);
        }

        var anime = await seed.AddAnimeAsync("Starlit Academy", [(1, 1, true), (1, 2, true)]);
        var progress = new VideoProgressService(fixture.Db);
        await progress.UpdateAsync(Alice, MediaProgressTarget.Episode(anime.Work.Id, anime.Episodes[1].Canonical.Id), new MediaProgressUpdate(600_000, 1_440_000, false));
        await progress.UpdateAsync(Alice, MediaProgressTarget.Episode(series.Id, seriesEpisodes[2].Id), new MediaProgressUpdate(1_100_000, 2_640_000, false));
        await progress.UpdateAsync(Alice, MediaProgressTarget.Movie(movie.Id), new MediaProgressUpdate(2_976_000, 7_440_000, false));

        var home = await LoadAsync(fixture.Db, Alice);

        CollectionAssert.AreEqual(new[] { "Quiet Harbor", "The Long Winter", "Starlit Academy" }, home.ContinueWatching.Select(item => item.Title.Title).ToArray());
        var movieItem = home.ContinueWatching[0];
        Assert.AreEqual($"/Library/Watch/{movie.Id}", movieItem.PlayHref);
        Assert.AreEqual($"/Library/Movie/{movie.Id}", movieItem.Title.DetailHref);
        Assert.AreEqual(40, movieItem.Percent);
        Assert.AreEqual($"/Library/Watch/{series.Id}/{seriesEpisodes[2].Id}", home.ContinueWatching[1].PlayHref);
        Assert.AreEqual($"/Library/Episode/{anime.Episodes[1].Legacy.Id}", home.ContinueWatching[2].PlayHref, "Anime is still routed by its legacy episode.");

        CollectionAssert.AreEqual(home.ContinueWatching.Select(item => item.PlayHref).ToArray(), home.ContinueTiles.Select(tile => tile.Href).ToArray());
        Assert.AreEqual(home.Ui["home.continueWatching"], home.ContinueHeading);
        Assert.AreEqual("75 min left", home.ContinueTiles[0].Caption, "A Movie caption is its remaining time; it has no episode.");
        StringAssert.StartsWith(home.ContinueTiles[1].Caption, "S01 · Episode 3");

        Assert.AreEqual("Quiet Harbor", home.Hero[0].Title, "The strongest Continue item leads the Hero.");
        CollectionAssert.IsSubsetOf(new[] { "Quiet Harbor", "The Long Winter" }, home.Hero.Select(slide => slide.Title).ToArray());
        Assert.IsTrue(home.Hero.Count > 2 || home.Hero.All(slide => slide.PrimaryIsPlay), "The pool holds more than the Continue items where the library has more.");
        Assert.AreEqual(home.Ui["calendar.media.movie"], home.Hero[0].Meta!.Split(" · ")[1]);
    }

    [TestMethod]
    public async Task HeroAndTilesCarryPersistedArtworkYearAndSynopsisOfMoviesAndSeries()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Quiet Harbor", 2023);
        await seed.AddVideoAsync(movie, null, durationSeconds: 7440);
        await SeedMetadataAsync(fixture.Db, movie.Id, "A harbour keeper stays through the last winter.", withBackdrop: true);
        await new VideoProgressService(fixture.Db).UpdateAsync(Alice, MediaProgressTarget.Movie(movie.Id), new MediaProgressUpdate(2_976_000, 7_440_000, false));

        var home = await LoadAsync(fixture.Db, Alice);

        var slide = Assert.ContainsSingle(home.Hero);
        Assert.AreEqual("A harbour keeper stays through the last winter.", slide.Description);
        Assert.IsTrue(slide.ImageIsBackdrop);
        StringAssert.StartsWith(slide.ImageUrl, $"/works/{movie.Id}/artwork/");
        Assert.AreEqual($"2023 · {home.Ui["calendar.media.movie"]} · ★ 7.9", slide.Meta);
        Assert.IsNull(slide.Subtitle, "A Movie has no episode line.");
        var tile = Assert.ContainsSingle(home.ContinueTiles);
        Assert.IsFalse(tile.ImageIsBackdrop, "The Continue tile is a poster card: the backdrop only stands in for a title without a poster.");
        Assert.AreEqual(40, tile.ProgressPercent);
    }

    [TestMethod]
    public async Task CompletedTitlesLeaveContinueAndTheNextEpisodeTakesOverOnlyWhenPlayable()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Finished Film");
        await seed.AddVideoAsync(movie, null);
        var running = await seed.AddWorkAsync(WorkMediaType.Series, "Running Series");
        var finished = await seed.AddWorkAsync(WorkMediaType.Series, "Finished Series");
        var runningEpisodes = new[] { await seed.AddEpisodeAsync(running, 1, 1), await seed.AddEpisodeAsync(running, 1, 2) };
        var finishedEpisodes = new[] { await seed.AddEpisodeAsync(finished, 1, 1), await seed.AddEpisodeAsync(finished, 1, 2) };
        foreach (var episode in runningEpisodes)
        {
            await seed.AddVideoAsync(running, episode);
        }

        foreach (var episode in finishedEpisodes)
        {
            await seed.AddVideoAsync(finished, episode);
        }

        var progress = new VideoProgressService(fixture.Db);
        await progress.SetCompletedAsync(Alice, MediaProgressTarget.Movie(movie.Id), true);
        await progress.SetCompletedAsync(Alice, MediaProgressTarget.Episode(running.Id, runningEpisodes[0].Id), true);
        foreach (var episode in finishedEpisodes)
        {
            await progress.SetCompletedAsync(Alice, MediaProgressTarget.Episode(finished.Id, episode.Id), true);
        }

        var home = await LoadAsync(fixture.Db, Alice);

        var item = Assert.ContainsSingle(home.ContinueWatching);
        Assert.AreEqual(VideoContinueWatchingKind.UpNext, item.Kind);
        Assert.AreEqual("Running Series", item.Title.Title);
        Assert.AreEqual(2, item.EpisodeNumber);
        Assert.AreEqual($"/Library/Watch/{running.Id}/{runningEpisodes[1].Id}", item.PlayHref);
        Assert.IsNull(Assert.ContainsSingle(home.ContinueTiles).ProgressPercent, "An up-next episode has no progress yet.");
        var slide = home.Hero[0];
        Assert.AreEqual(home.Ui["home.continueWatching.upNext"], slide.Label);
        Assert.AreEqual(home.Ui["home.spotlight.play"], slide.PrimaryLabel);
    }

    [TestMethod]
    public async Task OnlyTheOwnProfilesProgressIsShownButTheLibraryIsShared()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Quiet Harbor");
        await seed.AddVideoAsync(movie, null);
        await new VideoProgressService(fixture.Db).UpdateAsync(Alice, MediaProgressTarget.Movie(movie.Id), new MediaProgressUpdate(2_976_000, 7_440_000, false));

        var bob = await LoadAsync(fixture.Db, Bob);

        Assert.IsEmpty(bob.ContinueWatching);
        Assert.IsEmpty(bob.ContinueTiles);
        Assert.IsFalse(bob.Hero.Any(slide => slide.Label == bob.Ui["home.continueWatching.eyebrow"]), "Bob has no progress, so nothing of Alice's Continue leads his Hero.");
        Assert.IsEmpty(bob.PlaybackHistory);
        Assert.AreEqual("Quiet Harbor", Assert.ContainsSingle(bob.RecentTitles).Title.Title);
        Assert.AreEqual(1, (await LoadAsync(fixture.Db, Alice)).PlaybackHistory.Count);
    }

    [TestMethod]
    public async Task PlaybackHistoryNamesMoviesSeriesAndAnimeWithTheirPlayers()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Quiet Harbor");
        await seed.AddVideoAsync(movie, null);
        var series = await seed.AddWorkAsync(WorkMediaType.Series, "The Long Winter");
        var episode = await seed.AddEpisodeAsync(series, 2, 4);
        await seed.AddVideoAsync(series, episode);
        var anime = await seed.AddAnimeAsync("Starlit Academy", [(1, 1, true)]);
        var progress = new VideoProgressService(fixture.Db);
        await progress.UpdateAsync(Alice, MediaProgressTarget.Movie(movie.Id), new MediaProgressUpdate(2_976_000, 7_440_000, false));
        await progress.UpdateAsync(Alice, MediaProgressTarget.Episode(series.Id, episode.Id), new MediaProgressUpdate(1_100_000, 2_640_000, false));
        await progress.UpdateAsync(Alice, MediaProgressTarget.Episode(anime.Work.Id, anime.Episodes[0].Canonical.Id), new MediaProgressUpdate(600_000, 1_440_000, false));

        var home = await LoadAsync(fixture.Db, Alice);

        CollectionAssert.AreEqual(
            new[] { $"/Library/Episode/{anime.Episodes[0].Legacy.Id}", $"/Library/Watch/{series.Id}/{episode.Id}", $"/Library/Watch/{movie.Id}" },
            home.PlaybackHistory.Select(entry => entry.PlayHref).ToArray());
        Assert.AreEqual(home.Ui.Format("home.continueWatching.episode", ("season", "02"), ("episode", 4)), home.EpisodeLabel(2, 4));
    }

    [TestMethod]
    public async Task RecentlyAddedShowsOneCardPerTitleAcrossTypesNewestFirst()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var anime = await seed.AddAnimeAsync("Starlit Academy", [(1, 1, true), (1, 2, true)]);
        var series = await seed.AddWorkAsync(WorkMediaType.Series, "The Long Winter", 2022);
        await seed.AddVideoAsync(series, await seed.AddEpisodeAsync(series, 1, 1));
        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Quiet Harbor", 2023);
        await seed.AddVideoAsync(movie, null);
        var notYetFetched = await seed.AddWorkAsync(WorkMediaType.Series, "Structure Only");
        await seed.AddEpisodeAsync(notYetFetched, 1, 1);
        await seed.AddVideoAsync(series, await seed.AddEpisodeAsync(series, 1, 2));

        var home = await LoadAsync(fixture.Db, Alice);

        CollectionAssert.AreEqual(new[] { "The Long Winter", "Quiet Harbor", "Starlit Academy" }, home.RecentTitles.Select(item => item.Title.Title).ToArray());
        Assert.AreEqual("S01 · Episode 2", home.RecentTitles[0].Subtitle, "A series shows its newest added episode.");
        Assert.AreEqual("2023", home.RecentTitles[1].Subtitle, "A Movie shows its year.");
        Assert.AreEqual("S01 · Episode 2", home.RecentTitles[2].Subtitle);
        Assert.AreEqual($"/Library/Series/{series.Id}", home.RecentTitles[0].Title.DetailHref);
        Assert.AreEqual($"/Library/Movie/{movie.Id}", home.RecentTitles[1].Title.DetailHref);
        Assert.AreEqual($"/Library/Anime/{anime.Anime.Id}", home.RecentTitles[2].Title.DetailHref);
        Assert.IsFalse(home.IsEmpty);
    }

    [TestMethod]
    public async Task EmptyStateAppearsOnlyForAnEmptyLibraryAndOffersStorageToOwnersOnly()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var owner = EpisodeFlowFixture.Home(fixture.Db, EpisodeFlowFixture.Account(Alice, AccountRoles.Owner));
        var member = EpisodeFlowFixture.Home(fixture.Db, EpisodeFlowFixture.Account(Bob));
        await owner.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);
        await member.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);

        Assert.IsTrue(owner.IsEmpty);
        Assert.IsTrue(owner.CanManageStorage);
        Assert.IsTrue(member.IsEmpty);
        Assert.IsFalse(member.CanManageStorage, "The storage link is only for accounts that may manage library folders.");

        var seed = new LibraryCanonicalSeed(fixture.Db);
        await seed.AddVideoAsync(await seed.AddWorkAsync(WorkMediaType.Movie, "Quiet Harbor"), null);
        await member.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);

        Assert.IsFalse(member.IsEmpty);
    }

    [TestMethod]
    public async Task ManagerOnlyInstanceShowsTitlesButNoPlayAffordance()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Quiet Harbor");
        await seed.AddVideoAsync(movie, null);
        await new VideoProgressService(fixture.Db).UpdateAsync(Alice, MediaProgressTarget.Movie(movie.Id), new MediaProgressUpdate(2_976_000, 7_440_000, false));
        var modules = new InstanceModuleStore(Directory.CreateTempSubdirectory("jularr-home-modules-").FullName);
        await modules.SetAsync(InstanceModule.Playback, false);

        var home = EpisodeFlowFixture.Home(fixture.Db, EpisodeFlowFixture.Account(Alice), null, modules, null);
        await home.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);

        Assert.IsFalse(home.PlaybackEnabled);
        Assert.IsEmpty(home.ContinueWatching);
        Assert.IsEmpty(home.ContinueTiles);
        Assert.IsEmpty(home.PlaybackHistory);
        Assert.IsFalse(home.Hero.Any(slide => slide.PrimaryIsPlay), "A manager-only instance has no play affordance in the Hero.");
        var card = Assert.ContainsSingle(home.RecentTitles);
        Assert.AreEqual($"/Library/Movie/{movie.Id}", card.Title.DetailHref, "A card opens the title, never the player.");
    }

    [TestMethod]
    public async Task ADisabledVideoModuleHidesItsTitlesFromEveryRow()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Quiet Harbor");
        await seed.AddVideoAsync(movie, null);
        var series = await seed.AddWorkAsync(WorkMediaType.Series, "The Long Winter");
        var episode = await seed.AddEpisodeAsync(series, 1, 1);
        await seed.AddVideoAsync(series, episode);
        var progress = new VideoProgressService(fixture.Db);
        await progress.UpdateAsync(Alice, MediaProgressTarget.Movie(movie.Id), new MediaProgressUpdate(2_976_000, 7_440_000, false));
        await progress.UpdateAsync(Alice, MediaProgressTarget.Episode(series.Id, episode.Id), new MediaProgressUpdate(1_100_000, 2_640_000, false));
        var modules = new InstanceModuleStore(Directory.CreateTempSubdirectory("jularr-home-modules-").FullName);
        await modules.SetAsync(InstanceModule.Movie, false);

        var home = EpisodeFlowFixture.Home(fixture.Db, EpisodeFlowFixture.Account(Alice), null, modules, null);
        await home.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);

        Assert.AreEqual("The Long Winter", Assert.ContainsSingle(home.ContinueWatching).Title.Title);
        Assert.AreEqual("The Long Winter", Assert.ContainsSingle(home.RecentTitles).Title.Title);
        Assert.AreEqual("The Long Winter", Assert.ContainsSingle(home.PlaybackHistory).Title.Title);
    }

    [TestMethod]
    public async Task TheTypeFilterNarrowsContinueToThatVideoTypeAndReadingFiltersHideVideo()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Quiet Harbor");
        await seed.AddVideoAsync(movie, null);
        var series = await seed.AddWorkAsync(WorkMediaType.Series, "The Long Winter");
        var episode = await seed.AddEpisodeAsync(series, 1, 1);
        await seed.AddVideoAsync(series, episode);
        var progress = new VideoProgressService(fixture.Db);
        await progress.UpdateAsync(Alice, MediaProgressTarget.Movie(movie.Id), new MediaProgressUpdate(2_976_000, 7_440_000, false));
        await progress.UpdateAsync(Alice, MediaProgressTarget.Episode(series.Id, episode.Id), new MediaProgressUpdate(1_100_000, 2_640_000, false));

        Assert.AreEqual(2, (await LoadAsync(fixture.Db, Alice, null)).ContinueWatching.Count);
        Assert.AreEqual("Quiet Harbor", Assert.ContainsSingle((await LoadAsync(fixture.Db, Alice, "movie")).ContinueWatching).Title.Title);
        Assert.AreEqual("The Long Winter", Assert.ContainsSingle((await LoadAsync(fixture.Db, Alice, "series")).ContinueWatching).Title.Title);
        Assert.IsEmpty((await LoadAsync(fixture.Db, Alice, "anime")).ContinueWatching);
        Assert.IsEmpty((await LoadAsync(fixture.Db, Alice, "manga")).ContinueWatching);
    }

    [TestMethod]
    public void HomeReadsVideoOnlyThroughTheCanonicalQueryNotLegacyAnimeTables()
    {
        var page = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Pages", "Index.cshtml.cs"));

        foreach (var legacy in new[] { "db.Episodes", "db.Anime", "db.AnimeMetadata", "EpisodeProgressService", "ContinueWatchingItem" })
        {
            Assert.IsFalse(page.Contains(legacy, StringComparison.Ordinal), $"Home must not read {legacy}; video comes from HomeVideoQuery.");
        }

        StringAssert.Contains(page, "new HomeVideoQuery(db, videoProgress)");
    }

    private static Task<IndexModel> LoadAsync(AppDbContext db, string profileId) => LoadAsync(db, profileId, null);

    private static async Task<IndexModel> LoadAsync(AppDbContext db, string profileId, string? type)
    {
        var home = EpisodeFlowFixture.Home(db, EpisodeFlowFixture.Account(profileId));
        await home.LoadHomeAsync(DiscoveryRequest.ParseCategory(type), CancellationToken.None);
        return home;
    }

    private static async Task SeedMetadataAsync(AppDbContext db, Guid workId, string overview, bool withBackdrop)
    {
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "WorkMetadataFacts" ("WorkId", "OriginalLanguage", "RuntimeMinutes", "Rating", "Studios", "ProductionCountries", "UpdatedAt")
            VALUES ({workId}, 'en', 124, 7.9, ARRAY[]::text[], ARRAY[]::text[], {now})
            """);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "WorkLocalizedValues" ("WorkId", "Locale", "Field", "Position", "Value", "Origin", "Source", "FallbackPriority", "IsManualOverride", "FetchedAt", "UpdatedAt")
            VALUES ({workId}, 'en', {(int)WorkLocalizedField.Overview}, 0, {overview}, 0, 'tmdb', 0, FALSE, {now}, {now})
            """);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "WorkArtwork" ("WorkId", "Slot", "Language", "Source", "ProviderFilePath", "Width", "Height", "VoteAverage", "VoteCount", "CacheKey", "IsManualOverride", "FetchedAt", "CachedAt", "UpdatedAt")
            VALUES ({workId}, {(int)WorkArtworkSlot.Poster}, '', 'tmdb', '/poster.jpg', 500, 750, 5, 5, '0123456789abcdef0123456789abcdef', FALSE, {now}, {now}, {now})
            """);
        if (withBackdrop)
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "WorkArtwork" ("WorkId", "Slot", "Language", "Source", "ProviderFilePath", "Width", "Height", "VoteAverage", "VoteCount", "CacheKey", "IsManualOverride", "FetchedAt", "CachedAt", "UpdatedAt")
                VALUES ({workId}, {(int)WorkArtworkSlot.Backdrop}, '', 'tmdb', '/backdrop.jpg', 1280, 720, 5, 5, 'fedcba9876543210fedcba9876543210', FALSE, {now}, {now}, {now})
                """);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }
}
