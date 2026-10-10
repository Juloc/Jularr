using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.ReadingAcquisition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BookIndexer = Jularr.Tests.BooksLifecycleTests.BookIndexer;
using Env = Jularr.Tests.BookPdfAcquisitionTests.BookAcquisitionEnvironment;
using Lifecycle = Jularr.Tests.LightNovelLifecycleTests;

namespace Jularr.Tests;

[TestClass]
public sealed class LightNovelWebTests
{
    private const string Ncode = "n9669bk";

    private sealed class FakeNcode : INovelSourceProvider
    {
        public int Chapters { get; set; } = 1;

        public string Key => NcodeNovelSourceProvider.ProviderKey;

        public bool CanHandle(Uri sourceUri) => sourceUri.Host == "ncode.syosetu.com";

        public Task<NovelSourceWorkSnapshot> GetWorkAsync(Uri sourceUri, CancellationToken cancellationToken) =>
            Task.FromResult(new NovelSourceWorkSnapshot(
                Key,
                Ncode,
                sourceUri.ToString(),
                "無職転生",
                "理不尽な孫の手",
                null,
                [.. Enumerable.Range(1, Chapters).Select(number => new NovelSourceChapterReference(number, $"Chapter {number}", $"https://ncode.syosetu.com/{Ncode}/{number}/"))]));

        public Task<NovelSourceChapterSnapshot> GetChapterAsync(Uri sourceUri, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used.");
    }

    private static Task<AcquisitionRequest> SubmitWebAsync(Env environment) =>
        environment.Services.GetRequiredService<AcquisitionRequestService>().SubmitAsync(
            new AcquisitionRequestDraft(MediaAcquisitionKind.LightNovel, NcodeNovelSourceProvider.ProviderKey, Ncode, "無職転生", "理不尽な孫の手", null, null),
            CancellationToken.None);

    [TestMethod]
    public async Task ASyosetuWorkIsImportedWithoutUsenetTiedToItsWorkAndRefreshesAddOnlyNewChaptersAndKeepProgress()
    {
        var source = new FakeNcode();
        await using var environment = await Lifecycle.StartAsync(new BookIndexer(), new Lifecycle.FakeNovelAniList(), [source]);

        var request = await SubmitWebAsync(environment);

        Assert.AreEqual(AcquisitionRequestStatus.Completed, request.Status, request.StatusMessage);
        Assert.IsEmpty(environment.Sabnzbd.Grabs, "A direct web import never goes through the download client.");
        var novel = await environment.Db.NovelWorks.AsNoTracking().SingleAsync();
        Assert.AreEqual(NcodeNovelSourceProvider.ProviderKey, novel.SourceProvider);
        Assert.AreEqual($"/Novels/Work/{novel.Id}", request.ResultUrl);
        var work = await Lifecycle.WorkAsync(environment);
        Assert.AreEqual(work.Id, (await environment.Db.WorkSourceLinks.AsNoTracking().SingleAsync(link => link.SourceKind == WorkSourceKind.NovelWork)).WorkId, "The request's Work holds the imported web novel.");
        Assert.AreEqual(Ncode, (await environment.Db.WorkExternalIdentities.AsNoTracking().SingleAsync(identity => identity.WorkId == work.Id)).ExternalId);

        var firstChapter = await environment.Db.NovelChapters.AsNoTracking().SingleAsync();
        await new NovelProgressService(environment.Db).SaveProgressAsync("owner", firstChapter.Id, 300, "ja", 0, 0, CancellationToken.None);
        source.Chapters = 3;
        var import = environment.Services.GetRequiredService<NovelImportService>();

        await import.RefreshWorkAsync(novel.Id, CancellationToken.None);
        await import.RefreshWorkAsync(novel.Id, CancellationToken.None);

        var chapters = await environment.Db.NovelChapters.AsNoTracking().OrderBy(chapter => chapter.Number).ToListAsync();
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, chapters.Select(chapter => chapter.Number).ToArray(), "New chapters arrive in order, none twice.");
        Assert.AreEqual(firstChapter.Id, chapters[0].Id, "The known chapter keeps its identity.");
        Assert.AreEqual(300, (await new NovelProgressService(environment.Db).GetProgressAsync("owner", novel.Id, CancellationToken.None))!.PositionPermille, "Reading progress survives a refresh.");
        Assert.AreEqual(1, await environment.Db.NovelWorks.CountAsync());
    }

