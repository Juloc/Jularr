using System.Data.Common;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Providers;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Ui;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// The one canonical Library read model: Anime, Series and Movie Works with availability and languages from
/// MediaAsset -> StoredFile -> MediaTrack, progress from the profile's MediaProgress and requests projected from the
/// shared acquisition requests.
/// </summary>
[TestClass]
public sealed class LibraryCanonicalReadTests
{
    private const string Alice = "alice";
    private const string Bob = "bob";
    private static readonly DateTime BaseTime = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly WorkMediaType[] AllVideo = [WorkMediaType.Anime, WorkMediaType.Series, WorkMediaType.Movie];

    [TestMethod]
    public async Task AnimeSeriesAndMoviesAppearTogetherWithTheirCanonicalIdentity()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var anime = await seed.AddAnimeAsync("Akatsuki", [(1, 1, true)]);
        var series = await seed.AddWorkAsync(WorkMediaType.Series, "Dark Harbor", 2021);
        await seed.AddVideoAsync(series, await seed.AddEpisodeAsync(series, 1, 1));
        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Moon Empire", 2024);
        await seed.AddVideoAsync(movie, null, durationSeconds: 7440);

        var read = await ReadAsync(fixture, Alice);

        Assert.IsFalse(read.Degraded);
        CollectionAssert.AreEquivalent(
            new[] { WorkMediaType.Anime, WorkMediaType.Series, WorkMediaType.Movie },
            read.Entries.Select(x => x.MediaType).ToArray());
        Assert.AreEqual($"/Library/Anime/{anime.Anime.Id}", Entry(read, anime.Work.Id).Card.Href, "Anime keeps its legacy-keyed detail route.");
        Assert.AreEqual($"/Library/Series/{series.Id}", Entry(read, series.Id).Card.Href, "A Series is keyed by its Work.");
        Assert.AreEqual($"/Library/Movie/{movie.Id}", Entry(read, movie.Id).Card.Href, "A Movie is keyed by its Work.");
        Assert.AreEqual(MediaBannerKind.Series, Entry(read, series.Id).Card.Kind);
        Assert.AreEqual(2021, Entry(read, series.Id).Card.Year);
        Assert.AreEqual(124, Entry(read, movie.Id).RuntimeMinutes, "Runtime comes from the analysed file.");
    }

    [TestMethod]
    public async Task OnlyTheRequestedMediaTypesAreRead()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        await seed.AddAnimeAsync("Akatsuki", [(1, 1, true)]);
        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Moon Empire");
        await seed.AddVideoAsync(movie, null);
        var book = await seed.AddWorkAsync(WorkMediaType.Book, "Not video");
        await seed.AddVideoAsync(book, null);

        var movies = await new LibraryMediaCardQuery(fixture.Db).GetEntriesAsync(Alice, [WorkMediaType.Movie], CancellationToken.None);
        var anime = await new LibraryMediaCardQuery(fixture.Db).GetEntriesAsync(Alice, [WorkMediaType.Anime], CancellationToken.None);
        var everything = await ReadAsync(fixture, Alice);
        var nothing = await new LibraryMediaCardQuery(fixture.Db).GetEntriesAsync(Alice, [], CancellationToken.None);

        Assert.AreEqual(movie.Id, Assert.ContainsSingle(movies.Entries).WorkId);
        Assert.AreEqual(WorkMediaType.Anime, Assert.ContainsSingle(anime.Entries).MediaType);
        Assert.AreEqual(2, everything.Entries.Count, "A book is not a Library video title.");
        Assert.AreEqual(0, nothing.Entries.Count);
    }

    [TestMethod]
    public async Task AvailabilityIsDerivedFromTheFilesOfEachTitle()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var complete = await seed.AddWorkAsync(WorkMediaType.Series, "Complete");
        var partial = await seed.AddWorkAsync(WorkMediaType.Series, "Partial");
        var bare = await seed.AddWorkAsync(WorkMediaType.Series, "Structure only");
        foreach (var number in Enumerable.Range(1, 3))
        {
            await seed.AddVideoAsync(complete, await seed.AddEpisodeAsync(complete, 1, number));
        }

        foreach (var number in Enumerable.Range(1, 5))
        {
            var episode = await seed.AddEpisodeAsync(partial, 1, number);
            if (number <= 3)
            {
                await seed.AddVideoAsync(partial, episode);
            }

            await seed.AddEpisodeAsync(bare, 1, number);
        }

        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Available movie");
        await seed.AddVideoAsync(movie, null);
        var movieWithoutFile = await seed.AddWorkAsync(WorkMediaType.Movie, "Movie without a file");
        fixture.Db.Add(new WorkSourceLink { WorkId = movieWithoutFile.Id, SourceKind = WorkSourceKind.Movie, SourceId = Guid.NewGuid() });
        fixture.Db.Add(new WorkSourceLink { WorkId = bare.Id, SourceKind = WorkSourceKind.Series, SourceId = Guid.NewGuid() });
        await fixture.Db.SaveChangesAsync();

        var read = await ReadAsync(fixture, Alice);

        AssertAvailability(Entry(read, complete.Id), 3, 0, LibraryAvailabilityState.Complete, null);
        AssertAvailability(Entry(read, partial.Id), 3, 2, LibraryAvailabilityState.Partial, LibraryAvailabilityState.Partial);
        AssertAvailability(Entry(read, movie.Id), 1, 0, LibraryAvailabilityState.Complete, null);
        AssertAvailability(Entry(read, bare.Id), 0, 5, LibraryAvailabilityState.Missing, LibraryAvailabilityState.Missing);
        AssertAvailability(Entry(read, movieWithoutFile.Id), 0, 0, LibraryAvailabilityState.Missing, LibraryAvailabilityState.Missing);
        Assert.IsNull(Entry(read, movieWithoutFile.Id).Card.Progress, "Nothing playable means no resume state.");
    }

    [TestMethod]
    public async Task AnAssetWhoseFileIsGoneIsNotAvailable()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Deleted file");
        await seed.AddVideoAsync(movie, null);
        fixture.Db.StoredFiles.RemoveRange(await fixture.Db.StoredFiles.ToListAsync());
        fixture.Db.Add(new WorkSourceLink { WorkId = movie.Id, SourceKind = WorkSourceKind.Movie, SourceId = Guid.NewGuid() });
        await fixture.Db.SaveChangesAsync();

        var entry = Entry(await ReadAsync(fixture, Alice), movie.Id);

        Assert.AreEqual(0, entry.PlayableUnits);
        Assert.AreEqual(LibraryAvailabilityState.Missing, LibraryBrowse.IndicatorOf(entry));
    }

    [TestMethod]
    public async Task LanguagesComeFromTheCanonicalTracksAndAnimeSidecarSubtitles()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Languages");
        await seed.AddVideoAsync(movie, null, audio: ["jpn", "ger"], subtitles: ["eng", "und"]);
        var series = await seed.AddWorkAsync(WorkMediaType.Series, "Series languages");
        await seed.AddVideoAsync(series, await seed.AddEpisodeAsync(series, 1, 1), audio: ["ger"], subtitles: ["ger"]);
        await seed.AddVideoAsync(series, await seed.AddEpisodeAsync(series, 1, 2), audio: ["ger", "eng"]);
        var anime = await seed.AddAnimeAsync("Sidecar", [(1, 1, true)], audio: ["jpn"], subtitles: ["eng"]);
        fixture.Db.SubtitleTracks.Add(new SubtitleTrack { EpisodeId = anime.Episodes[0].Legacy.Id, Path = "a.de.srt", Language = "de", Format = "srt" });
        await fixture.Db.SaveChangesAsync();

        var read = await ReadAsync(fixture, Alice);

        CollectionAssert.AreEqual(new[] { "ja", "de" }, Entry(read, movie.Id).Card.AudioLanguages!.ToArray());
        CollectionAssert.AreEqual(new[] { "en" }, Entry(read, movie.Id).Card.SubtitleLanguages!.ToArray(), "An undetermined language is not a language.");
        CollectionAssert.AreEqual(new[] { "de", "en" }, Entry(read, series.Id).Card.AudioLanguages!.ToArray(), "Most common first.");
        CollectionAssert.AreEqual(new[] { "de" }, Entry(read, series.Id).Card.SubtitleLanguages!.ToArray());
        CollectionAssert.AreEqual(new[] { "en", "de" }, Entry(read, anime.Work.Id).Card.SubtitleLanguages!.ToArray(), "Embedded subtitles first, imported sidecars after.");
    }

    [TestMethod]
    public async Task ProgressComesFromTheProfilesMediaProgressAndIsIsolatedPerProfile()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var series = await seed.AddWorkAsync(WorkMediaType.Series, "Dark Harbor");
        var episodes = new List<WorkEpisode>();
        foreach (var number in Enumerable.Range(1, 4))
        {
            var episode = await seed.AddEpisodeAsync(series, 1, number);
            await seed.AddVideoAsync(series, episode);
            episodes.Add(episode);
        }

        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Moon Empire", 2024);
        await seed.AddVideoAsync(movie, null, durationSeconds: 7200);
        var finished = await seed.AddWorkAsync(WorkMediaType.Movie, "Finished");
        await seed.AddVideoAsync(finished, null);

        await seed.SetProgressAsync(Alice, series, episodes[0], 0, null, completed: true, BaseTime);
        await seed.SetProgressAsync(Alice, series, episodes[1], 600_000, 1_440_000, completed: false, BaseTime.AddMinutes(1));
        await seed.SetProgressAsync(Alice, movie, null, 1_800_000, 7_200_000, completed: false, BaseTime.AddMinutes(2));
        await seed.SetProgressAsync(Alice, finished, null, 0, 6_000_000, completed: true, BaseTime.AddMinutes(3));
        await seed.SetProgressAsync(Bob, series, episodes[2], 0, null, completed: true, BaseTime);

        var alice = await ReadAsync(fixture, Alice);
        var bob = await ReadAsync(fixture, Bob);

        var aliceSeries = Entry(alice, series.Id);
        Assert.AreEqual(MediaBannerProgressState.InProgress, aliceSeries.Card.Progress?.State);
        Assert.AreEqual(2, aliceSeries.Card.Progress?.NextNumber, "Alice resumes episode 2.");
        Assert.AreEqual(25, aliceSeries.Card.Progress?.Percent, "One of four episodes is watched.");
        Assert.AreEqual(BaseTime.AddMinutes(1), aliceSeries.LastWatchedAt);
        Assert.AreEqual($"/Library/Series/{series.Id}", aliceSeries.Card.Progress?.NextUrl, "A Series card opens its detail page, which plays the next episode.");

        var bobSeries = Entry(bob, series.Id);
        Assert.AreEqual(4, bobSeries.Card.Progress?.NextNumber, "Bob continues after his own episode 3, whatever Alice watched.");
        Assert.AreEqual(BaseTime, bobSeries.LastWatchedAt);

        var aliceMovie = Entry(alice, movie.Id);
        Assert.AreEqual(MediaBannerProgressState.InProgress, aliceMovie.Card.Progress?.State);
        Assert.AreEqual(25, aliceMovie.Card.Progress?.Percent);
        Assert.AreEqual(90, aliceMovie.RemainingMinutes);
        Assert.AreEqual(MediaBannerProgressState.Completed, Entry(alice, finished.Id).Card.Progress?.State);

        var bobMovie = Entry(bob, movie.Id);
        Assert.AreEqual(MediaBannerProgressState.NotStarted, bobMovie.Card.Progress?.State);
        Assert.IsNull(bobMovie.LastWatchedAt);
        Assert.IsNull(bobMovie.RemainingMinutes);
    }

    [TestMethod]
    public async Task AnimeProgressIsReadFromMediaProgressNotFromLegacyEpisodeProgress()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var anime = await seed.AddAnimeAsync("Akatsuki", [(1, 1, true), (1, 2, true)]);
        fixture.Db.EpisodeProgress.Add(new EpisodeProgress { ProfileId = Alice, EpisodeId = anime.Episodes[0].Legacy.Id, IsCompleted = true, UpdatedAt = BaseTime });
        await fixture.Db.SaveChangesAsync();

        var withLegacyOnly = Entry(await ReadAsync(fixture, Alice), anime.Work.Id);
        await seed.SetProgressAsync(Alice, anime.Work, anime.Episodes[0].Canonical, 0, null, completed: true, BaseTime);
        var afterCanonicalWrite = Entry(await ReadAsync(fixture, Alice), anime.Work.Id);

        Assert.AreEqual(MediaBannerProgressState.NotStarted, withLegacyOnly.Card.Progress?.State, "The legacy row is not a Library source.");
        Assert.AreEqual(MediaBannerProgressState.InProgress, afterCanonicalWrite.Card.Progress?.State);
        Assert.AreEqual($"/Library/Episode/{anime.Episodes[1].Legacy.Id}", afterCanonicalWrite.Card.Progress?.NextUrl, "The player route is still the legacy episode route.");
    }

    [TestMethod]
    public async Task OpenRequestsAreProjectedOntoWorksThroughTheirTmdbIdentity()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var requestedMovie = await seed.AddWorkAsync(WorkMediaType.Movie, "Requested movie");
        var requestedSeries = await seed.AddWorkAsync(WorkMediaType.Series, "Requested series");
        var failedMovie = await seed.AddWorkAsync(WorkMediaType.Movie, "Failed movie");
        var transient = await seed.AddWorkAsync(WorkMediaType.Movie, "Looked at only");
        AddIdentity(fixture.Db, requestedMovie, "603");
        AddIdentity(fixture.Db, requestedSeries, "1399");
        AddIdentity(fixture.Db, failedMovie, "604");
        AddIdentity(fixture.Db, transient, "605");
        await fixture.Db.SaveChangesAsync();

        var store = new AcquisitionAccessStore(fixture.Db);
        await store.CreateAsync(Draft(MediaAcquisitionKind.Movie, "603"), Alice, AcquisitionRequestStatus.Searching, "owner", CancellationToken.None);
        await store.CreateAsync(Draft(MediaAcquisitionKind.Tv, "1399"), Bob, AcquisitionRequestStatus.Pending, "owner", CancellationToken.None);
        await store.CreateAsync(Draft(MediaAcquisitionKind.Movie, "604"), Alice, AcquisitionRequestStatus.Failed, "owner", CancellationToken.None);

        var read = await ReadAsync(fixture, Alice);

        CollectionAssert.AreEquivalent(
            new[] { requestedMovie.Id, requestedSeries.Id },
            read.Entries.Select(x => x.WorkId).ToArray(),
            "A Work is in the Library through an open request; a failed request or a candidate that was only viewed is not.");
        var movieEntry = Entry(read, requestedMovie.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Searching, movieEntry.Card.Availability?.Request);
        Assert.AreEqual(LibraryAvailabilityState.Requested, LibraryBrowse.IndicatorOf(movieEntry));
        Assert.AreEqual(AcquisitionRequestStatus.Pending, Entry(read, requestedSeries.Id).Card.Availability?.Request, "Requests of other profiles are part of the shared lifecycle.");
    }

    [TestMethod]
    public async Task TheMediaTypeScopeAndTheFiltersAreBoundToTheSameRead()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        await seed.AddAnimeAsync("Akatsuki", [(1, 1, true)]);
        var series = await seed.AddWorkAsync(WorkMediaType.Series, "Dark Harbor", 2021);
        await seed.AddVideoAsync(series, await seed.AddEpisodeAsync(series, 1, 1), audio: ["ger"]);
        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Moon Empire", 2021);
        await seed.AddVideoAsync(movie, null, audio: ["eng"]);
        var entries = (await ReadAsync(fixture, Alice)).Entries;
        var preference = LibraryLanguagePreference.None;

        var movies = LibraryBrowse.Apply(entries, new LibraryBrowseQuery { MediaType = WorkMediaType.Movie }, preference);
        var germanSeries = LibraryBrowse.Apply(entries, new LibraryBrowseQuery { MediaType = WorkMediaType.Series, AudioLanguage = "de" }, preference);
        var germanMovies = LibraryBrowse.Apply(entries, new LibraryBrowseQuery { MediaType = WorkMediaType.Movie, AudioLanguage = "de" }, preference);
        var year2021 = LibraryBrowse.Apply(entries, new LibraryBrowseQuery { Year = 2021 }, preference);

        Assert.AreEqual(movie.Id, Assert.ContainsSingle(movies).WorkId);
        Assert.AreEqual(series.Id, Assert.ContainsSingle(germanSeries).WorkId);
        Assert.AreEqual(0, germanMovies.Count);
        Assert.AreEqual(2, year2021.Count, "Without a scope every video type is filtered together.");
        Assert.AreEqual(3, LibraryBrowse.Facets([.. LibraryBrowse.Scope(entries, new LibraryBrowseQuery())], preference).Availability[LibraryAvailabilityState.Complete]);
    }

    [TestMethod]
    public async Task APageLoadIssuesAnEqualBoundedNumberOfCommandsWhateverTheTitleCount()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var counter = new CommandCounter();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(fixture.Db.Database.GetConnectionString())
            .AddInterceptors(counter)
            .Options);
        var seed = new LibraryCanonicalSeed(fixture.Db);

        await AddTitlesAsync(seed, "first", 1);
        await new LibraryMediaCardQuery(db).GetEntriesAsync(Alice, AllVideo, CancellationToken.None);
        var single = counter.Count;

        await AddTitlesAsync(seed, "more", 6);
        counter.Count = 0;
        var read = await new LibraryMediaCardQuery(db).GetEntriesAsync(Alice, AllVideo, CancellationToken.None);

        Assert.AreEqual(21, read.Entries.Count, "Seven titles of each media type.");
        Assert.AreEqual(single, counter.Count, "The grid must not issue commands per title.");
        Assert.IsTrue(single <= 12, $"Expected at most twelve commands, saw {single}.");
    }

    [TestMethod]
    public async Task TheBackfillBridgesEveryLegacyAnimeAndEpisodeWhetherOrNotItHasAFile()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var directory = Path.Combine(Path.GetTempPath(), $"jularr-library-backfill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var withFile = await fixture.AddAnimeAsync("with-file");
            var episodeWithFile = await fixture.AddEpisodeAsync(withFile, 1, 1, withMedia: false);
            await fixture.AddEpisodeAsync(withFile, 1, 2, withMedia: false);
            var emptyAnime = await fixture.AddAnimeAsync("without-episodes");
            var path = Path.Combine(directory, "with-file-s01e01.mkv");
            await File.WriteAllBytesAsync(path, [1]);
            var root = new LibraryRoot { Name = "Anime", Path = directory };
            fixture.Db.Add(root);
            fixture.Db.Add(new StoredFile { LibraryRootId = root.Id, EpisodeId = episodeWithFile.Id, Path = path, SizeBytes = 1, LastWriteTimeUtc = DateTime.UtcNow });
            await fixture.Db.SaveChangesAsync();

            var backfill = CreateBackfill(fixture.Db);
            Assert.AreEqual(1, await backfill.BackfillLegacyAnimeAsync(libraryRootId: null, CancellationToken.None));
            Assert.AreEqual(0, await backfill.BackfillLegacyAnimeAsync(libraryRootId: null, CancellationToken.None), "A second run has nothing left to do.");

            var links = await fixture.Db.WorkSourceLinks.AsNoTracking().ToListAsync();
            Assert.AreEqual(2, links.Count(x => x.SourceKind == WorkSourceKind.Anime), "An Anime without any episode is bridged too.");
            Assert.AreEqual(2, links.Count(x => x.SourceKind == WorkSourceKind.Episode), "An episode without a file is bridged too.");
            var workId = links.Single(x => x.SourceKind == WorkSourceKind.Anime && x.SourceId == withFile.Id).WorkId;
            Assert.AreEqual(2, await fixture.Db.WorkEpisodes.CountAsync(x => x.WorkId == workId));
            Assert.AreEqual(1, await fixture.Db.MediaAssets.CountAsync(x => x.WorkId == workId && x.WorkEpisodeId != null));
            Assert.IsTrue(links.Any(x => x.SourceKind == WorkSourceKind.Anime && x.SourceId == emptyAnime.Id));

            var read = await new LibraryMediaCardQuery(fixture.Db).GetEntriesAsync(Alice, [WorkMediaType.Anime], CancellationToken.None);
            var entry = read.Entries.Single(x => x.WorkId == workId);
            Assert.AreEqual(1, entry.PlayableUnits);
            Assert.AreEqual(1, entry.MissingUnits, "The episode without a file stays a known, missing unit.");
            Assert.AreEqual(2, read.Entries.Count, "Both legacy Anime keep their Library card.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task TheBackfillSkipsAFileThatIsMissingOnDiskAndKeepsBridgingTheRest()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var directory = Path.Combine(Path.GetTempPath(), $"jularr-library-stale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var anime = await fixture.AddAnimeAsync("unmounted");
            var present = await fixture.AddEpisodeAsync(anime, 1, 1, withMedia: false);
            var stale = await fixture.AddEpisodeAsync(anime, 1, 2, withMedia: false);
            var presentPath = Path.Combine(directory, "e1.mkv");
            await File.WriteAllBytesAsync(presentPath, [1]);
            var root = new LibraryRoot { Name = "Anime", Path = directory };
            fixture.Db.Add(root);
            fixture.Db.Add(new StoredFile { LibraryRootId = root.Id, EpisodeId = present.Id, Path = presentPath, SizeBytes = 1, LastWriteTimeUtc = DateTime.UtcNow });
            fixture.Db.Add(new StoredFile { LibraryRootId = root.Id, EpisodeId = stale.Id, Path = Path.Combine(directory, "gone.mkv"), SizeBytes = 1, LastWriteTimeUtc = DateTime.UtcNow });
            await fixture.Db.SaveChangesAsync();

            var backfill = CreateBackfill(fixture.Db);
            Assert.AreEqual(1, await backfill.BackfillLegacyAnimeAsync(libraryRootId: null, CancellationToken.None), "Startup must not fail on a file of unmounted media.");
            Assert.AreEqual(0, await backfill.BackfillLegacyAnimeAsync(libraryRootId: null, CancellationToken.None), "A rerun skips the missing file again.");

            Assert.AreEqual(1, await fixture.Db.MediaAssets.CountAsync());
            Assert.AreEqual(2, await fixture.Db.WorkEpisodes.CountAsync(), "The episode of the missing file is still bridged as a known unit.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ARequestedOrCreatedAnimeIsInTheLibraryWithoutAScan()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = new Anime { Key = "fresh", Title = "Fresh Anime" };
        fixture.Db.Add(anime);
        await fixture.Db.SaveChangesAsync();
        var bridge = new LegacyWorkBridge(fixture.Db, new WorkService(fixture.Db), new WorkStructureService(fixture.Db));

        await bridge.EnsureWorkForAnimeAsync(anime, CancellationToken.None);

        var entry = Assert.ContainsSingle((await ReadAsync(fixture, Alice)).Entries);
        Assert.AreEqual($"/Library/Anime/{anime.Id}", entry.Card.Href);
        Assert.AreEqual(0, entry.PlayableUnits);
    }

    private static Task<LibraryEntries> ReadAsync(EpisodeFlowFixture fixture, string profile) =>
        new LibraryMediaCardQuery(fixture.Db).GetEntriesAsync(profile, AllVideo, CancellationToken.None);

    private static LibraryCardEntry Entry(LibraryEntries read, Guid workId) => read.Entries.Single(x => x.WorkId == workId);

    private static void AssertAvailability(
        LibraryCardEntry entry,
        int playable,
        int missing,
        LibraryAvailabilityState matches,
        LibraryAvailabilityState? indicator)
    {
        Assert.AreEqual(playable, entry.PlayableUnits, entry.Card.Title);
        Assert.AreEqual(missing, entry.MissingUnits, entry.Card.Title);
        Assert.IsTrue(LibraryBrowse.Matches(entry, matches), entry.Card.Title);
        Assert.AreEqual(indicator, LibraryBrowse.IndicatorOf(entry), entry.Card.Title);
    }

    private static void AddIdentity(AppDbContext db, Work work, string tmdbId) =>
        db.WorkExternalIdentities.Add(new WorkExternalIdentity { WorkId = work.Id, MediaType = work.MediaType, Provider = ProviderKeys.Tmdb, ExternalId = tmdbId, IsPrimary = true });

    private static AcquisitionRequestDraft Draft(MediaAcquisitionKind kind, string tmdbId) =>
        new(kind, ProviderKeys.Tmdb, tmdbId, $"Title {tmdbId}", null, null);

    private static async Task AddTitlesAsync(LibraryCanonicalSeed seed, string prefix, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var anime = await seed.AddAnimeAsync($"{prefix}-anime-{index}", [(1, 1, true), (1, 2, false)], audio: ["jpn"], subtitles: ["eng"]);
            await seed.SetProgressAsync(Alice, anime.Work, anime.Episodes[0].Canonical, 0, null, completed: true, BaseTime);

            var series = await seed.AddWorkAsync(WorkMediaType.Series, $"{prefix}-series-{index}");
            await seed.AddVideoAsync(series, await seed.AddEpisodeAsync(series, 1, 1), audio: ["ger"]);
            await seed.AddEpisodeAsync(series, 1, 2);

            var movie = await seed.AddWorkAsync(WorkMediaType.Movie, $"{prefix}-movie-{index}");
            await seed.AddVideoAsync(movie, null, audio: ["eng"], durationSeconds: 5400);
            await seed.SetProgressAsync(Alice, movie, null, 900_000, 5_400_000, completed: false, BaseTime);
        }
    }

    private static CanonicalVideoStorageBackfillService CreateBackfill(AppDbContext db) =>
        new(db, new LegacyWorkBridge(db, new WorkService(db), new WorkStructureService(db)), new CanonicalMediaStorageService(db), NullLogger<CanonicalVideoStorageBackfillService>.Instance);

    internal sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
