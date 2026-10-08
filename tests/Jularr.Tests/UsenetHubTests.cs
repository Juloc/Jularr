using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Localization;
using Jularr.Web.Pages.Admin;

namespace Jularr.Tests;

[TestClass]
public sealed class UsenetHubTests
{
    [TestMethod]
    public void CategoryCheckPassesWhenEveryMediaTypeHasACategory()
    {
        var check = UsenetModel.BuildCategoryCheck(
            UiTextBundle.English,
            [Client(DownloadClientSettings.CreateDefault("http://sab:8080"))]);

        Assert.AreEqual(UsenetCheckState.Ok, check.State);
        StringAssert.Contains(check.Detail, "Manga: manga");
        StringAssert.Contains(check.Detail, "Light novels: lightnovels");
    }

    [TestMethod]
    public void CategoryCheckNamesTheMediaTypesWithoutACategory()
    {
        var client = Client(new DownloadClientSettings(
            "http://sab:8080",
            new Dictionary<MediaAcquisitionKind, string?>
            {
                [MediaAcquisitionKind.Anime] = "anime",
                [MediaAcquisitionKind.Book] = "books"
            }));

        var check = UsenetModel.BuildCategoryCheck(UiTextBundle.English, [client], [MediaAcquisitionKind.Anime, MediaAcquisitionKind.Book, MediaAcquisitionKind.Manga, MediaAcquisitionKind.LightNovel]);

        Assert.AreEqual(UsenetCheckState.Warning, check.State);
        StringAssert.Contains(check.Detail, "Manga, Light novels");
        Assert.IsFalse(check.Detail.Contains("Books", StringComparison.Ordinal));
        Assert.AreEqual($"/Settings/DownloadClients/Edit/{client.Id}", check.LinkPage);
    }

    [TestMethod]
    public void CategoryCheckAcceptsCategoriesSpreadOverSeveralClients()
    {
        var books = Client(new DownloadClientSettings(
            "http://sab-a:8080",
            new Dictionary<MediaAcquisitionKind, string?>
            {
                [MediaAcquisitionKind.Anime] = "anime",
                [MediaAcquisitionKind.Book] = "books"
            }));
        var reading = Client(new DownloadClientSettings(
            "http://sab-b:8080",
            new Dictionary<MediaAcquisitionKind, string?>
            {
                [MediaAcquisitionKind.Manga] = "manga",
                [MediaAcquisitionKind.LightNovel] = "ln"
            }));

        var check = UsenetModel.BuildCategoryCheck(UiTextBundle.English, [books, reading], [MediaAcquisitionKind.Anime, MediaAcquisitionKind.Book, MediaAcquisitionKind.Manga, MediaAcquisitionKind.LightNovel]);

        Assert.AreEqual(UsenetCheckState.Ok, check.State);
    }

    [TestMethod]
    public void EveryMediaTypeTheManagerServesIsAskedForACategoryIncludingMusicAndMovies()
    {
        var withoutMusic = Client(new DownloadClientSettings(
            "http://sab:8080",
            new Dictionary<MediaAcquisitionKind, string?> { [MediaAcquisitionKind.Movie] = "movies", [MediaAcquisitionKind.Tv] = "tv" }));

        var all = UsenetModel.BuildCategoryCheck(UiTextBundle.English, [withoutMusic]);
        var movieAndTvOnly = UsenetModel.BuildCategoryCheck(UiTextBundle.English, [withoutMusic], [MediaAcquisitionKind.Movie, MediaAcquisitionKind.Tv]);

        Assert.AreEqual(UsenetCheckState.Warning, all.State);
        StringAssert.Contains(all.Detail, "Music");
        Assert.AreEqual(UsenetCheckState.Ok, movieAndTvOnly.State, "A type whose module is off is not asked for.");
        Assert.AreEqual(UsenetCheckState.Ok, UsenetModel.BuildCategoryCheck(UiTextBundle.English, [Client(DownloadClientSettings.CreateDefault("http://sab:8080"))], [MediaAcquisitionKind.Music]).State);
    }

    [TestMethod]
    public void IndexerFactsUseTheCategoriesEachMediaTypeSearches()
    {
        var entry = new IndexerEntry(
            Guid.NewGuid(),
            "Newznab",
            IndexerType.Newznab,
            Enabled: true,
            Priority: 1,
            new IndexerSettings("http://indexer", [5070], [], 100, BookCategories: [7020]),
            ApiKey: "unused");

        CollectionAssert.AreEqual(new[] { 5070 }, SearchPlanner.Categories(MediaAcquisitionKind.Anime, entry).ToArray());
        CollectionAssert.AreEqual(new[] { 7020 }, SearchPlanner.Categories(MediaAcquisitionKind.Book, entry).ToArray());
        CollectionAssert.Contains(SearchPlanner.Categories(MediaAcquisitionKind.Manga, entry).ToArray(), 7030);
        CollectionAssert.Contains(SearchPlanner.Categories(MediaAcquisitionKind.LightNovel, entry).ToArray(), 7020);
    }

    [TestMethod]
    [DataRow("manga", MediaAcquisitionKind.Manga)]
    [DataRow("lightNovel", MediaAcquisitionKind.LightNovel)]
    [DataRow("book", MediaAcquisitionKind.Book)]
    [DataRow("anime", MediaAcquisitionKind.Book)]
    [DataRow(null, MediaAcquisitionKind.Book)]
    public void SearchTestRunsBooksMangaOrLightNovels(string? value, MediaAcquisitionKind expected) =>
        Assert.AreEqual(expected, UsenetModel.ParseTestKind(value));

    private static DownloadClientEntry Client(DownloadClientSettings settings) =>
        new(Guid.NewGuid(), "SABnzbd", DownloadClientType.Sabnzbd, Enabled: true, Priority: 1, settings, Secret: null);
}