    [TestMethod]
    public async Task AWebNovelAndThePublishedEditionOfTheSameSeriesKeepSeparateIdentitiesAndProgress()
    {
        var source = new FakeNcode { Chapters = 2 };
        var indexer = new BookIndexer($"{Lifecycle.Series} Vol 1-2 EPUB");
        await using var environment = await Lifecycle.StartAsync(indexer, new Lifecycle.FakeNovelAniList { Volumes = 2 }, [source]);
        var published = await Lifecycle.SubmitAsync(environment);
        await Lifecycle.DownloadAsync(environment, published, $"{Lifecycle.Series} Vol 1-2 EPUB", ($"{Lifecycle.Series} v01.epub", 1, "retail"), ($"{Lifecycle.Series} v02.epub", 2, "retail"));
        var publishedChapter = await environment.Db.NovelChapters.AsNoTracking().OrderBy(chapter => chapter.Number).FirstAsync();
        await new NovelProgressService(environment.Db).SaveProgressAsync("owner", publishedChapter.Id, 800, "en", 0, 0, CancellationToken.None);
        var epubWork = await environment.Db.NovelWorks.AsNoTracking().SingleAsync();

        var web = await SubmitWebAsync(environment);

        Assert.AreEqual(AcquisitionRequestStatus.Completed, web.Status, web.StatusMessage);
        Assert.AreEqual(2, await environment.Db.NovelWorks.CountAsync(), "A title match never merges a web novel into a published edition.");
        Assert.AreEqual(2, await environment.Db.Works.CountAsync(item => item.MediaType == WorkMediaType.LightNovel));
        Assert.AreNotEqual(published.WorkId, web.WorkId);
        var coverage = await Lifecycle.CoverageAsync(environment, published.WorkId!.Value);
        Assert.IsTrue(coverage.Volumes.All(volume => volume.State == ReadingCoverageState.Installed), "The web copy changes nothing about the published volumes.");
        Assert.AreEqual(800, (await new NovelProgressService(environment.Db).GetProgressAsync("owner", epubWork.Id, CancellationToken.None))!.PositionPermille);
        Assert.IsNull(await new NovelProgressService(environment.Db).GetProgressAsync("owner", (await environment.Db.NovelWorks.AsNoTracking().SingleAsync(novel => novel.Id != epubWork.Id)).Id, CancellationToken.None), "Each edition has its own reading progress.");
    }

    [TestMethod]
    public async Task WhenAniListIsDownTheTitleIsStillSearchedWholeImportedAndReadableAndTheStructureArrivesWithTheNextRefresh()
    {
        var aniList = new Lifecycle.FakeNovelAniList { Volumes = 2, Down = true };
        var indexer = new BookIndexer($"{Lifecycle.Series} Complete EPUB");
        await using var environment = await Lifecycle.StartAsync(indexer, aniList);

        var request = await Lifecycle.SubmitAsync(environment);

        StringAssert.Contains(Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName, "Complete", "Without a known structure the whole title is searched.");
        var work = await Lifecycle.WorkAsync(environment);
        Assert.IsFalse((await Lifecycle.CoverageAsync(environment, work.Id)).HasStructure);
        var stored = await Lifecycle.DownloadAsync(environment, request, $"{Lifecycle.Series} Complete EPUB", ($"{Lifecycle.Series} v01.epub", 1, "retail"), ($"{Lifecycle.Series} v02.epub", 2, "retail"));
        Assert.AreEqual(AcquisitionRequestStatus.Completed, stored.Status, stored.StatusMessage);
        Assert.AreEqual(2, await environment.Db.NovelVolumes.CountAsync(), "The library and reader work while the provider is down.");

        aniList.Down = false;
        var structure = environment.Services.GetRequiredService<ReadingStructureService>();
        var refresh = await structure.RefreshAsync(work.Id, CancellationToken.None);

        Assert.IsNull(refresh.Problem);
        Assert.AreEqual(2, refresh.Volumes!.Created);
        Assert.IsFalse((await structure.RefreshAsync(work.Id, CancellationToken.None)).Changed, "A refresh of the same structure changes nothing.");
        var coverage = await Lifecycle.CoverageAsync(environment, work.Id);
        Assert.IsTrue(coverage.Volumes.All(volume => volume.State == ReadingCoverageState.Installed), "The imported volumes are tied to the volumes AniList then states.");
        Assert.AreEqual(2, await environment.Db.WorkVolumes.CountAsync(volume => volume.WorkId == work.Id));
        Assert.AreEqual(0, await environment.Db.WantedItems.CountAsync(item => item.WorkId == work.Id));
    }
}
