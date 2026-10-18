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

                // The novel is added from Open Library on the Works page; the manga and the light novel are found on Discover in AniList.
                await Steps.ImportAndRequestAsync(page, "Book", null, FakeServices.BookTitle);
                await Steps.RequestFromCatalogAsync(page, "manga", FakeServices.MangaTitle, "anilist");
                await Steps.RequestFromCatalogAsync(page, "lightnovels", FakeServices.NovelTitle, "anilist");

                // Each search finds its release, offered just before; the chosen one is downloaded.
                fakes.Offer(book);
                await Steps.OpenSearchAsync(page, FakeServices.BookTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                StringAssert.Contains(await page.Locator("tr", new() { HasText = book.Title }).InnerTextAsync(), "Eligible");
                await Steps.GrabAsync(page, book);
                fakes.Offer(volume);
                await Steps.OpenSearchAsync(page, FakeServices.MangaTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                StringAssert.Contains(await page.Locator("tr", new() { HasText = volume.Title }).InnerTextAsync(), "Eligible");
                await Steps.GrabAsync(page, volume);
                fakes.Offer(novel);
                await Steps.OpenSearchAsync(page, FakeServices.NovelTitle);
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
                await AssertPaperReaderAsync(page);
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
                // A wide window shows two pages side by side.
                StringAssert.Matches((await page.Locator("[data-role=position]").InnerTextAsync()).Trim(), new System.Text.RegularExpressions.Regex("^2(–3)? / 3$"));
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

        private static async Task AssertPaperReaderAsync(IPage page)
        {
            var root = page.Locator(".reader");
            await page.WaitForFunctionAsync("() => Number(document.querySelector('.reader').dataset.pageCount) > 2");
            await page.Locator("[data-action=settings]").ClickAsync();
            await page.GetByLabel("Page turn animation", new() { Exact = true }).SelectOptionAsync("on");
            await page.Locator("[data-action=settings]").ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.curl === 'true'");
            Assert.IsTrue(await page.Locator(".reader__leaf").CountAsync() <= 6);
            var margins = await page.Locator("[data-role=content]").EvaluateAsync<double>("el => el.querySelector('p').getBoundingClientRect().left - el.getBoundingClientRect().left");
            Assert.IsTrue(margins >= 23, "Each paper page needs its own inner text margin.");
            var area = (await page.Locator(".reader__curl").BoundingBoxAsync())!;
            var initial = await root.GetAttributeAsync("data-page");
            await page.Mouse.MoveAsync(area.X + area.Width - 8, area.Y + area.Height - 12);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(area.X + area.Width * .65f, area.Y + area.Height - 60, new() { Steps = 18 });
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.curlState === 'user_fold'");
            Assert.AreEqual(initial, await root.GetAttributeAsync("data-page"), "Holding a page must not save an uncommitted turn.");
            Assert.IsTrue(await page.Locator(".reader__leaf").EvaluateAllAsync<bool>("items => items.some(el => el.style.clipPath.startsWith('polygon('))"));
            await page.Mouse.MoveAsync(area.X + area.Width - 8, area.Y + area.Height - 12, new() { Steps = 18 });
            await page.Mouse.UpAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.curlState === 'read'");
            Assert.AreEqual(initial, await root.GetAttributeAsync("data-page"));
            await page.RunAndWaitForResponseAsync(() => page.Locator("[data-turn='1']").ClickAsync(), response => response.Request.Method == "PUT" && response.Ok);
            Assert.AreNotEqual(initial, await root.GetAttributeAsync("data-page"));
            await page.Locator("[data-reader-select]").ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.curl === 'false'");
            await page.Locator("[data-reader-select]").ClickAsync();

            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await page.Locator("[data-action=settings]").ClickAsync();
            await page.GetByLabel("Page turn animation", new() { Exact = true }).SelectOptionAsync("system");
            await page.Locator("[data-action=settings]").ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.curl === 'false'");
            await page.SetViewportSizeAsync(390, 844);
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.columns === '1'");
            await page.Locator("[data-action=settings]").ClickAsync();
            await page.GetByLabel("Layout", new() { Exact = true }).SelectOptionAsync("scroll");
            await page.GetByLabel("Paper colour", new() { Exact = true }).FillAsync("#242424");
            await page.Locator("[data-action=settings]").ClickAsync();
            Assert.AreEqual("scroll", await root.GetAttributeAsync("data-mode"));
            Assert.AreEqual("rgb(250, 250, 250)", await root.EvaluateAsync<string>("el => getComputedStyle(el.querySelector('[data-role=content]')).color"));
            Assert.IsTrue(await page.Locator("[data-role=content]").EvaluateAsync<bool>("el => el.scrollHeight > el.clientHeight"));
            await page.SetViewportSizeAsync(1280, 720);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.NoPreference });
        }

        [TestMethod]
        [Timeout(600_000)]
        public async Task PdfBook_FromRequestDownloadAndImportReadsReflowedAndContinues()
        {
            using var folders = new Folders("Books");
            // Chapters long enough to scroll through, each paragraph a few lines with a blank line after.
            string[] Text(int paragraphs) => [.. Enumerable.Range(1, paragraphs).SelectMany(number => new[] { "Paragraph " + number + " runs across the page like the text of any printed book",
                "does, line after line, until it ends here.", "" })];
            var pdf = new FakeRelease("Jane Austen - Pride and Prejudice (pdf)", "pride-pdf", 7020, Steps.WritePdf(Path.Combine(folders.Fixtures, "book.pdf"),
                ["Chapter One", "", "", "It is a truth universally acknowledged, that a single man in possession", "of a good fortune, must be in want of a wife.", "", .. Text(13)],
                [.. Text(16)],
                ["Chapter Two", "", "", "Mr. Bennet was among the earliest of those who waited on Mr. Bingley.", "", .. Text(13)],
                [.. Text(16)]));
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:OpenLibrary"] = fakes.OpenLibrary.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Books", "Book"));
                await Steps.AddUsenetAsync(page, fakes, "7000,7020");
                await Steps.ImportAndRequestAsync(page, "Book", null, FakeServices.BookTitle);

                // The PDF release is found, grabbed and imported like an EPUB.
                fakes.Offer(pdf);
                await Steps.OpenSearchAsync(page, FakeServices.BookTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                await Steps.GrabAsync(page, pdf);
                var details = await Steps.WaitForAvailableAsync(page, FakeServices.BookTitle, "Read");
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Books"), "*.pdf", SearchOption.AllDirectories).Count(), "The PDF was not imported once.");

                // The reader reflows it by chapter, keeps the original at hand and continues where reading stopped.
                await Steps.OpenReaderAsync(page, details);
                StringAssert.Contains(await page.Locator("[data-role=content]").InnerTextAsync(), "must be in want of a wife.");
                var download = page.GetByRole(AriaRole.Button, new() { Name = "Download", Exact = true });
                await download.ClickAsync();
                Assert.AreEqual(1, await page.GetByRole(AriaRole.Link, new() { Name = "Original PDF" }).CountAsync());
                var epub = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Link, new() { Name = "EPUB", Exact = true }).ClickAsync());
                Assert.AreEqual(".epub", Path.GetExtension(epub.SuggestedFilename));
                await download.ClickAsync();
                await Steps.ReadNextSectionAsync(page);
                await Steps.SignOutAndInAsync(page);
                await Steps.OpenReaderAsync(page, details);
                StringAssert.Contains(await page.Locator("[data-role=content]").InnerTextAsync(), "Chapter Two");

                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "reading-pdf");
                throw;
            }
        }
    }
}
