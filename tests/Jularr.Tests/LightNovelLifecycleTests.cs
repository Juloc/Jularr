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

    internal static async Task<Env> StartAsync(BookIndexer indexer, FakeNovelAniList aniList)
    {
        var environment = await Env.CreateAsync(newznab: indexer, novelAniList: aniList);
        await environment.AddNewznabIndexerAsync();
        return environment;
    }

    /// <summary>A request as the Light Novel add dialog sends it: the AniList id, the title, the author and the native title as a search alias.</summary>
    internal static Task<AcquisitionRequest> SubmitAsync(Env environment, string title = Series) =>
        environment.Services.GetRequiredService<AcquisitionRequestService>().SubmitAsync(
            new AcquisitionRequestDraft(MediaAcquisitionKind.LightNovel, "anilist", AniListId, title, "Magonote", null, ReadingAcquisitionEngine.LightNovelDraftPayload(title, "転生の賢者", "Magonote")),
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
}
