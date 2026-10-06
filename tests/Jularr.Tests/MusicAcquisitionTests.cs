using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Music;
using Jularr.Web.Features.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class MusicAcquisitionTests
{
    private const string ArtistId = "056e4f3e-d505-4dad-8ec1-d04f521cbb56";
    private const string RamGroup = "rg-3";

    [TestMethod]
    public void TheJudgeAcceptsTheRequestedAlbumAndSeparatesOtherAlbumsKindsAndCollections()
    {
        var parser = MusicReleaseParser.Instance;
        ReleaseIdentityEvidence Judge(string name, string artist = "Daft Punk", string album = "Random Access Memories", int? year = 2013) =>
            MusicReleaseJudge.Judge(parser, artist, album, year, Release(name)).Evidence;

        Assert.AreEqual(IdentityConfidence.Exact, Judge("Daft Punk - Random Access Memories (2013) [FLAC]").Confidence);
        Assert.AreEqual(IdentityConfidence.Strong, Judge("Daft.Punk-Random.Access.Memories-WEB-FLAC-GRP").Confidence);
        Assert.AreEqual(IdentityConfidence.Exact, Judge("Daft Punk - Random Access Memories (Deluxe Edition) (2013) MP3 320").Confidence, "An edition tag is not another album.");
        Assert.AreEqual(IdentityConfidence.Strong, Judge("Daft Punk - Random Access Memories (Deluxe Edition) MP3 320").Confidence);
        Assert.AreEqual("WrongAlbum", Judge("Daft Punk - Homework (1997) [FLAC]").Code);
        Assert.AreEqual("WrongArtist", Judge("Random Access Memories (2013) [FLAC]").Code);
        Assert.AreEqual("WrongArtist", Judge("Daft Hunk - Random Access Memories (2013) [FLAC]").Code);
        Assert.AreEqual("DifferentRelease", Judge("Daft Punk - Random Access Memories Karaoke (2013) [FLAC]").Code);
        Assert.AreEqual("DifferentRelease", Judge("Daft Punk - Random Access Memories (Live) [FLAC]").Code);
        Assert.AreEqual(IdentityConfidence.Ambiguous, Judge("Daft Punk - Discography (1997-2013) [FLAC] Random Access Memories").Confidence);
        Assert.AreEqual(IdentityConfidence.Exact, Judge("Various Artists - Random Access Memories (2013) [FLAC]", "Various Artists").Confidence);
    }

    [TestMethod]
    public async Task MonitoringRequestsOnlyWhatIsMissingAndNeverOverridesTheLifecycle()
    {
        await using var host = await MusicHost.CreateAsync();
        var missing = await host.AddAlbumAsync("rg-a", "Homework", 1997, monitored: true);
        var unmonitored = await host.AddAlbumAsync("rg-b", "Alive 2007", 2007, monitored: false);
        var unreleased = await host.AddAlbumAsync("rg-c", "Future Album", DateTime.UtcNow.Year + 1, monitored: true, releaseDate: DateTime.UtcNow.AddDays(90));
        var owned = await host.AddAlbumAsync("rg-d", "Discovery", 2001, monitored: true);
        await host.AttachAudioAsync(owned, 1);

        var created = await host.Get<MusicMonitoringService>().EnsureRequestsAsync(CancellationToken.None);
        var again = await host.Get<MusicMonitoringService>().EnsureRequestsAsync(CancellationToken.None);

        Assert.AreEqual(1, created);
        Assert.AreEqual(0, again, "An open request is the Wanted state; nothing is requested twice.");
        var request = (await host.Requests.ListAsync(MediaAcquisitionKind.Music, null, openOnly: false, 10, CancellationToken.None)).Single();
        Assert.AreEqual("rg-a", request.ExternalId);
        Assert.AreEqual("Homework", request.Title);
        Assert.AreEqual("Daft Punk", request.Subtitle);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status);
        Assert.AreEqual("owner", request.RequestedByProfileId);
        Assert.AreEqual(missing, MusicRequestPayload.Of(request).WorkId);
        Assert.AreNotEqual(unmonitored, missing);
        Assert.AreNotEqual(unreleased, missing);

        await host.Requests.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Failed, "Gave up.", null, null, null, CancellationToken.None);
        Assert.AreEqual(0, await host.Get<MusicMonitoringService>().EnsureRequestsAsync(CancellationToken.None), "A request that gave up waits for the owner.");
    }

    [TestMethod]
    public async Task AWantedPassSearchesChoosesTheBestReleaseAndSubmitsItToSabnzbdInTheMusicCategoryOnce()
    {
        await using var host = await MusicHost.CreateAsync(
            "Daft Punk - Homework (1997) MP3 320",
            "Daft Punk - Homework (1997) [FLAC]",
            "Daft Punk - Discovery (2001) [FLAC]",
            "Daft Punk - Homework (1997) Live [FLAC]");
        await host.AddAlbumAsync("rg-a", "Homework", 1997, monitored: true);

        await host.ProcessAsync();
        await host.ProcessAsync();

        var request = (await host.Requests.ListAsync(MediaAcquisitionKind.Music, null, openOnly: false, 10, CancellationToken.None)).Single();
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, request.StatusMessage);
        var grab = host.Environment.Client.Grabs.Single();
        StringAssert.Contains(grab.NzbName!, "[FLAC]", "Lossless is preferred over the 320 kbit MP3.");
        StringAssert.Contains(grab.NzbName!, "Homework", "Releases of other albums and live releases are never taken.");
        Assert.AreEqual("music", grab.Category);
        Assert.AreEqual(MusicLinks.AlbumPath(MusicRequestPayload.Of(request).WorkId), request.ResultUrl);
    }

    [TestMethod]
    public async Task ADownloadedAlbumIsImportedThroughTheSharedDispatcherAndTheRequestCompletes()
    {
        await using var host = await MusicHost.CreateAsync("Daft Punk - Homework (1997) [FLAC]");
        var work = await host.AddAlbumAsync("rg-a", "Homework", 1997, monitored: true);
        await host.ProcessAsync();
        var request = (await host.Requests.ListAsync(MediaAcquisitionKind.Music, null, openOnly: false, 10, CancellationToken.None)).Single();
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, request.StatusMessage);

        host.CompleteInSabnzbd(request, "/downloads/music/Daft.Punk-Homework-1997-FLAC");
        await host.Operations.MarkSucceededAsync(request.OperationId!.Value, "Downloaded.");
        await host.ProcessAsync();
        await host.ProcessAsync();

        var done = await host.Requests.GetAsync(request.Id, CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, done!.Status, done.StatusMessage);
        Assert.AreEqual(1, host.Importer.Imports, "A repeated pass never imports the same download again.");
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
        Assert.AreEqual(0, await host.Get<MusicMonitoringService>().EnsureRequestsAsync(CancellationToken.None), "An album with files is not wanted any more.");
        Assert.IsTrue(await host.Get<MusicAcquisitionEngine>().HasAudioFilesAsync(work, CancellationToken.None));
    }

    [TestMethod]
    public async Task OnlyOtherAlbumsOrManualReviewReleasesMeanNothingIsGrabbedAndTheReasonIsExplained()
    {
        await using var host = await MusicHost.CreateAsync("Daft Punk - Discovery (2001) [FLAC]", "Daft Punk - Homework Karaoke (1997) [FLAC]");
        await host.AddAlbumAsync("rg-a", "Homework", 1997, monitored: true);

        await host.ProcessAsync();

        var request = (await host.Requests.ListAsync(MediaAcquisitionKind.Music, null, openOnly: false, 10, CancellationToken.None)).Single();
        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status);
        Assert.AreEqual(0, host.Environment.Client.Grabs.Count);
        StringAssert.Contains(request.StatusMessage!, "none is the requested album");
    }

    [TestMethod]
    public async Task ALossyReleaseWaitsForItsFallbackTierAndThenIsTakenAsTemporary()
    {
        await using var host = await MusicHost.CreateAsync("Daft Punk - Homework (1997) MP3 256");
        await host.AddAlbumAsync("rg-a", "Homework", 1997, monitored: true);

        await host.ProcessAsync();
        var first = (await host.Requests.ListAsync(MediaAcquisitionKind.Music, null, openOnly: false, 10, CancellationToken.None)).Single();
        Assert.AreEqual(0, host.Environment.Client.Grabs.Count);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, first.Status);

        host.Clock.Advance(TimeSpan.FromHours(7));
        var payload = MusicRequestPayload.Of(first) with { NextSearchUtc = host.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1) };
        await host.Requests.UpdatePayloadAsync(first.Id, payload.Serialize(), CancellationToken.None);
        await host.ProcessAsync();

        Assert.AreEqual(1, host.Environment.Client.Grabs.Count, "After its wait, the lower quality is acceptable.");
    }

    [TestMethod]
    public async Task ManualSearchListsEveryReleaseWithItsVerdictAndHowItWasFound()
    {
        await using var host = await MusicHost.CreateAsync("Daft Punk - Homework (1997) [FLAC]", "Daft Punk - Discovery (2001) [FLAC]", "Daft Punk - Homework Live (1997) [FLAC]", "Daft Punk - Homework (1997) MP3 320");
        var work = await host.AddAlbumAsync("rg-a", "Homework", 1997, monitored: false);

        var result = await host.Get<MusicManualSearchService>().SearchAsync(work, refresh: true, Jularr.Web.Features.Acquisition.Search.SearchDepth.Normal, CancellationToken.None);

        Assert.AreEqual(4, result!.Candidates.Count);
        var flac = result.Candidates.Single(candidate => candidate.Title.EndsWith("(1997) [FLAC]", StringComparison.Ordinal) && !candidate.Title.Contains("Live"));
        Assert.IsTrue(flac.CanGrab);
        Assert.AreEqual("FLAC", flac.Quality);
        Assert.IsTrue(flac.Provenance.Count > 0, "Every candidate keeps the query that found it.");
        Assert.IsFalse(result.Candidates.Single(candidate => candidate.Title.Contains("Discovery")).CanGrab, "Another album is rejected.");
        Assert.IsFalse(result.Candidates.Single(candidate => candidate.Title.Contains("Live")).CanGrab, "A live release is not the studio album.");
        Assert.AreEqual(flac.Identity, result.Candidates.First(candidate => candidate.CanGrab).Identity, "The best release is listed before the lossy one.");
        Assert.IsNotNull(result.WinnerReason);
        Assert.AreEqual(0, host.Environment.Client.Grabs.Count, "Searching never grabs.");
    }

    [TestMethod]
    public async Task ManualGrabSendsTheSelectedReleaseOnceAndRefusesWhatIsNotEligible()
    {
        await using var host = await MusicHost.CreateAsync("Daft Punk - Homework (1997) [FLAC]", "Daft Punk - Homework (1997) MP3 320", "Daft Punk - Discovery (2001) [FLAC]");
        var work = await host.AddAlbumAsync("rg-a", "Homework", 1997, monitored: false);
        var service = host.Get<MusicManualSearchService>();
        var listed = await service.SearchAsync(work, refresh: true, Jularr.Web.Features.Acquisition.Search.SearchDepth.Normal, CancellationToken.None);
        var mp3 = listed!.Candidates.Single(candidate => candidate.Quality == "MP3-320").Identity;
        var wrong = listed.Candidates.Single(candidate => candidate.Title.Contains("Discovery")).Identity;

        var refused = await service.GrabAsync(work, "owner", wrong, CancellationToken.None);
        var first = await service.GrabAsync(work, "owner", mp3, CancellationToken.None);
        var again = await service.GrabAsync(work, "owner", mp3, CancellationToken.None);
        var unknown = await service.GrabAsync(Guid.NewGuid(), "owner", mp3, CancellationToken.None);

        Assert.AreEqual(MusicGrabStatus.NotAvailable, refused.Status);
        Assert.AreEqual(MusicGrabStatus.Submitted, first.Status, first.Message);
        Assert.AreEqual(MusicGrabStatus.AlreadySubmitted, again.Status);
        Assert.AreEqual(MusicGrabStatus.NotFound, unknown.Status);
        var grab = host.Environment.Client.Grabs.Single();
        StringAssert.Contains(grab.NzbName!, "MP3 320", "The owner's choice is grabbed, not the automatic favourite.");
        Assert.AreEqual("music", grab.Category);
        var request = (await host.Requests.ListAsync(MediaAcquisitionKind.Music, null, openOnly: false, 10, CancellationToken.None)).Single();
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);
        CollectionAssert.Contains((MusicRequestPayload.Of(request).TriedReleases ?? []).ToList(), mp3, "A manual grab is recorded as tried like an automatic one.");
    }

    [TestMethod]
    public async Task SearchNowCreatesTheRequestRunsItAndNeverGrabsTwice()
    {
        await using var host = await MusicHost.CreateAsync("Daft Punk - Homework (1997) [FLAC]");
        var work = await host.AddAlbumAsync("rg-a", "Homework", 1997, monitored: false);
        var service = host.Get<MusicManualSearchService>();

        var first = await service.SearchNowAsync(work, "owner", CancellationToken.None);
        var second = await service.SearchNowAsync(work, "owner", CancellationToken.None);

        Assert.IsNotNull(first);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
        Assert.AreEqual(1, (await host.Requests.ListAsync(MediaAcquisitionKind.Music, null, openOnly: false, 10, CancellationToken.None)).Count);
        Assert.IsNotNull(second);
        Assert.IsNull(await service.SearchNowAsync(Guid.NewGuid(), "owner", CancellationToken.None));
    }

    [TestMethod]
    public async Task TheAdminQueryDerivesAlbumStatesFromFilesMonitoringAndTheRequest()
    {
        await using var host = await MusicHost.CreateAsync();
        var unmonitored = await host.AddAlbumAsync("rg-1", "Unmonitored", 1990, monitored: false);
        var missing = await host.AddAlbumAsync("rg-2", "Missing", 1991, monitored: true);
        var available = await host.AddAlbumAsync("rg-3", "Available", 1992, monitored: true);
        var requested = await host.AddAlbumAsync("rg-4", "Requested", 1993, monitored: true);
        var failed = await host.AddAlbumAsync("rg-5", "Failed", 1994, monitored: true);
        var partial = await host.AddAlbumAsync("rg-6", "Partial", 1995, monitored: true);
        await host.AttachAudioAsync(available, 1);
        await host.AttachAudioAsync(partial, 1);
        host.Environment.Db.WorkTracks.Add(new WorkTrack { WorkId = partial, Number = 2, Title = "Second" });
        await host.Environment.Db.SaveChangesAsync();
        await host.Requests.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Music, "musicbrainz", "rg-4", "Requested", "Daft Punk", null), "owner", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        var failure = await host.Requests.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Music, "musicbrainz", "rg-5", "Failed", "Daft Punk", null), "owner", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        await host.Requests.UpdateStatusAsync(failure.Id, AcquisitionRequestStatus.Failed, "Gave up.", null, null, null, CancellationToken.None);

        var artistId = (await host.Environment.Db.MusicArtists.SingleAsync()).Id;
        var view = await host.Get<MusicAdminQuery>().GetArtistAsync(artistId, CancellationToken.None);
        var states = view!.Albums.ToDictionary(album => album.WorkId, album => album.State);
        var artists = await host.Get<MusicAdminQuery>().ListArtistsAsync(CancellationToken.None);

        Assert.AreEqual(MusicAlbumState.Unmonitored, states[unmonitored]);
        Assert.AreEqual(MusicAlbumState.Missing, states[missing]);
        Assert.AreEqual(MusicAlbumState.Available, states[available]);
        Assert.AreEqual(MusicAlbumState.Requested, states[requested]);
        Assert.AreEqual(MusicAlbumState.Failed, states[failed]);
        Assert.AreEqual(MusicAlbumState.Partial, states[partial]);
        Assert.AreEqual((6, 5, 2), (artists.Single().Albums, artists.Single().Monitored, artists.Single().Available));
    }

    private static ProwlarrReleaseCandidate Release(string title) =>
        new(title, "Music test indexer", 1, "usenet", 400L * 1024 * 1024, null, null, DateTimeOffset.UtcNow, 0, 1, title, null, AnimeReleaseParser.Parse(title), [], new Uri($"https://indexer.invalid/download/{Uri.EscapeDataString(title)}"), null);

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan span) => now += span;
    }

    private sealed class MusicHost : IAsyncDisposable
    {
        private readonly ServiceProvider services;

        private MusicHost(SabnzbdTestEnvironment environment, ServiceProvider services, RecordingMusicImporter importer, ManualClock clock)
        {
            Environment = environment;
            this.services = services;
            Importer = importer;
            Clock = clock;
        }

        public SabnzbdTestEnvironment Environment { get; }

        public RecordingMusicImporter Importer { get; }

        public ManualClock Clock { get; }

        public OperationStore Operations => new(Environment.Db);

        public AcquisitionAccessStore Requests => new(Environment.Db);

        public T Get<T>() where T : notnull => services.GetRequiredService<T>();

        public static async Task<MusicHost> CreateAsync(params string[] releases)
        {
            var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
            var directory = environment.Directory;
            var db = environment.Db;
            var clients = environment.NewDownloadClientStore();
            var client = (await clients.LoadAllAsync()).First();
            await clients.SaveAsync(client with { Settings = DownloadClientSettings.CreateDefault(client.Settings.BaseUrl) });
            var indexerStore = new IndexerStore(environment.Protection, directory);
            await indexerStore.SaveAsync(new IndexerEntry(Guid.NewGuid(), "Music test indexer", IndexerType.Newznab, true, 1, new IndexerSettings("https://indexer.invalid", [3000], [], 100), "key"));
            var indexer = new FixedVideoIndexer([.. releases.Select(Release)]);
            var coordinator = new IndexerSearchCoordinator(
                new Dictionary<IndexerType, IIndexer> { [IndexerType.Newznab] = indexer },
                indexerStore,
                new AcquisitionHealthStore(directory),
                NullLogger<IndexerSearchCoordinator>.Instance);
            var registry = new MediaAcquisitionRegistry([new MusicAcquisitionRegistration()]);
            var importer = new RecordingMusicImporter(db);
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            var services = new ServiceCollection()
                .AddSingleton(db)
                .AddSingleton<TimeProvider>(clock)
                .AddSingleton(coordinator)
                .AddSingleton(registry)
                .AddSingleton(new QualityProfileStore(new DirectoryInfo(Path.Combine(directory.FullName, "quality-profiles")), registry))
                .AddSingleton(_ => environment.NewDownloadClientStore())
                .AddSingleton(_ => environment.NewSubmissionService())
                .AddSingleton<IDownloadClient>(new SabnzbdDownloadClient(environment.Client))
                .AddSingleton(new AnimeImportSettingsStore(directory.FullName))
                .AddSingleton(_ => new AcquisitionAccessStore(db))
                .AddSingleton(new CurrentAccountContext(new FixedAccessor(new DefaultHttpContext { User = VideoAcquisitionTestHost.OwnerPrincipal() })))
                .AddSingleton<ReleaseRequestTracker>()
                .AddSingleton<IJularrEventPublisher, RecordingEventPublisher>()
                .AddSingleton<IMediaCapabilityService>(new MediaCapabilityService(new MediaCapabilityStore(directory.FullName)))
                .AddSingleton(new AcquisitionRequestSettingsStore(directory.FullName))
                .AddSingleton<AcquisitionRequestService>()
                .AddSingleton<IMusicMetadataProvider, MusicLibraryTests.FakeMusicProvider>()
                .AddSingleton(new WorkService(db))
                .AddSingleton<MusicLibraryService>()
                .AddSingleton<MusicMonitoringService>()
                .AddSingleton<MusicAcquisitionEngine>()
                .AddSingleton<MusicManualSearchService>()
                .AddSingleton<MusicAdminQuery>()
                .AddSingleton<IAcquisitionRequestExecutor, MusicAcquisitionRequestExecutor>()
                .AddSingleton<IWantedRequestHandler, MusicWantedRequestHandler>()
                .AddSingleton<IWantedSource, MusicWantedSource>()
                .AddSingleton<ICompletedDownloadImportAdapter>(importer)
                .AddSingleton<CompletedDownloadDispatcher>()
                .AddSingleton<CompletedDownloadImportService>()
                .AddSingleton<ICompletedDownloadLocationResolver, CompletedDownloadLocationResolver>()
                .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
            return new MusicHost(environment, services.BuildServiceProvider(), importer, clock);
        }

        public async Task<Guid> AddAlbumAsync(string groupId, string title, int year, bool monitored, DateTime? releaseDate = null)
        {
            var db = Environment.Db;
            var artist = await db.MusicArtists.FirstOrDefaultAsync();
            if (artist is null)
            {
                artist = new MusicArtist { Name = "Daft Punk", SortName = "Daft Punk", MusicBrainzId = ArtistId, AddedByProfileId = "owner", LastRefreshedAt = DateTime.UtcNow };
                db.MusicArtists.Add(artist);
            }

            var work = new Work { MediaType = WorkMediaType.Music, CanonicalTitle = title, Year = year };
            db.Works.Add(work);
            db.WorkExternalIdentities.Add(new WorkExternalIdentity { WorkId = work.Id, MediaType = WorkMediaType.Music, Provider = "musicbrainz", ExternalId = groupId, IsPrimary = true, Evidence = "test" });
            db.MusicAlbums.Add(new MusicAlbum { WorkId = work.Id, ArtistId = artist.Id, Type = MusicAlbumType.Album, ReleaseDate = releaseDate ?? new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc), MusicBrainzReleaseGroupId = groupId, Monitored = monitored });
            await db.SaveChangesAsync();
            return work.Id;
        }

        public async Task AttachAudioAsync(Guid workId, int number)
        {
            var db = Environment.Db;
            var root = await db.LibraryRoots.FirstOrDefaultAsync() ?? db.LibraryRoots.Add(new LibraryRoot { Name = "Music", Path = "/music" }).Entity;
            var track = new WorkTrack { WorkId = workId, Number = number, Title = $"Track {number}" };
            var version = new WorkVersion { WorkId = workId, VersionKey = $"audio-file:{Guid.NewGuid():N}", Source = "test" };
            var asset = new MediaAsset { WorkId = workId, WorkTrackId = track.Id, WorkVersionId = version.Id, Kind = MediaAssetKind.Audio };
            db.AddRange(track, version, asset, new StoredFile { MediaAssetId = asset.Id, LibraryRootId = root.Id, Path = $"/music/{Guid.NewGuid():N}.flac", SizeBytes = 1000, LastWriteTimeUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        public Task<int> ProcessAsync() => WantedAcquisitionService.ProcessOnceAsync(services, Clock.GetUtcNow().UtcDateTime, CancellationToken.None);

        public void CompleteInSabnzbd(AcquisitionRequest request, string storagePath)
        {
            var operation = Operations.GetAsync(request.OperationId!.Value).GetAwaiter().GetResult()!;
            Environment.Client.History = new SabnzbdHistorySnapshot(
            [
                new SabnzbdHistoryJob(operation.ExternalId!, Path.GetFileName(storagePath), "Completed", null, storagePath, null, SabnzbdFailureKind.None, DateTimeOffset.UtcNow)
            ]);
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await Environment.DisposeAsync();
        }
    }

    private sealed class RecordingMusicImporter(AppDbContext db) : ICompletedDownloadImportAdapter
    {
        public MediaAcquisitionKind Kind => MediaAcquisitionKind.Music;

        public int Imports { get; private set; }

        public async Task<CompletedDownloadImportResult> ImportAsync(CompletedDownloadImportRequest request, CancellationToken cancellationToken)
        {
            Imports++;
            var payload = MusicRequestPayload.Of(request.Request!);
            var root = await db.LibraryRoots.FirstOrDefaultAsync(cancellationToken) ?? db.LibraryRoots.Add(new LibraryRoot { Name = "Music", Path = "/music" }).Entity;
            var track = new WorkTrack { WorkId = payload.WorkId, Number = 1, Title = "Imported" };
            var version = new WorkVersion { WorkId = payload.WorkId, VersionKey = $"audio-file:{Guid.NewGuid():N}", Source = "test" };
            var asset = new MediaAsset { WorkId = payload.WorkId, WorkTrackId = track.Id, WorkVersionId = version.Id, Kind = MediaAssetKind.Audio };
            db.AddRange(track, version, asset, new StoredFile { MediaAssetId = asset.Id, LibraryRootId = root.Id, Path = $"/music/{Guid.NewGuid():N}.flac", SizeBytes = 1000, LastWriteTimeUtc = DateTime.UtcNow });
            await db.SaveChangesAsync(cancellationToken);
            return CompletedDownloadImportResult.Completed("Imported.", resultUrl: null);
        }
    }
}
