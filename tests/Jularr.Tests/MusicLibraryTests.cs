using System.Net;
using System.Text;
using Jularr.Tests.Infrastructure;
using Jularr.Web.Data;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Music;
using Jularr.Web.Features.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class MusicLibraryTests
{
    private const string ArtistId = "056e4f3e-d505-4dad-8ec1-d04f521cbb56";
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task TheProviderReadsArtistsAlbumsAndTheTrackListOfTheEarliestOfficialRelease()
    {
        var requests = new List<string>();
        var provider = Provider(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            var path = request.RequestUri.AbsolutePath;
            return path.EndsWith("/artist/", StringComparison.Ordinal)
                ? Json("""{"artists":[{"id":"056e4f3e-d505-4dad-8ec1-d04f521cbb56","name":"Daft Punk","sort-name":"Daft Punk","country":"FR","type":"Group","disambiguation":"French duo"}]}""")
                : path.EndsWith("/release-group", StringComparison.Ordinal)
                    ? Json("""
                        {"release-group-count":3,"release-groups":[
                          {"id":"rg-1","title":"Homework","primary-type":"Album","secondary-types":[],"first-release-date":"1997-01-20"},
                          {"id":"rg-2","title":"Alive 2007","primary-type":"Album","secondary-types":["Live"],"first-release-date":"2007-11-19"},
                          {"id":"rg-3","title":"Random Access Memories","primary-type":"Album","first-release-date":"2013-05"}]}
                        """)
                    : Json("""
                        {"releases":[
                          {"id":"rel-late","date":"2014-01-01","media":[{"position":1,"tracks":[{"position":1,"title":"Late","length":1000,"recording":{"id":"rec-late"}}]}]},
                          {"id":"rel-early","date":"2013-05-17","media":[
                            {"position":1,"tracks":[{"position":1,"title":"Give Life Back to Music","length":274000,"recording":{"id":"rec-1"}},{"position":2,"title":"The Game of Love","length":321000,"recording":{"id":"rec-2"}}]},
                            {"position":2,"tracks":[{"position":1,"title":"Bonus","length":1,"recording":{"id":"rec-3"}}]}]},
                          {"id":"rel-empty","date":"2012-01-01","media":[]}]}
                        """);
        });

        var artists = await provider.SearchArtistsAsync("daft punk", 5, CancellationToken.None);
        var groups = await provider.ListReleaseGroupsAsync(ArtistId, CancellationToken.None);
        var tracks = await provider.GetTracksAsync("rg-3", CancellationToken.None);

        Assert.AreEqual("Daft Punk", artists.Single().Name);
        CollectionAssert.AreEqual(new[] { "Homework", "Alive 2007", "Random Access Memories" }, groups.Select(group => group.Title).ToArray());
        Assert.AreEqual(MusicAlbumType.Live, groups.Single(group => group.MusicBrainzId == "rg-2").Type);
        Assert.AreEqual(new DateTime(2013, 5, 1, 0, 0, 0, DateTimeKind.Utc), groups.Single(group => group.MusicBrainzId == "rg-3").ReleaseDate, "A partial date counts from its first day.");
        Assert.AreEqual("rel-early", tracks!.ReleaseId);
        CollectionAssert.AreEqual(new[] { (1, 1), (1, 2), (2, 1) }, tracks.Tracks.Select(track => (track.Disc, track.Number)).ToArray());
        Assert.IsTrue(requests.All(path => path.Contains("fmt=json", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AFailingProviderIsAnExceptionAndNeverAnEmptyDiscography()
    {
        var provider = Provider(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<MusicMetadataException>(() => provider.ListReleaseGroupsAsync(ArtistId, CancellationToken.None));
    }

    [TestMethod]
    public async Task AddingAnArtistCreatesOneAlbumWorkPerReleaseGroupAndARefreshNeverDuplicates()
    {
        await using var db = await CreateDbAsync();
        var fake = new FakeMusicProvider();
        var service = new MusicLibraryService(db, fake, new WorkService(db), MonitoringTestSupport.Commands(db), new FixedClock(Now));

        var artist = await service.AddArtistAsync(ArtistId, MusicMonitorMode.All, "owner", CancellationToken.None);
        await service.RefreshArtistAsync(artist.Id, CancellationToken.None);
        await service.AddArtistAsync(ArtistId.ToUpperInvariant(), MusicMonitorMode.All, "owner", CancellationToken.None);

        Assert.AreEqual(1, await db.MusicArtists.CountAsync());
        Assert.AreEqual(3, await db.MusicAlbums.CountAsync());
        Assert.AreEqual(3, await db.Works.CountAsync(work => work.MediaType == WorkMediaType.Music));
        Assert.AreEqual(3, (await MonitoringTestSupport.MonitoredMusicAsync(db)).Count, "An artist reaches every release of it, whatever its type.");
        Assert.IsTrue(await db.WorkExternalIdentities.AnyAsync(identity => identity.Provider == "musicbrainz" && identity.ExternalId == "rg-1" && identity.MediaType == WorkMediaType.Music));
        Assert.IsNotNull((await db.MusicArtists.SingleAsync()).LastRefreshedAt);
    }

    [TestMethod]
    public async Task FutureMonitoringOnlyWantsAlbumsAfterTheArtistWasAddedAndANewAlbumIsPickedUpByARefresh()
    {
        await using var db = await CreateDbAsync();
        var fake = new FakeMusicProvider();
        var service = new MusicLibraryService(db, fake, new WorkService(db), MonitoringTestSupport.Commands(db), new FixedClock(Now));
        var artist = await service.AddArtistAsync(ArtistId, MusicMonitorMode.Future, "owner", CancellationToken.None);

        Assert.AreEqual(0, (await MonitoringTestSupport.MonitoredMusicAsync(db)).Count);

        fake.Groups.Add(new MusicReleaseGroupSummary("rg-new", "Next Album", MusicAlbumType.Album, Now.AddDays(60), Now.AddDays(60).Year, false));
        await service.RefreshArtistAsync(artist.Id, CancellationToken.None);

        var next = await db.MusicAlbums.SingleAsync(album => album.MusicBrainzReleaseGroupId == "rg-new");
        CollectionAssert.AreEqual(new[] { next.WorkId }, (await MonitoringTestSupport.MonitoredMusicAsync(db)).ToArray(), "Only the album that appeared after the artist was added is wanted.");

        await service.SetArtistMonitorAsync(artist.Id, MusicMonitorMode.All, CancellationToken.None);
        Assert.AreEqual(4, await db.MusicAlbums.CountAsync(), "No album is lost when the mode changes.");
        Assert.AreEqual(4, (await MonitoringTestSupport.MonitoredMusicAsync(db)).Count);
        await service.SetAlbumMonitoredAsync(next.WorkId, false, CancellationToken.None);
        Assert.AreEqual(3, (await MonitoringTestSupport.MonitoredMusicAsync(db)).Count);
    }

    [TestMethod]
    public async Task TheTrackListIsStoredOnceAndAnUnknownListMeansNotYet()
    {
        await using var db = await CreateDbAsync();
        var fake = new FakeMusicProvider();
        var service = new MusicLibraryService(db, fake, new WorkService(db), MonitoringTestSupport.Commands(db), new FixedClock(Now));
        await service.AddArtistAsync(ArtistId, MusicMonitorMode.All, "owner", CancellationToken.None);
        var withTracks = (await db.MusicAlbums.SingleAsync(album => album.MusicBrainzReleaseGroupId == "rg-3")).WorkId;
        var withoutTracks = (await db.MusicAlbums.SingleAsync(album => album.MusicBrainzReleaseGroupId == "rg-1")).WorkId;

        Assert.IsTrue(await service.EnsureTracksAsync(withTracks, CancellationToken.None));
        Assert.IsTrue(await service.EnsureTracksAsync(withTracks, CancellationToken.None));
        Assert.IsFalse(await service.EnsureTracksAsync(withoutTracks, CancellationToken.None));

        Assert.AreEqual(2, await db.WorkTracks.CountAsync(track => track.WorkId == withTracks));
        Assert.AreEqual(1, fake.TrackReads["rg-3"], "A stored list is never read again.");
    }

    private static MusicBrainzProvider Provider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var clock = TimeProvider.System;
        var executor = new ProviderExecutor(new ProviderRateLimiter(), new ProviderHealthTracker(clock), clock, NullLogger<ProviderExecutor>.Instance);
        return new MusicBrainzProvider(new HttpClient(new StubHandler(responder)), executor);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<AppDbContext> CreateDbAsync()
    {
        var connection = TestPostgres.ResolveConnectionString($"Data Source=music-library-{Guid.NewGuid():N}.db");
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    internal sealed class FakeMusicProvider : IMusicMetadataProvider
    {
        public List<MusicReleaseGroupSummary> Groups { get; } =
        [
            new("rg-1", "Homework", MusicAlbumType.Album, new DateTime(1997, 1, 20, 0, 0, 0, DateTimeKind.Utc), 1997, false),
            new("rg-2", "Alive 2007", MusicAlbumType.Live, new DateTime(2007, 11, 19, 0, 0, 0, DateTimeKind.Utc), 2007, true),
            new("rg-3", "Random Access Memories", MusicAlbumType.Album, new DateTime(2013, 5, 17, 0, 0, 0, DateTimeKind.Utc), 2013, false)
        ];

        public Dictionary<string, int> TrackReads { get; } = [];

        public Task<IReadOnlyList<MusicArtistSummary>> SearchArtistsAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MusicArtistSummary>>([new(ArtistId, "Daft Punk", "Daft Punk", null, "FR", "Group")]);

        public Task<IReadOnlyList<MusicAlbumSearchHit>> SearchAlbumsAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MusicAlbumSearchHit>>([]);

        public Task<MusicArtistSummary?> GetArtistAsync(string musicBrainzId, CancellationToken cancellationToken) =>
            Task.FromResult<MusicArtistSummary?>(new MusicArtistSummary(ArtistId, "Daft Punk", "Daft Punk", null, "FR", "Group"));

        public Task<IReadOnlyList<MusicReleaseGroupSummary>> ListReleaseGroupsAsync(string artistMusicBrainzId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MusicReleaseGroupSummary>>([.. Groups]);

        public Task<MusicAlbumTracks?> GetTracksAsync(string releaseGroupMusicBrainzId, CancellationToken cancellationToken)
        {
            TrackReads[releaseGroupMusicBrainzId] = TrackReads.GetValueOrDefault(releaseGroupMusicBrainzId) + 1;
            return Task.FromResult(releaseGroupMusicBrainzId == "rg-3"
                ? new MusicAlbumTracks("rel", [new MusicTrackInfo(1, 1, "Give Life Back to Music", 274000, "rec-1"), new MusicTrackInfo(1, 2, "The Game of Love", 321000, "rec-2")])
                : null);
        }
    }
}
