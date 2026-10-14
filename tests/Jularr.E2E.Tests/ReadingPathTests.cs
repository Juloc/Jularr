using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // A novel, a manga volume and a light novel volume from their metadata providers through request, download and import to reading on and continuing after signing in anew.
    [TestClass]
    public sealed class ReadingPathTests
    {
        [TestMethod]
        [Timeout(600_000)]
        public async Task BookMangaAndLightNovel_FromMetadataToRequestDownloadImportReadAndContinue()
        {
            using var folders = new Folders("Books", "Manga", "Light Novels");
            var book = new FakeRelease("Jane Austen - Pride and Prejudice (epub)", "pride", 7020,
                Steps.WriteEpub(Path.Combine(folders.Fixtures, "book.epub"), FakeServices.BookTitle, "Jane Austen"));
            var volume = new FakeRelease("Example Manga v01 (2019) (Digital)", "v01", 7030, Steps.WriteComic(Path.Combine(folders.Fixtures, "volume.cbz"), 3));
            var novel = new FakeRelease("Example Novel v01 (2020) (epub)", "novel", 7020,
                Steps.WriteEpub(Path.Combine(folders.Fixtures, "novel.epub"), FakeServices.NovelTitle, "Ren Example"));
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:OpenLibrary"] = fakes.OpenLibrary.ToString(),
                ["Providers:AniList"] = fakes.AniList.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Books", "Book"), new LibraryPath("Manga", "Book", "Manga"),
                    new LibraryPath("Light Novels", "Book", "Light novel"));
                await Steps.AddUsenetAsync(page, fakes, "7000,7020,7030");

                // The novel comes from Open Library, the manga series from AniList; both are requested on their details.
                await Steps.ImportAndRequestAsync(page, "Book", null, FakeServices.BookTitle);
                await Steps.ImportAndRequestAsync(page, "Book", "Manga", FakeServices.MangaTitle, "anilist");
                await Steps.ImportAndRequestAsync(page, "Book", "Light novel", FakeServices.NovelTitle, "anilist");

                // Each search finds its release, offered just before; the chosen one is downloaded.
                fakes.Offer(book);
                await Steps.OpenWantedAsync(page, FakeServices.BookTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                StringAssert.Contains(await page.Locator("tr", new() { HasText = book.Title }).InnerTextAsync(), "Eligible");
                await Steps.GrabAsync(page, book);
                fakes.Offer(volume);
                await Steps.OpenWantedAsync(page, FakeServices.MangaTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                StringAssert.Contains(await page.Locator("tr", new() { HasText = volume.Title }).InnerTextAsync(), "Eligible");
                await Steps.GrabAsync(page, volume);
                fakes.Offer(novel);
                await Steps.OpenWantedAsync(page, FakeServices.NovelTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                StringAssert.Contains(await page.Locator("tr", new() { HasText = novel.Title }).InnerTextAsync(), "Eligible");
                await Steps.GrabAsync(page, novel);
                CollectionAssert.AreEqual(new[] { book, volume, novel }.Select(release => fakes.NzbAddress(release).ToString()).ToArray(), fakes.Grabbed.ToArray());

                // Both land in their own library path.
                var bookDetails = await Steps.WaitForAvailableAsync(page, FakeServices.BookTitle, "Read");
                var mangaDetails = await Steps.WaitForAvailableAsync(page, FakeServices.MangaTitle, "Read");
                var novelDetails = await Steps.WaitForAvailableAsync(page, FakeServices.NovelTitle, "Read");
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Books"), "*.epub", SearchOption.AllDirectories).Count(), "The novel was not imported once.");
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Manga"), "*.cbz", SearchOption.AllDirectories).Count(), "The volume was not imported once.");
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Light Novels"), "*.epub", SearchOption.AllDirectories).Count(), "The light novel was not imported once.");

                // Reading on saves the position in each.
                await Steps.OpenReaderAsync(page, bookDetails);
                StringAssert.Contains(await page.Locator("[data-role=content]").InnerTextAsync(), "Chapter One");
                await Steps.ReadNextSectionAsync(page);
                await Steps.OpenReaderAsync(page, mangaDetails);
                await Steps.TurnAsync(page);
                await Steps.OpenReaderAsync(page, novelDetails);
                await Steps.ReadNextSectionAsync(page);

                // Signed out and in again, both open where reading stopped.
                await Steps.SignOutAndInAsync(page);
                await Steps.OpenReaderAsync(page, bookDetails);
                StringAssert.Contains(await page.Locator("[data-role=content]").InnerTextAsync(), "Chapter Two");
                await Steps.OpenReaderAsync(page, mangaDetails);
                Assert.AreEqual("2 / 3", (await page.Locator("[data-role=position]").InnerTextAsync()).Trim());
                await Steps.OpenReaderAsync(page, novelDetails);
                StringAssert.Contains(await page.Locator("[data-role=content]").InnerTextAsync(), "Chapter Two");

                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "reading-path");
                throw;
            }
        }
    }
}
