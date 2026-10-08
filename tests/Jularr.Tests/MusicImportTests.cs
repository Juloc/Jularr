using Jularr.Web.Features.Monitoring;
using Jularr.Tests.Infrastructure;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Music;
using Jularr.Web.Features.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class MusicImportTests
{
    private const string Group = "rg-3";

    [TestMethod]
    public void FilesAreMatchedToTracksByNumberDiscAndTitleAndEveryTrackIsUsedOnce()
    {
        var tracks = new[]
        {
            Track(1, 1, "Give Life Back to Music"), Track(1, 2, "The Game of Love"), Track(2, 1, "Disc Two Opener"), Track(2, 2, "Disc Two Closer")
        };
        var files = new[]
        {
            AudioFile("/dl/CD1/01 - Give Life Back to Music.flac"),
            AudioFile("/dl/CD1/02. The Game of Love.flac"),
            AudioFile("/dl/CD2/01_Disc Two Opener.flac"),
            AudioFile("/dl/CD2/Daft Punk - Random Access Memories - 02 - Disc Two Closer.flac"),
            AudioFile("/dl/CD2/Bonus Hidden Song.flac"),
            AudioFile("/dl/cover.jpg")
        };

        var matches = MusicTrackMatcher.Match(files.Where(file => MusicTrackMatcher.IsAudio(file.Path)).ToArray(), tracks);

        Assert.AreEqual(5, matches.Count);
        CollectionAssert.AreEqual(
            new[] { (1, 1), (1, 2), (2, 1), (2, 2) },
            matches.Where(match => match.Track is not null).Select(match => (match.Track!.Disc, match.Track.Number)).OrderBy(pair => pair).ToArray());
        Assert.IsNull(matches.Single(match => match.File.Path.EndsWith("Bonus Hidden Song.flac", StringComparison.Ordinal)).Track);
    }

    [TestMethod]
    public void ANumberAloneIsEnoughOnASingleDiscAndOnSeveralDiscsTheDiscOrTheTitleMustAgree()
    {
        var single = new[] { Track(1, 1, "Alpha"), Track(1, 2, "Beta") };
        var multi = new[] { Track(1, 1, "Alpha"), Track(2, 1, "Gamma") };

        Assert.AreEqual("Beta", MusicTrackMatcher.Match([AudioFile("/dl/02 - something else.mp3")], single).Single().Track?.Title);
        var ambiguous = MusicTrackMatcher.Match([AudioFile("/dl/01 - Something else.mp3")], multi).Single();
        Assert.IsNull(ambiguous.Track, "Track 1 of an unknown disc is not guessed on a two-disc album.");
        Assert.AreEqual("Gamma", MusicTrackMatcher.Match([AudioFile("/dl/01 - Gamma.mp3")], multi).Single().Track?.Title, "The title settles which disc it is.");
    }

    [TestMethod]
    public async Task ADownloadedAlbumIsPlacedByTheRootsPolicyAndRecordedAsAudioAssetsOfItsTracks()
    {
        using var world = await World.CreateAsync(LibraryPlacementPolicy.Copy);
        var source = world.Download("Daft.Punk-Random.Access.Memories-2013-FLAC", ["01 - Give Life Back to Music.flac", "02 - The Game of Love.flac", "03 - Giorgio by Moroder.flac", "cover.jpg"]);

        var result = await world.ImportAsync(source);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition, result.Message);
        var folder = Path.Combine(world.LibraryPath, "Daft Punk", "Random Access Memories (2013)");
        CollectionAssert.AreEqual(
            new[] { "01 - Give Life Back to Music.flac", "02 - The Game of Love.flac", "03 - Giorgio by Moroder.flac" },
            Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        Assert.IsTrue(Directory.GetFiles(source).Any(path => path.EndsWith("01 - Give Life Back to Music.flac", StringComparison.Ordinal)), "A copy keeps the source.");
        Assert.AreEqual(3, await world.Db.MediaAssets.CountAsync(asset => asset.WorkId == world.WorkId && asset.Kind == MediaAssetKind.Audio && asset.WorkTrackId != null));
        Assert.AreEqual(3, await world.Db.StoredFiles.CountAsync(file => file.Path.StartsWith(folder)));
        Assert.AreEqual(MusicLinks.AlbumPath(world.WorkId), result.ResultUrl);
        Assert.AreEqual(ImportMode.Copy, result.Placement!.Mode);
    }

    [TestMethod]
    public async Task ImportingTheSameDownloadAgainNeverDuplicatesFilesOrRecords()
    {
        using var world = await World.CreateAsync(LibraryPlacementPolicy.Copy);
        var source = world.Download("album", ["01 - Give Life Back to Music.flac", "02 - The Game of Love.flac", "03 - Giorgio by Moroder.flac"]);

        await world.ImportAsync(source);
        var again = await world.ImportAsync(source);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, again.Disposition, again.Message);
        Assert.AreEqual(3, await world.Db.StoredFiles.CountAsync());
        Assert.AreEqual(3, await world.Db.MediaAssets.CountAsync(asset => asset.Kind == MediaAssetKind.Audio));
        Assert.AreEqual(3, await world.Db.WorkVersions.CountAsync(version => version.WorkId == world.WorkId));
    }

    [TestMethod]
    public async Task ACompleteLosslessReleaseReplacesALossyAlbumOnceItIsRecordedAndTheVersionsRecordTheirQuality()
    {
        using var world = await World.CreateAsync(LibraryPlacementPolicy.Copy);
        var lossy = world.Download("Daft.Punk-Random.Access.Memories-2013-MP3-320", ["01 - Give Life Back to Music.mp3", "02 - The Game of Love.mp3", "03 - Giorgio by Moroder.mp3"]);
        await world.ImportAsync(lossy);
        var installed = await new CanonicalMediaStorageService(world.Db).ListAudioFilesAsync(world.WorkId, CancellationToken.None);
        Assert.IsTrue(installed.All(file => file.Quality == "MP3-320"), "The import records the quality of the release.");

        var lossless = world.Download("Daft.Punk-Random.Access.Memories-2013-FLAC", ["01 - Give Life Back to Music.flac", "02 - The Game of Love.flac", "03 - Giorgio by Moroder.flac"]);
        var result = await world.ImportAsync(lossless);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition, result.Message);
        var folder = Path.Combine(world.LibraryPath, "Daft Punk", "Random Access Memories (2013)");
        CollectionAssert.AreEqual(
            new[] { "01 - Give Life Back to Music.flac", "02 - The Game of Love.flac", "03 - Giorgio by Moroder.flac" },
            Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray(),
            "The replaced files are gone from the album folder.");
        var after = await new CanonicalMediaStorageService(world.Db).ListAudioFilesAsync(world.WorkId, CancellationToken.None);
        Assert.HasCount(3, after);
        Assert.IsTrue(after.All(file => file.Quality == "FLAC" && file.Path.EndsWith(".flac", StringComparison.Ordinal)));
        Assert.AreEqual(3, await world.Db.StoredFiles.CountAsync(), "One canonical file per track after the upgrade.");
    }

    [TestMethod]
    public async Task APartialBetterReleaseNeverReplacesTheCompleteAlbumAndALesserOneOnlyFillsTheGaps()
    {
        using var world = await World.CreateAsync(LibraryPlacementPolicy.Copy);
        await world.ImportAsync(world.Download("Daft.Punk-Random.Access.Memories-2013-MP3-320", ["01 - Give Life Back to Music.mp3", "02 - The Game of Love.mp3", "03 - Giorgio by Moroder.mp3"]));

        var partial = await world.ImportAsync(world.Download("Daft.Punk-Random.Access.Memories-2013-FLAC", ["01 - Give Life Back to Music.flac", "02 - The Game of Love.flac"]));

        Assert.AreEqual(CompletedDownloadImportDisposition.RejectedRelease, partial.Disposition, partial.Message);
        StringAssert.Contains(partial.Message, "cannot replace the complete album");
        var folder = Path.Combine(world.LibraryPath, "Daft Punk", "Random Access Memories (2013)");
        Assert.AreEqual(3, Directory.GetFiles(folder, "*.mp3").Length, "The installed album is untouched.");
        Assert.AreEqual(0, Directory.GetFiles(folder, "*.flac").Length);

        // A release of equal or lower quality does not replace anything either: it only fills tracks that have no file.
        using var gap = await World.CreateAsync(LibraryPlacementPolicy.Copy);
        await gap.ImportAsync(gap.Download("Daft.Punk-Random.Access.Memories-2013-FLAC", ["01 - Give Life Back to Music.flac", "02 - The Game of Love.flac"]));
        var lesser = await gap.ImportAsync(gap.Download("Daft.Punk-Random.Access.Memories-2013-MP3-320", ["01 - Give Life Back to Music.mp3", "02 - The Game of Love.mp3", "03 - Giorgio by Moroder.mp3"]));

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, lesser.Disposition, lesser.Message);
        var gapFolder = Path.Combine(gap.LibraryPath, "Daft Punk", "Random Access Memories (2013)");
        CollectionAssert.AreEqual(
            new[] { "01 - Give Life Back to Music.flac", "02 - The Game of Love.flac", "03 - Giorgio by Moroder.mp3" },
            Directory.GetFiles(gapFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
    }

    [TestMethod]
    public async Task AMovePlacementRemovesTheSourceFilesOnlyAfterTheyAreRecorded()
    {
        using var world = await World.CreateAsync(LibraryPlacementPolicy.Move);
        var source = world.Download("album", ["01 - Give Life Back to Music.flac", "02 - The Game of Love.flac", "03 - Giorgio by Moroder.flac"]);

        var result = await world.ImportAsync(source);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition, result.Message);
        Assert.AreEqual(0, Directory.GetFiles(source, "*.flac").Length);
        Assert.AreEqual(3, await world.Db.StoredFiles.CountAsync());
    }

    [TestMethod]
    public async Task AMultiDiscAlbumKeepsTheDiscInTheFileNames()
    {
        using var world = await World.CreateAsync(LibraryPlacementPolicy.Copy, tracks:
        [
            Track(1, 1, "One"), Track(1, 2, "Two"), Track(2, 1, "Three"), Track(2, 2, "Four")
        ]);
        var source = world.Download("album", ["CD1/01 - One.flac", "CD1/02 - Two.flac", "CD2/01 - Three.flac", "CD2/02 - Four.flac"]);

        var result = await world.ImportAsync(source);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition, result.Message);
        CollectionAssert.AreEqual(
            new[] { "1-01 - One.flac", "1-02 - Two.flac", "2-01 - Three.flac", "2-02 - Four.flac" },
            Directory.GetFiles(Path.Combine(world.LibraryPath, "Daft Punk", "Random Access Memories (2013)")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
    }

    [TestMethod]
    public async Task AReleaseWithTooFewTracksOrNoAudioIsTheWrongReleaseAndNothingIsPlaced()
    {
        using var world = await World.CreateAsync(LibraryPlacementPolicy.Copy);
        var partial = world.Download("partial", ["01 - Give Life Back to Music.flac"]);
        var noAudio = world.Download("text", ["readme.txt", "cover.jpg"]);

        var tooFew = await world.ImportAsync(partial);
        var none = await world.ImportAsync(noAudio);

        Assert.AreEqual(CompletedDownloadImportDisposition.RejectedRelease, tooFew.Disposition);
        StringAssert.Contains(tooFew.Message, "1 of 3 tracks");
        Assert.AreEqual(CompletedDownloadImportDisposition.RejectedRelease, none.Disposition);
        Assert.AreEqual(MusicCompletedDownloadImportAdapter.NoAudioFileReason, none.Message);
        Assert.AreEqual(0, await world.Db.StoredFiles.CountAsync());
        Assert.IsFalse(Directory.Exists(Path.Combine(world.LibraryPath, "Daft Punk")));
    }

    [TestMethod]
    public async Task AnUnreachableSourceOrAMissingDefaultRootOnlyWaits()
    {
        using var world = await World.CreateAsync(LibraryPlacementPolicy.Copy);
        var missing = await world.ImportAsync(Path.Combine(world.Root, "not-mounted"));
        await world.Routing.SetDefaultAsync(LibraryContentType.Music, null);
        var noRoot = await world.ImportAsync(world.Download("album", ["01 - Give Life Back to Music.flac", "02 - The Game of Love.flac", "03 - Giorgio by Moroder.flac"]));

        Assert.AreEqual(CompletedDownloadImportDisposition.RetryLater, missing.Disposition);
        Assert.AreEqual(CompletedDownloadImportDisposition.RetryLater, noRoot.Disposition);
        StringAssert.Contains(noRoot.Message, "No default Music library root");
    }

    [TestMethod]
    public async Task AnAlbumWithoutAKnownTrackListIsImportedFromItsFileNames()
    {
        using var world = await World.CreateAsync(LibraryPlacementPolicy.Copy, tracks: []);
        var source = world.Download("album", ["01 - First Song.flac", "02 - Second Song.flac"]);

        var result = await world.ImportAsync(source);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition, result.Message);
        Assert.AreEqual(2, await world.Db.WorkTracks.CountAsync(track => track.WorkId == world.WorkId));
        CollectionAssert.AreEqual(new[] { "First Song", "Second Song" }, (await world.Db.WorkTracks.OrderBy(track => track.Number).Select(track => track.Title).ToListAsync()).ToArray());
    }

    [TestMethod]
    public async Task ADifferentFileAtTheDestinationNeedsReviewInsteadOfBeingOverwritten()
    {
        using var world = await World.CreateAsync(LibraryPlacementPolicy.Copy);
        var folder = Path.Combine(world.LibraryPath, "Daft Punk", "Random Access Memories (2013)");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "01 - Give Life Back to Music.flac"), "a different, older file");
        var source = world.Download("album", ["01 - Give Life Back to Music.flac", "02 - The Game of Love.flac", "03 - Giorgio by Moroder.flac"]);

        var result = await world.ImportAsync(source);

        Assert.AreEqual(CompletedDownloadImportDisposition.NeedsReview, result.Disposition);
        Assert.AreEqual("a different, older file", await File.ReadAllTextAsync(Path.Combine(folder, "01 - Give Life Back to Music.flac")));
        Assert.AreEqual(0, await world.Db.StoredFiles.CountAsync());
    }

    [TestMethod]
    public async Task TheInboxImportsAFolderThatNamesOneKnownAlbumAndLeavesOthersAlone()
    {
        using var world = await World.CreateAsync(LibraryPlacementPolicy.Copy);
        var inbox = Path.Combine(world.Root, "inbox");
        world.Download("inbox/Daft Punk - Random Access Memories (2013) [FLAC]", ["01 - Give Life Back to Music.flac", "02 - The Game of Love.flac", "03 - Giorgio by Moroder.flac"]);
        world.Download("inbox/Some Other Artist - Unknown Record", ["01 - Song.flac"]);

        var result = await world.Adapter.ImportInboxAsync(inbox, [], CancellationToken.None);

        Assert.AreEqual(1, result.Imported, result.Message);
        StringAssert.Contains(result.Message, "does not identify exactly one known album");
        Assert.AreEqual(3, await world.Db.MediaAssets.CountAsync(asset => asset.Kind == MediaAssetKind.Audio));
    }

    private static WorkTrack Track(int disc, int number, string title) => new() { Disc = disc, Number = number, Title = title };

    private static CompletedDownloadFile AudioFile(string path) => new(path, 1000);

    private sealed class World : IDisposable
    {
        private World(AppDbContext db, string root, string libraryPath, Guid workId, Guid rootId, MusicCompletedDownloadImportAdapter adapter, LibraryRootRoutingService routing, string requestExternalId, AcquisitionRequest request)
        {
            Db = db;
            Root = root;
            LibraryPath = libraryPath;
            WorkId = workId;
            RootId = rootId;
            Adapter = adapter;
            Routing = routing;
            Request = request;
        }

        public AppDbContext Db { get; }

        public string Root { get; }

        public string LibraryPath { get; }

        public Guid WorkId { get; }

        public Guid RootId { get; }

        public MusicCompletedDownloadImportAdapter Adapter { get; }

        public LibraryRootRoutingService Routing { get; }

        public AcquisitionRequest Request { get; }

        public static async Task<World> CreateAsync(LibraryPlacementPolicy policy, WorkTrack[]? tracks = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"jularr-music-{Guid.NewGuid():N}");
            var libraryPath = Path.Combine(root, "library");
            Directory.CreateDirectory(libraryPath);
            var connection = TestPostgres.ResolveConnectionString($"Data Source=music-import-{Guid.NewGuid():N}.db");
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);

            var libraryRoot = new LibraryRoot { Name = "Music", Path = libraryPath };
            db.LibraryRoots.Add(libraryRoot);
            var artist = new MusicArtist { Name = "Daft Punk", SortName = "Daft Punk", MusicBrainzId = "056e4f3e-d505-4dad-8ec1-d04f521cbb56", LastRefreshedAt = DateTime.UtcNow };
            var work = new Work { MediaType = WorkMediaType.Music, CanonicalTitle = "Random Access Memories", Year = 2013 };
            db.MusicArtists.Add(artist);
            db.Works.Add(work);
            db.MusicAlbums.Add(new MusicAlbum { WorkId = work.Id, ArtistId = artist.Id, MusicBrainzReleaseGroupId = Group, ReleaseDate = new DateTime(2013, 5, 17, 0, 0, 0, DateTimeKind.Utc) });
            db.WorkExternalIdentities.Add(new WorkExternalIdentity { WorkId = work.Id, MediaType = WorkMediaType.Music, Provider = "musicbrainz", ExternalId = Group, IsPrimary = true, Evidence = "test" });
            await db.SaveChangesAsync();
            await MonitoringTestSupport.Commands(db).SetAsync(MonitoringTargetKind.Work, work.Id, true, CancellationToken.None);

            var provider = new TrackProvider(tracks ?? [Track(1, 1, "Give Life Back to Music"), Track(1, 2, "The Game of Love"), Track(1, 3, "Giorgio by Moroder")]);
            var routing = new LibraryRootRoutingService(db);
            await routing.AssignDefaultAsync(LibraryContentType.Music, libraryRoot.Id, policy);
            var library = new MusicLibraryService(db, provider, new WorkService(db), MonitoringTestSupport.Commands(db), TimeProvider.System);
            var adapter = new MusicCompletedDownloadImportAdapter(
                db,
                library,
                routing,
                new LibraryRootAvailabilityService(db, new StorageAvailabilityCoordinator()),
                new FileSystemHardLinkCreator(),
                new CanonicalMediaStorageService(db),
                NullLogger<MusicCompletedDownloadImportAdapter>.Instance,
                new QualityProfileStore(new DirectoryInfo(Path.Combine(root, "profiles")), new MediaAcquisitionRegistry([new MusicAcquisitionRegistration()])));
            var payload = new MusicRequestPayload(work.Id, "Daft Punk", "Random Access Memories", 2013);
            var request = await new AcquisitionAccessStore(db).CreateAsync(
                new AcquisitionRequestDraft(MediaAcquisitionKind.Music, "musicbrainz", Group, "Random Access Memories", "Daft Punk", null, payload.Serialize()),
                "owner",
                AcquisitionRequestStatus.Downloading,
                "owner",
                CancellationToken.None);
            return new World(db, root, libraryPath, work.Id, libraryRoot.Id, adapter, routing, Group, request);
        }

        public string Download(string folder, IReadOnlyList<string> names)
        {
            var directory = Path.Combine(Root, "downloads", folder.Replace('/', Path.DirectorySeparatorChar));
            if (folder.StartsWith("inbox/", StringComparison.Ordinal))
            {
                directory = Path.Combine(Root, folder.Replace('/', Path.DirectorySeparatorChar));
            }

            foreach (var name in names)
            {
                var path = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                System.IO.File.WriteAllText(path, $"audio of {name}");
            }

            return directory;
        }

        public Task<CompletedDownloadImportResult> ImportAsync(string source) =>
            Adapter.ImportAsync(new CompletedDownloadImportRequest(Request, null, source), CancellationToken.None);

        public void Dispose()
        {
            Db.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class TrackProvider(WorkTrack[] tracks) : IMusicMetadataProvider
    {
        public Task<IReadOnlyList<MusicArtistSummary>> SearchArtistsAsync(string query, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MusicArtistSummary>>([]);

        public Task<IReadOnlyList<MusicAlbumSearchHit>> SearchAlbumsAsync(string query, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MusicAlbumSearchHit>>([]);

        public Task<MusicArtistSummary?> GetArtistAsync(string musicBrainzId, CancellationToken cancellationToken) => Task.FromResult<MusicArtistSummary?>(null);

        public Task<IReadOnlyList<MusicReleaseGroupSummary>> ListReleaseGroupsAsync(string artistMusicBrainzId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MusicReleaseGroupSummary>>([]);

        public Task<MusicAlbumTracks?> GetTracksAsync(string releaseGroupMusicBrainzId, CancellationToken cancellationToken) =>
            Task.FromResult(tracks.Length == 0 ? null : new MusicAlbumTracks("rel", [.. tracks.Select(track => new MusicTrackInfo(track.Disc, track.Number, track.Title, null, null))]));
    }
}
