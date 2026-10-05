using System.Data.Common;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Progress;
using Jularr.Web.Ui;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jularr.Tests;

/// <summary>The read model behind the Movie and Series detail pages and the pure rules that turn it into a hero.</summary>
[TestClass]
public sealed class VideoDetailQueryTests
{
    private const string Alice = "alice";
    private const string Bob = "bob";
    private static readonly WorkMediaType[] AllVideo = [WorkMediaType.Anime, WorkMediaType.Series, WorkMediaType.Movie];

    private static VideoDetailQuery Query(AppDbContext db) => new(db, new AcquisitionAccessStore(db), new VideoProgressService(db), TimeProvider.System);

    private static readonly InstantPlayPolicy Playing = new(MediaTypeEnabled: true, AcquisitionEnabled: true, PlaybackEnabled: true, CanRequest: true, AutoApproves: false, AcquisitionReady: true);

    [TestMethod]
    public async Task LanguagesComeFromTheCanonicalTracksOfEachEpisodeAndAreNormalised()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var series = await seed.AddWorkAsync(WorkMediaType.Series, "Dark Harbor", 2021);
        var first = await seed.AddEpisodeAsync(series, 1, 1);
        var second = await seed.AddEpisodeAsync(series, 1, 2);
        await seed.AddVideoAsync(series, first, audio: ["ger", "jpn"], subtitles: ["eng"], durationSeconds: 1500);
        await seed.AddVideoAsync(series, second, audio: ["eng"]);

        var detail = (await Query(fixture.Db).GetAsync(Alice, series.Id, WorkMediaType.Series, AllVideo, CancellationToken.None))!;

