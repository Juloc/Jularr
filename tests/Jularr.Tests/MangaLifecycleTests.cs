using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Jularr.Web.Features.ReadingAcquisition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BookIndexer = Jularr.Tests.BooksLifecycleTests.BookIndexer;
using Env = Jularr.Tests.BookPdfAcquisitionTests.BookAcquisitionEnvironment;

namespace Jularr.Tests;

[TestClass]
public sealed class MangaLifecycleTests
{
    internal const string AniListId = "154587";
    private const string Title = "Frieren";
    private const string Job = "/data/downloads/complete/manga/";

    internal sealed class FakeAniList : HttpMessageHandler
    {
        public int? Volumes { get; set; }

        public int? Chapters { get; set; }

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
                format = "MANGA",
                isAdult = false,
                title = new { romaji = "Sousou no Frieren", english = Title, native = "葬送のフリーレン" },
                description = "An elf outlives her party.",
                coverImage = new { extraLarge = (string?)null, large = (string?)null },
                bannerImage = (string?)null,
                status = Volumes is null && Chapters is null ? "RELEASING" : "FINISHED",
                chapters = Chapters,
                volumes = Volumes,
                startDate = new { year = 2020 }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data = new { Media = media } }), Encoding.UTF8, "application/json") });
        }
    }

    internal static async Task<Env> StartAsync(BookIndexer indexer, FakeAniList aniList)
    {
        var environment = await Env.CreateAsync(newznab: indexer, aniList: aniList);
        await environment.AddNewznabIndexerAsync();
        return environment;
    }

    internal static Task<AcquisitionRequest> SubmitAsync(Env environment) =>
        environment.Services.GetRequiredService<AcquisitionRequestService>().SubmitAsync(
            new AcquisitionRequestDraft(MediaAcquisitionKind.Manga, "anilist", AniListId, Title, "葬送のフリーレン", null),
            CancellationToken.None);

    internal static void WriteVolume(string folder, string name, int pages = 3)
    {
        using var archive = ZipFile.Open(Path.Combine(folder, name), ZipArchiveMode.Create);
        for (var index = 0; index < pages; index++)
        {
            var entry = archive.CreateEntry($"{index + 1:D3}.jpg", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write([(byte)(index + 1), 2, 3, (byte)name.Length]);
        }
    }

    internal static Task<Work> MangaWorkAsync(Env environment) => environment.Db.Works.AsNoTracking().SingleAsync(work => work.MediaType == WorkMediaType.Manga);

    internal static Task<ReadingCoverageView> CoverageAsync(Env environment, long workId) =>
        environment.Services.GetRequiredService<ReadingCoverageService>().LoadAsync(workId, CancellationToken.None);

    private static async Task<AcquisitionRequest> DownloadAsync(Env environment, AcquisitionRequest request, string job, params string[] files)
    {
        var folder = environment.MangaFolder(job);
        foreach (var file in files)
        {
            WriteVolume(folder, file);
        }

        await environment.CompleteDownloadAsync(request, Job + job);
        return await environment.RequestAsync(request.Id);
    }

    [TestMethod]
    public async Task AMangaIsRequestedFoundDownloadedImportedTiedToItsVolumesAndRead()
    {
        var aniList = new FakeAniList { Volumes = 3 };
        var indexer = new BookIndexer("Frieren Vol 1-3 CBZ");
        await using var environment = await StartAsync(indexer, aniList);

        var request = await SubmitAsync(environment);

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, request.StatusMessage);
        var grab = Assert.ContainsSingle(environment.Sabnzbd.Grabs);
        StringAssert.Contains(grab.NzbName, "Vol 1-3");
        var work = await MangaWorkAsync(environment);
        var volumes = await environment.Db.WorkVolumes.AsNoTracking().Where(volume => volume.WorkId == work.Id).OrderBy(volume => volume.Number).ToListAsync();
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, volumes.Select(volume => volume.Number).ToArray());
        Assert.IsTrue(volumes.All(volume => volume.Provider == "anilist" && volume.ExternalId is not null), "Every volume carries the provider identity AniList vouched for.");
        Assert.IsTrue((await CoverageAsync(environment, work.Id)).Monitored, "Requesting a Manga monitors it.");

        var stored = await DownloadAsync(environment, request, "Frieren Vol 1-3", "Frieren v01.cbz", "Frieren v02.cbz", "Frieren v03.cbz");

        Assert.AreEqual(AcquisitionRequestStatus.Completed, stored.Status, stored.StatusMessage);
        var coverage = await CoverageAsync(environment, work.Id);
        Assert.IsTrue(coverage.Volumes.All(volume => volume.State == ReadingCoverageState.Installed), "All three volumes are installed.");
        Assert.IsTrue(coverage.Want.IsEmpty, "Nothing is wanted any more.");
        Assert.AreEqual(0, await environment.Db.WantedItems.CountAsync(item => item.WorkId == work.Id), "Wanted dropped the Work without a rescan.");
        Assert.AreEqual(1, await environment.Db.Works.CountAsync(item => item.MediaType == WorkMediaType.Manga), "Exactly one canonical Work.");

        var repository = new MangaRepository(environment.Db);
        var seriesId = Guid.Parse((await environment.Db.WorkSourceLinks.AsNoTracking().SingleAsync(link => link.WorkId == work.Id && link.SourceKind == WorkSourceKind.MangaSeries)).SourceId.ToString());
        var chapters = await repository.GetChaptersAsync(seriesId, CancellationToken.None);
        Assert.HasCount(3, chapters);
        CollectionAssert.AreEqual(new int?[] { 1, 2, 3 }, chapters.Select(chapter => chapter.VolumeNumber).ToArray(), "The reader lists the volumes in order.");
        var first = (await repository.GetChapterAsync(chapters[1].Id, CancellationToken.None))!;
        await repository.SaveProgressAsync("owner", first, 2, CancellationToken.None);
        var resumed = (await repository.GetProgressAsync("owner", seriesId, CancellationToken.None))!;
        Assert.AreEqual(chapters[1].Id, resumed.ChapterId);
        Assert.AreEqual(2, resumed.PageIndex);
    }
}
