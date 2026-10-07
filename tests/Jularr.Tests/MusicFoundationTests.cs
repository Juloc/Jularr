using Jularr.Tests.Infrastructure;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Music;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class MusicFoundationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow("Daft Punk - Random Access Memories (2013) [FLAC]", "FLAC")]
    [DataRow("Daft_Punk-Random_Access_Memories-WEB-2013-ENTiTLED", "UNKNOWN-UNKNOWN")]
    [DataRow("Daft Punk - Random Access Memories 2013 MP3 320kbps", "MP3-320")]
    [DataRow("Daft.Punk.Random.Access.Memories.2013.MP3.V0", "MP3-V0")]
    [DataRow("Daft Punk - Random Access Memories [MP3 256]", "MP3-256")]
    [DataRow("Daft Punk - Random Access Memories (M4A)", "AAC")]
    [DataRow("Daft Punk - Random Access Memories MP3", "MP3")]
    public void TheReleaseNameDeclaresTheQualityKey(string release, string expected)
    {
        var parsed = MusicReleaseParser.Instance.Parse(release);

        Assert.AreEqual(expected, ReleaseQuality.GetKey(parsed));
    }

    [TestMethod]
    public void TheReadableNameDropsSeparatorsAndTechnicalTags()
    {
        Assert.AreEqual("Daft Punk - Random Access Memories (2013)", MusicReleaseParser.ReadableName("Daft_Punk_-_Random_Access_Memories_(2013)_[FLAC]").Replace("  ", " "));
        Assert.AreEqual("Daft Punk Random Access Memories 2013", MusicReleaseParser.ReadableName("Daft.Punk.Random.Access.Memories.2013.MP3.320"));
    }

    [TestMethod]
    public void TheDefaultProfileTakesLosslessFirstAndLossyOnlyThroughTheTimedFallback()
    {
        var profile = MusicQualityProfiles.CreateDefaultMusic();
        SelectionCandidate Candidate(string id, string name) =>
            new(id, MusicReleaseParser.Instance.Parse(name), 300_000_000, "Indexer", 0, Now.AddDays(-1), ReleaseIdentityEvidence.Strong("Matches", "ok"), SelectionCoverage.Single);
        var flac = Candidate("flac", "Artist - Album (2020) [FLAC]");
        var mp3 = Candidate("mp3-320", "Artist - Album (2020) MP3 320");
        var weak = Candidate("mp3-256", "Artist - Album (2020) MP3 256");

        var first = ReleaseSelectionEngine.Select(profile, new SelectionContext(Now, Now), [weak, mp3, flac]);
        var later = ReleaseSelectionEngine.Select(profile, new SelectionContext(Now, Now.AddHours(-7)), [weak]);

        Assert.AreEqual("flac", first.Winner!.Candidate.Id);
        Assert.IsFalse(first.Ranked.Single(evaluation => evaluation.Candidate.Id == "mp3-256").IsSelectable, "A 256 kbit release waits for its fallback tier.");
        Assert.AreEqual(SelectionDecision.Temporary, later.Winner!.Decision);
        Assert.AreEqual(0, ReleaseScorer.ValidateProfile(profile).Count);
    }

    [TestMethod]
    public void MusicIsAKindWithItsOwnModuleCategoryAndWorkType()
    {
        Assert.AreEqual("music", AcquisitionAccessNames.Kind(MediaAcquisitionKind.Music));
        Assert.AreEqual(MediaAcquisitionKind.Music, AcquisitionAccessNames.ParseKind("music"));
        Assert.AreEqual(WorkMediaType.Music, AcquisitionAccessNames.WorkType(MediaAcquisitionKind.Music));
        Assert.AreEqual(InstanceModule.Music, AcquisitionInstanceModules.For(MediaAcquisitionKind.Music));
        Assert.AreEqual(InstanceModule.Music, InstanceModuleMedia.For(WorkMediaType.Music));
        Assert.AreEqual("music", Jularr.Web.Features.Acquisition.DownloadClients.DownloadClientSettings.CreateDefault("http://sab").CategoryFor(MediaAcquisitionKind.Music));
        Assert.IsTrue(new MediaAcquisitionRegistry([new MusicAcquisitionRegistration()]).Supports(MediaAcquisitionKind.Music));
    }

    [TestMethod]
    public void MusicSearchUsesTheMusicFunctionOnlyWhenArtistAndAlbumAreAdvertised()
    {
        var intent = new SearchIntent(MediaAcquisitionKind.Music, "Random Access Memories") { Creator = "Daft Punk", Year = 2013 };
        var capable = new IndexerCapabilities(Now, new Dictionary<IndexerSearchMode, string[]> { [IndexerSearchMode.Search] = ["q"], [IndexerSearchMode.Music] = ["q", "artist", "album", "year"] });

        var structured = SearchPlanner.Plan(intent, capable, SearchDepth.Normal);
        var textOnly = SearchPlanner.Plan(intent, null, SearchDepth.Normal);

        Assert.AreEqual(IndexerSearchMode.Music, structured[0].Mode);
        CollectionAssert.AreEqual(
            new[] { new KeyValuePair<string, string>("artist", "Daft Punk"), new("album", "Random Access Memories"), new("year", "2013") },
            structured[0].Parameters.ToArray());
        Assert.IsTrue(textOnly.All(query => query.Mode == IndexerSearchMode.Search));
        Assert.AreEqual("Daft Punk Random Access Memories", textOnly[0].Text);
        CollectionAssert.AreEqual(new[] { 3000, 3010, 3040 }, SearchPlanner.Categories(MediaAcquisitionKind.Music, new IndexerEntry(Guid.NewGuid(), "I", IndexerType.Newznab, true, 1, IndexerSettings.CreateDefault("http://i", IndexerType.Newznab), "k")).ToArray());
        Assert.IsFalse(SearchPlanner.Plan(intent, null, SearchDepth.Normal).Any(query => query.Text == "Random Access Memories" && !query.AnyCategory), "The album title alone is only tried by a deep search.");
        Assert.IsTrue(SearchPlanner.Plan(intent, null, SearchDepth.Deep).Any(query => query.Text == "Random Access Memories" && !query.AnyCategory));
    }

    [TestMethod]
    public async Task AnAlbumIsAWorkWithTracksAnArtistAndAFileAttachedToATrack()
    {
        await using var db = await CreateDbAsync();
        var artist = new MusicArtist { Name = "Daft Punk", SortName = "Daft Punk", MusicBrainzId = "056e4f3e-d505-4dad-8ec1-d04f521cbb56" };
        var work = new Work { MediaType = WorkMediaType.Music, CanonicalTitle = "Random Access Memories", Year = 2013 };
        db.MusicArtists.Add(artist);
        db.Works.Add(work);
        db.MusicAlbums.Add(new MusicAlbum { WorkId = work.Id, ArtistId = artist.Id, Type = MusicAlbumType.Album, MusicBrainzReleaseGroupId = "aa1c3a1a-0000-0000-0000-000000000001", Monitored = true });
        var track = new WorkTrack { WorkId = work.Id, Disc = 1, Number = 1, Title = "Give Life Back to Music" };
        db.WorkTracks.Add(track);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var version = new WorkVersion { WorkId = work.Id, VersionKey = "audio-file:/music/a.flac" };
        db.WorkVersions.Add(version);
        await db.SaveChangesAsync();
        db.MediaAssets.Add(new MediaAsset { WorkId = work.Id, WorkTrackId = track.Id, WorkVersionId = version.Id, Kind = MediaAssetKind.Audio });
        await db.SaveChangesAsync();

        Assert.AreEqual(1, await db.MediaAssets.CountAsync(asset => asset.WorkTrackId == track.Id));
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            await using var second = await CreateSecondContextAsync(db);
            second.WorkTracks.Add(new WorkTrack { WorkId = work.Id, Disc = 1, Number = 1, Title = "Duplicate position" });
            await second.SaveChangesAsync();
        });
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            await using var second = await CreateSecondContextAsync(db);
            second.MusicArtists.Add(new MusicArtist { Name = "Same id", SortName = "Same id", MusicBrainzId = artist.MusicBrainzId });
            await second.SaveChangesAsync();
        });
    }

    [TestMethod]
    public async Task AMusicLibraryRootIsAValidContentAssignment()
    {
        await using var db = await CreateDbAsync();
        var root = new LibraryRoot { Name = "Music", Path = "/media/music" };
        db.LibraryRoots.Add(root);
        await db.SaveChangesAsync();

        var routing = new Jularr.Web.Features.Storage.LibraryRootRoutingService(db);
        await routing.SetSupportedAsync(root.Id, LibraryContentType.Music, true);
        await routing.SetDefaultAsync(LibraryContentType.Music, root.Id);

        Assert.AreEqual(root.Id, (await routing.ResolveDefaultAsync(LibraryContentType.Music))!.LibraryRootId);
    }

    private static async Task<AppDbContext> CreateDbAsync()
    {
        var connection = TestPostgres.ResolveConnectionString($"Data Source=music-foundation-{Guid.NewGuid():N}.db");
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }

    private static Task<AppDbContext> CreateSecondContextAsync(AppDbContext first) =>
        Task.FromResult(new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(first.Database.GetConnectionString()!).Options));
}