        var one = detail.Episodes.Single(x => x.Id == first.Id);
        CollectionAssert.AreEquivalent(new[] { "de", "ja" }, one.Audio.ToArray());
        CollectionAssert.AreEquivalent(new[] { "en" }, one.Subtitles.ToArray());
        Assert.AreEqual(25, one.RuntimeMinutes);
        CollectionAssert.AreEquivalent(new[] { "en" }, detail.Episodes.Single(x => x.Id == second.Id).Audio.ToArray());
        CollectionAssert.AreEquivalent(new[] { "de", "en", "ja" }, detail.Audio.ToArray(), "The work's languages are the union of its episodes.");
    }

    [TestMethod]
    public async Task ProgressAndTheNextEpisodeBelongToTheProfileThatAsks()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var series = await seed.AddWorkAsync(WorkMediaType.Series, "Dark Harbor", 2021);
        var first = await seed.AddEpisodeAsync(series, 1, 1);
        var second = await seed.AddEpisodeAsync(series, 1, 2);
        await seed.AddVideoAsync(series, first);
        await seed.AddVideoAsync(series, second);
        await seed.SetProgressAsync(Alice, series, first, 0, 1_500_000, completed: true, DateTime.UtcNow);

        var alice = (await Query(fixture.Db).GetAsync(Alice, series.Id, WorkMediaType.Series, AllVideo, CancellationToken.None))!;
        var bob = (await Query(fixture.Db).GetAsync(Bob, series.Id, WorkMediaType.Series, AllVideo, CancellationToken.None))!;

        Assert.IsTrue(alice.Episodes.Single(x => x.Id == first.Id).IsWatched);
        var aliceAction = PrimaryActionResolver.Resolve(alice.Playback, Playing);
        Assert.AreEqual((PrimaryActionKind.Continue, second.Id), (aliceAction.Kind, aliceAction.WorkEpisodeId), "Alice continues with the next unfinished episode.");
        Assert.IsFalse(bob.Episodes.Any(x => x.IsWatched));
        var bobAction = PrimaryActionResolver.Resolve(bob.Playback, Playing);
        Assert.AreEqual((PrimaryActionKind.StartWatching, first.Id), (bobAction.Kind, bobAction.WorkEpisodeId), "Bob starts at the beginning.");
    }

    [TestMethod]
    public async Task APageLoadIssuesTheSameFewCommandsWhateverTheEpisodeCount()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var counter = new CommandCounter();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(fixture.Db.Database.GetConnectionString()).AddInterceptors(counter).Options);
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var small = await seed.AddWorkAsync(WorkMediaType.Series, "Small", 2020);
        await AddEpisodesAsync(seed, small, 3);
        var large = await seed.AddWorkAsync(WorkMediaType.Series, "Large", 2021);
        await AddEpisodesAsync(seed, large, 60);

        await Query(db).GetAsync(Alice, small.Id, WorkMediaType.Series, AllVideo, CancellationToken.None);
        var smallCount = counter.Count;
        counter.Count = 0;
        var detail = (await Query(db).GetAsync(Alice, large.Id, WorkMediaType.Series, AllVideo, CancellationToken.None))!;

        Assert.AreEqual(60, detail.Episodes.Count);
        Assert.AreEqual(smallCount, counter.Count, "The commands must not grow with the episodes.");
        Assert.IsTrue(smallCount <= 14, $"Expected at most fourteen commands, saw {smallCount}.");
    }

    private static async Task AddEpisodesAsync(LibraryCanonicalSeed seed, Work series, int count)
    {
        for (var number = 1; number <= count; number++)
        {
            var episode = await seed.AddEpisodeAsync(series, 1 + number / 25, number);
            if (number % 2 == 0)
            {
                await seed.AddVideoAsync(series, episode, audio: ["ger"], subtitles: ["eng"]);
            }
        }
    }

    [TestMethod]
    public async Task OnlyAWorkOfTheExpectedMediaTypeHasADetail()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Moon Empire", 2024);

        Assert.IsNotNull(await Query(fixture.Db).GetAsync(Alice, movie.Id, WorkMediaType.Movie, AllVideo, CancellationToken.None));
        Assert.IsNull(await Query(fixture.Db).GetAsync(Alice, movie.Id, WorkMediaType.Series, AllVideo, CancellationToken.None));
        Assert.IsNull(await Query(fixture.Db).GetAsync(Alice, Guid.NewGuid(), WorkMediaType.Movie, AllVideo, CancellationToken.None));
    }

    [TestMethod]
    public void EpisodesAreRequestableWhenNeitherAvailableNorOnItsWay()
    {
        HashSet<string> none = [];
        VideoDetailEpisode Episode(AnimeEpisodeAvailability state) => new(Guid.NewGuid(), 1, 1, null, state == AnimeEpisodeAvailability.Available, null, none, none, state, false, null);
        var all = Enum.GetValues<AnimeEpisodeAvailability>().Select(Episode).ToArray();

        var requestable = VideoDetailView.RequestableEpisodes(all);

        Assert.AreEqual(AnimeEpisodeAvailability.Unavailable, Assert.ContainsSingle(requestable).Availability);
    }

    // ---- What a request covers ----------------------------------------------------------------------------------------

    [TestMethod]
    public void TheRequestScopeDecidesWhichEpisodesItCovers()
    {
        var created = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var seasonId = Guid.NewGuid();
        var selected = Guid.NewGuid();
        var other = Guid.NewGuid();
        var aired = created.AddDays(-30);
        var upcoming = created.AddDays(7);

        VideoRequestPayload Payload(VideoRequestScope scope, bool future = false, Guid[]? seasons = null) => new(Guid.NewGuid(), "T", 2020, scope, [selected], future, SelectedSeasonIds: seasons);

        var all = new VideoRequestSelection(Payload(VideoRequestScope.AllCurrentAndFuture), created);
        Assert.IsTrue(all.Includes(other, null, aired) && all.Includes(other, null, upcoming));

        var futureOnly = new VideoRequestSelection(Payload(VideoRequestScope.FutureOnly), created);
        Assert.IsFalse(futureOnly.Includes(other, null, aired));
        Assert.IsTrue(futureOnly.Includes(other, null, upcoming));
        Assert.IsFalse(futureOnly.Includes(other, null, null), "An episode without an air date is not known to be future.");

        var custom = new VideoRequestSelection(Payload(VideoRequestScope.Custom, seasons: [seasonId]), created);
        Assert.IsTrue(custom.Includes(selected, null, aired), "A selected episode.");
        Assert.IsTrue(custom.Includes(other, seasonId, aired), "An episode of a selected season.");
        Assert.IsFalse(custom.Includes(other, Guid.NewGuid(), aired));
        Assert.IsFalse(custom.Includes(other, null, upcoming), "Future episodes only when future releases are included.");

        var withFuture = new VideoRequestSelection(Payload(VideoRequestScope.Custom, future: true), created);
        Assert.IsTrue(withFuture.Includes(other, null, upcoming));
        Assert.IsFalse(withFuture.Includes(other, null, aired));
    }

    private sealed class CommandCounter : DbCommandInterceptor
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
