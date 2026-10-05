using Jularr.Web.Features.Localization;

namespace Jularr.Tests;

/// <summary>
/// #221 part 2A page-placement smoke tests, in the same style as
/// LearningEpisodeActionTests.TheLearnButtonIsHiddenWithoutReviews: this
/// repository has no ASP.NET Core Testing/WebApplicationFactory harness to
/// render Razor Pages end to end, so presence of the offline-library
/// integration points is verified by reading the .cshtml/.cs source
/// directly, plus the JS-catalog keys it depends on.
/// </summary>
[TestClass]
public sealed class OfflineLibraryReaderIntegrationTests
{
    [TestMethod]
    public void NovelWorkPageIncludesTheSaveOfflineAction()
    {
        var view = ReadPage("Novels", "Work.cshtml");
        StringAssert.Contains(view, "<partial name=\"_OfflineLibraryAction\"");
    }

    [TestMethod]
    public void BooksLibraryPageStillIncludesTheSaveOfflineAction()
    {
        var view = ReadPage("Books", "Library.cshtml");
        StringAssert.Contains(view, "<partial name=\"_OfflineLibraryAction\"");
    }

    [TestMethod]
    public void BooksIndexPageHasAnOfflineLibraryFilterWithWorkScopedCards()
    {
        var view = ReadPage("Books", "Index.cshtml");
        StringAssert.Contains(view, "data-offline-library-filter");
        StringAssert.Contains(view, "data-library-filter-option=\"all\"");
        StringAssert.Contains(view, "data-library-filter-option=\"offline\"");
        StringAssert.Contains(view, "data-work-id=\"@book.WorkId\"");
    }

    [TestMethod]
    public void NovelsIndexPageHasAnOfflineLibraryFilterWithWorkScopedCards()
    {
        var view = ReadPage("Novels", "Index.cshtml");
        StringAssert.Contains(view, "data-offline-library-filter");
        StringAssert.Contains(view, "data-library-filter-option=\"all\"");
        StringAssert.Contains(view, "data-library-filter-option=\"offline\"");
        StringAssert.Contains(view, "data-work-id=\"@work.Id\"");
    }

    [TestMethod]
    public void NovelReaderPageLoadsTheRepositoryAndCarriesTheWorkId()
    {
        var view = ReadPage("Novels", "Read.cshtml");
        StringAssert.Contains(view, "js/offline-library-repository.js");
        StringAssert.Contains(view, "data-work-id=\"@chapter.WorkId\"");
    }

    [TestMethod]
    public void BookReaderPageLoadsTheRepositoryAndCarriesTheWorkId()
    {
        var view = ReadPage("Books", "Read.cshtml");
        StringAssert.Contains(view, "js/offline-library-repository.js");
        StringAssert.Contains(view, "data-work-id=\"@Model.Reader.Work.Id\"");
    }

    [TestMethod]
    public void GlobalLayoutRendersTheOfflineLibraryCatalogAndScriptsForAuthenticatedPages()
    {
        var view = ReadPage("Shared", "_Layout.cshtml");
        StringAssert.Contains(view, "offline-library-text");
        StringAssert.Contains(view, "js/offline-library-manager.js");
        StringAssert.Contains(view, "js/offline-library-ui.js");
    }

    [TestMethod]
    public void GlobalLayoutAndEpisodePlayerExposeTheBinaryOfflinePackageFlow()
    {
        var layout = ReadPage("Shared", "_Layout.cshtml");
        var episode = EpisodePlayerSource.Read(RepositoryRoot());
        StringAssert.Contains(layout, "js/offline-media-manager.js");
        StringAssert.Contains(layout, "js/offline-media-ui.js");
        StringAssert.Contains(episode, "data-offline-episode-save");
        StringAssert.Contains(episode, "data-episode-id");
    }

    [TestMethod]
    public void AccountFooterRendersTheGlobalDownloadIndicator()
    {
        var view = ReadPage("Shared", "_AppAccountFooter.cshtml");
        StringAssert.Contains(view, "data-offline-download-indicator");
    }

    [TestMethod]
    [DataRow("offlineLibrary.action.saveButton")]
    [DataRow("offlineLibrary.action.idle")]
    [DataRow("offlineLibrary.action.downloading")]
    [DataRow("offlineLibrary.action.available")]
    [DataRow("offlineLibrary.settings.storageUsed")]
    [DataRow("offlineLibrary.settings.empty")]
    [DataRow("offlineLibrary.chapter.offlineMissing")]
    [DataRow("offlineLibrary.library.filterAll")]
    [DataRow("offlineLibrary.library.filterOffline")]
    [DataRow("offlineLibrary.indicator.downloading")]
    public void OfflineLibraryCatalogKeysExist(string key)
    {
        Assert.IsTrue(
            UiTranslationResources.TryGet(key, out var message),
            $"Missing catalog entry for '{key}'.");
        Assert.AreEqual(key, message.Key);
        Assert.IsFalse(string.IsNullOrWhiteSpace(message.DefaultText));
    }

    private static string ReadPage(string folder, string fileName) =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Jularr.Web", "Pages", folder, fileName));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }
}
