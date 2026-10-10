using System.Net;
using System.Text;
using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.ReadingAcquisition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BookIndexer = Jularr.Tests.BooksLifecycleTests.BookIndexer;
using Env = Jularr.Tests.BookPdfAcquisitionTests.BookAcquisitionEnvironment;

namespace Jularr.Tests;

[TestClass]
public sealed class LightNovelLifecycleTests
{
    internal const string AniListId = "85470";
    internal const string Series = "Reincarnated Sage";
    internal const string Job = "/data/downloads/complete/lightnovel/";

    internal sealed class FakeNovelAniList : HttpMessageHandler
    {
        public int? Volumes { get; set; }

        public bool Down { get; set; }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Down)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            var media = new
            {
                id = int.Parse(AniListId),
                title = new { romaji = "Rensei no Kenja", english = Series, native = "転生の賢者" },
                description = "A sage is reborn.",
                coverImage = new { extraLarge = (string?)null, large = (string?)null },
                bannerImage = (string?)null,
                format = "NOVEL",
                status = Volumes is null ? "RELEASING" : "FINISHED",
                chapters = (int?)null,
                volumes = Volumes,
                startDate = new { year = 2019 },
                genres = Array.Empty<string>(),
                isAdult = false
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data = new { Media = media } }), Encoding.UTF8, "application/json") });
        }
    }

    internal static async Task<Env> StartAsync(BookIndexer indexer, FakeNovelAniList aniList, IEnumerable<INovelSourceProvider>? novelSources = null)
    {
        var environment = await Env.CreateAsync(newznab: indexer, novelAniList: aniList, novelSources: novelSources);
        await environment.AddNewznabIndexerAsync();
        return environment;
    }

    /// <summary>A request as the Light Novel add dialog sends it: the AniList id, the title, the author and the native title as a search alias.</summary>
    internal static Task<AcquisitionRequest> SubmitAsync(Env environment, string title = Series, string[]? languages = null) =>
        environment.Services.GetRequiredService<AcquisitionRequestService>().SubmitAsync(
            new AcquisitionRequestDraft(
                MediaAcquisitionKind.LightNovel,
                "anilist",
                AniListId,
                title,
                "Magonote",
                null,
                languages is null
                    ? ReadingAcquisitionEngine.LightNovelDraftPayload(title, "転生の賢者", "Magonote")
                    : JsonSerializer.Serialize(new ReadingRequestPayload(title, ["転生の賢者"], "Magonote", PreferredLanguages: languages), JsonSerializerOptions.Web)),
            CancellationToken.None);

    /// <summary>One EPUB volume of the series; <paramref name="edition"/> changes the text, so two editions of a volume differ the way two releases do.</summary>
    internal static void WriteEpub(string folder, string fileName, int volume, string edition = "retail", int chapters = 2, string series = Series)
    {
        var builder = new EpubTestBuilder
        {
            Title = $"{series}, Vol. {volume}",
            Author = "Magonote",
            Language = "en",
            Identifier = $"urn:uuid:{series}-{volume}",
            CalibreSeries = series,
            CalibreSeriesIndex = volume.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        for (var chapter = 1; chapter <= chapters; chapter++)
        {
            builder.Chapter($"c{chapter}.xhtml", $"Volume {volume} Chapter {chapter}", $"Volume {volume}, chapter {chapter}: the sage walks on. ({edition})");
        }

        using var file = File.Create(Path.Combine(folder, fileName));
        builder.Build().CopyTo(file);
    }

    internal static Task<Work> WorkAsync(Env environment) => environment.Db.Works.AsNoTracking().SingleAsync(work => work.MediaType == WorkMediaType.LightNovel);

    internal static Task<ReadingCoverageView> CoverageAsync(Env environment, long workId) =>
        environment.Services.GetRequiredService<ReadingCoverageService>().LoadAsync(workId, CancellationToken.None);

    internal static async Task<AcquisitionRequest> DownloadAsync(Env environment, AcquisitionRequest request, string job, params (string Name, int Volume, string Edition)[] files)
    {
        var folder = environment.LightNovelFolder(job);
        foreach (var (name, volume, edition) in files)
        {
            WriteEpub(folder, name, volume, edition);
        }

        await environment.CompleteDownloadAsync(request, Job + job);
        return await environment.RequestAsync(request.Id);
    }

    [TestMethod]
    public async Task ALightNovelIsRequestedFoundDownloadedImportedTiedToItsVolumesAndRead()
    {
        var aniList = new FakeNovelAniList { Volumes = 3 };
        var indexer = new BookIndexer($"{Series} Vol 1-3 EPUB");
        await using var environment = await StartAsync(indexer, aniList);

        var request = await SubmitAsync(environment);

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, request.StatusMessage);
        StringAssert.Contains(Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName, "Vol 1-3");
        var work = await WorkAsync(environment);
        var volumes = await environment.Db.WorkVolumes.AsNoTracking().Where(volume => volume.WorkId == work.Id).OrderBy(volume => volume.Number).ToListAsync();
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, volumes.Select(volume => volume.Number).ToArray(), "The published volumes AniList states are the canonical ones.");
        Assert.IsTrue(volumes.All(volume => volume.Provider == "anilist" && volume.ExternalId is not null));
        Assert.IsTrue((await CoverageAsync(environment, work.Id)).Monitored, "Requesting a Light Novel monitors it.");

        var stored = await DownloadAsync(environment, request, $"{Series} Vol 1-3 EPUB", ($"{Series} v01.epub", 1, "retail"), ($"{Series} v02.epub", 2, "retail"), ($"{Series} v03.epub", 3, "retail"));

        Assert.AreEqual(AcquisitionRequestStatus.Completed, stored.Status, stored.StatusMessage);
        var coverage = await CoverageAsync(environment, work.Id);
        Assert.IsTrue(coverage.Volumes.All(volume => volume.State == ReadingCoverageState.Installed && volume.InstalledQuality == "EPUB"));
        Assert.IsTrue(coverage.Want.IsEmpty);
        Assert.AreEqual(0, await environment.Db.WantedItems.CountAsync(item => item.WorkId == work.Id));
        Assert.AreEqual(1, await environment.Db.Works.CountAsync(item => item.MediaType == WorkMediaType.LightNovel), "Exactly one canonical Work.");

        var novelId = (await environment.Db.WorkSourceLinks.AsNoTracking().SingleAsync(link => link.WorkId == work.Id && link.SourceKind == WorkSourceKind.NovelWork)).SourceId;
        Assert.AreEqual($"/Novels/Work/{novelId}", stored.ResultUrl);
        var detail = (await new NovelCatalogQueries(environment.Db).GetWorkDetailAsync(novelId, CancellationToken.None))!;
        Assert.HasCount(3, detail.Volumes);
        Assert.HasCount(6, detail.Chapters, "Two chapters per volume, in reading order.");
        CollectionAssert.AreEqual(Enumerable.Range(1, 6).ToArray(), detail.Chapters.Select(chapter => chapter.Number).ToArray());
        StringAssert.Contains(detail.Chapters[2].Title, "Volume 2");
    }

    [TestMethod]
    public async Task ARepeatedRequestAndARepeatedImportKeepOneCanonicalWorkOneSeriesAndOneCopyOfEveryVolume()
    {
        var indexer = new BookIndexer($"{Series} Vol 1-2 EPUB");
        await using var environment = await StartAsync(indexer, new FakeNovelAniList { Volumes = 2 });
        var request = await SubmitAsync(environment);
        var again = await SubmitAsync(environment);
        Assert.AreEqual(request.Id, again.Id, "The same title cannot be requested twice while the first is open.");
        Assert.HasCount(1, environment.Sabnzbd.Grabs);
        await DownloadAsync(environment, request, $"{Series} Vol 1-2 EPUB", ($"{Series} v01.epub", 1, "retail"), ($"{Series} v02.epub", 2, "retail"));
        var work = await WorkAsync(environment);

        var volumes = await environment.Db.NovelVolumes.CountAsync();
        var chapters = await environment.Db.NovelChapters.CountAsync();
        var editions = await environment.Db.NovelVolumeEditions.CountAsync();
        var bindings = await environment.Db.WorkUnitBindings.CountAsync();
        for (var pass = 1; pass <= 3; pass++)
        {
            await environment.RecoverAsync(DateTime.UtcNow.AddDays(pass));
        }

        await environment.CompleteDownloadAsync(request, Job + $"{Series} Vol 1-2 EPUB");
        await SubmitAsync(environment);

        Assert.AreEqual(volumes, await environment.Db.NovelVolumes.CountAsync());
        Assert.AreEqual(chapters, await environment.Db.NovelChapters.CountAsync());
        Assert.AreEqual(editions, await environment.Db.NovelVolumeEditions.CountAsync());
        Assert.AreEqual(bindings, await environment.Db.WorkUnitBindings.CountAsync());
        Assert.AreEqual(1, await environment.Db.Works.CountAsync(item => item.MediaType == WorkMediaType.LightNovel));
        Assert.AreEqual(1, await environment.Db.NovelWorks.CountAsync());
        Assert.AreEqual(1, await environment.Db.WorkSourceLinks.CountAsync(link => link.WorkId == work.Id && link.SourceKind == WorkSourceKind.NovelWork));
        Assert.AreEqual(1, await environment.Db.WorkExternalIdentities.CountAsync(identity => identity.WorkId == work.Id && identity.Provider == "anilist"));
    }

    [TestMethod]
    public async Task VolumesAreMonitoredWholeAndOneByOneAndAMissingVolumeIsWantedUntilItsSingleVolumeReleaseIsImported()
    {
        var indexer = new BookIndexer($"{Series} Vol 1-2 EPUB");
        await using var environment = await StartAsync(indexer, new FakeNovelAniList { Volumes = 3 });
        var request = await SubmitAsync(environment);
        await DownloadAsync(environment, request, $"{Series} Vol 1-2 EPUB", ($"{Series} v01.epub", 1, "retail"), ($"{Series} v02.epub", 2, "retail"));
        var work = await WorkAsync(environment);
        var third = await environment.Db.WorkVolumes.AsNoTracking().SingleAsync(volume => volume.WorkId == work.Id && volume.Number == 3);
        var monitoring = environment.Services.GetRequiredService<Jularr.Web.Features.Monitoring.MonitoringCommands>();
        var wanted = environment.Services.GetRequiredService<WantedReconciler>();

        var coverage = await CoverageAsync(environment, work.Id);
        CollectionAssert.AreEqual(new[] { ReadingCoverageState.Installed, ReadingCoverageState.Installed, ReadingCoverageState.Missing }, coverage.Volumes.Select(volume => volume.State).ToArray());
        Assert.AreEqual(third.Id, Assert.ContainsSingle(await environment.Db.WantedItems.Where(item => item.WorkId == work.Id).ToListAsync()).TargetId, "Only the missing volume is wanted.");

        await monitoring.SetAsync(Jularr.Web.Features.Monitoring.MonitoringTargetKind.Volume, third.Id, false, CancellationToken.None);
        await wanted.ReconcileAsync(work.Id, CancellationToken.None);
        Assert.AreEqual(0, await environment.Db.WantedItems.CountAsync(item => item.WorkId == work.Id), "A volume that is off is not wanted.");
        await monitoring.SetAsync(Jularr.Web.Features.Monitoring.MonitoringTargetKind.Volume, third.Id, null, CancellationToken.None);
        await monitoring.SetWorkAsync(work.Id, false, CancellationToken.None);
        await wanted.ReconcileAsync(work.Id, CancellationToken.None);
        Assert.AreEqual(0, await environment.Db.WantedItems.CountAsync(item => item.WorkId == work.Id), "An unmonitored series wants nothing.");
        await monitoring.SetWorkAsync(work.Id, true, CancellationToken.None);
        await wanted.ReconcileAsync(work.Id, CancellationToken.None);
        Assert.AreEqual(1, await environment.Db.WantedItems.CountAsync(item => item.WorkId == work.Id));

        indexer.Titles.AddRange([$"{Series} v03 EPUB", $"{Series} v02 EPUB"]);
        await environment.RecoverAsync(DateTime.UtcNow.AddDays(2));

        Assert.HasCount(2, environment.Sabnzbd.Grabs);
        StringAssert.Contains(environment.Sabnzbd.Grabs[1].NzbName, "v03", "Only the missing volume is searched; the covered ones are never grabbed again.");
        var latest = await environment.Services.GetRequiredService<AcquisitionAccessStore>().FindLatestAsync(MediaAcquisitionKind.LightNovel, "anilist", AniListId, CancellationToken.None);
        await DownloadAsync(environment, latest!, $"{Series} v03 EPUB", ($"{Series} v03.epub", 3, "retail"));

        coverage = await CoverageAsync(environment, work.Id);
        Assert.IsTrue(coverage.Volumes.All(volume => volume.State == ReadingCoverageState.Installed));
        Assert.AreEqual(0, await environment.Db.WantedItems.CountAsync(item => item.WorkId == work.Id));
        Assert.AreEqual(3, await environment.Db.NovelVolumes.CountAsync());
    }

    [TestMethod]
    public async Task WrongTitleVolumeLanguageAndFormatAreRefusedWithReasonsAndRankOneIsTheReleaseAutomaticAcquisitionTakes()
    {
        var indexer = new BookIndexer();
        await using var environment = await StartAsync(indexer, new FakeNovelAniList { Volumes = 3 });
        var request = await SubmitAsync(environment, languages: ["en"]);
        Assert.IsEmpty(environment.Sabnzbd.Grabs);
        indexer.Titles.AddRange([$"{Series} v01 EPUB", $"{Series} Vol 1-3 EPUB", $"{Series} v04 EPUB", "Another Sage v02 EPUB", $"{Series} v02 German EPUB", $"{Series} v02 PDF"]);
        var manual = environment.Services.GetRequiredService<ReadingManualSearchService>();

        var result = (await manual.SearchAsync(request.Id, refresh: true, Jularr.Web.Features.Acquisition.Search.SearchDepth.Normal, CancellationToken.None))!;

        var ranked = result.Candidates.Where(candidate => candidate.Rank is not null).OrderBy(candidate => candidate.Rank).ToArray();
        Assert.AreEqual(1, ranked[0].Rank);
        StringAssert.Contains(ranked[0].Title, "Vol 1-3", "The set that covers all three missing volumes is first.");
        var rejected = result.Candidates.Where(candidate => candidate.Rank is null).ToArray();
        Assert.IsTrue(rejected.Any(candidate => candidate.Title.StartsWith("Another Sage", StringComparison.Ordinal) && candidate.RejectedBecause == "title does not match"));
        var wrongVolume = rejected.Single(candidate => candidate.Title.Contains("v04", StringComparison.Ordinal));
        Assert.AreEqual("volume 4 is not wanted", wrongVolume.RejectedBecause);
        Assert.IsTrue(rejected.Any(candidate => candidate.Title.Contains("German", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(candidate.RejectedBecause)));
        Assert.IsTrue(rejected.Any(candidate => candidate.Title.Contains("PDF", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(candidate.RejectedBecause)));
        Assert.IsTrue(rejected.All(candidate => !candidate.CanGrab));
        var refused = await manual.GrabAsync(request.Id, wrongVolume.Identity, CancellationToken.None);
        Assert.AreEqual(Jularr.Web.Features.Acquisition.ManualSearch.ManualGrabStatus.NotAvailable, refused.Status);
        Assert.IsEmpty(environment.Sabnzbd.Grabs);

        await environment.RecoverAsync(DateTime.UtcNow.AddDays(2));

        Assert.AreEqual(ranked[0].Title, Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName.Replace(".nzb", string.Empty), "Automatic acquisition grabs Rank 1.");
    }

    [TestMethod]
    public async Task AnUnusableDownloadContinuesWithTheNextReleaseAndAnImportWaitingForStorageResumesWithoutDownloadingAgain()
    {
        var indexer = new BookIndexer($"{Series} Vol 1-2 EPUB", $"{Series} v01 EPUB");
        await using var environment = await StartAsync(indexer, new FakeNovelAniList { Volumes = 2 });
        var request = await SubmitAsync(environment);
        StringAssert.Contains(Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName, "Vol 1-2");

        File.WriteAllText(Path.Combine(environment.LightNovelFolder($"{Series} Vol 1-2 EPUB"), "readme.txt"), "not a book");
        await environment.CompleteDownloadAsync(request, Job + $"{Series} Vol 1-2 EPUB");

        Assert.HasCount(2, environment.Sabnzbd.Grabs, "The next eligible release is taken.");
        StringAssert.Contains(environment.Sabnzbd.Grabs[1].NzbName, "v01");
        Assert.AreEqual(0, await environment.Db.NovelVolumes.CountAsync(), "Nothing was installed from the bad download.");

        // SABnzbd finished the next release, but its completed folder is not mounted yet.
        Directory.Delete(environment.LightNovelFolder($"{Series} v01 EPUB"));
        await environment.CompleteDownloadAsync(await environment.RequestAsync(request.Id), Job + $"{Series} v01 EPUB");
        var waiting = await environment.RequestAsync(request.Id);
        Assert.AreNotEqual(AcquisitionRequestStatus.Completed, waiting.Status);
        Assert.AreNotEqual(AcquisitionRequestStatus.Failed, waiting.Status, waiting.StatusMessage);

        WriteEpub(environment.LightNovelFolder($"{Series} v01 EPUB"), $"{Series} v01.epub", 1);
        await environment.RecoverAsync(DateTime.UtcNow);

        Assert.HasCount(2, environment.Sabnzbd.Grabs, "Nothing is downloaded again.");
        Assert.AreEqual(1, await environment.Db.NovelVolumes.CountAsync());
        Assert.AreEqual(2, await environment.Db.NovelChapters.CountAsync());
        var work = await WorkAsync(environment);
        Assert.AreEqual(ReadingCoverageState.Installed, (await CoverageAsync(environment, work.Id)).Volumes.Single(volume => volume.Number == 1).State);
    }
}
