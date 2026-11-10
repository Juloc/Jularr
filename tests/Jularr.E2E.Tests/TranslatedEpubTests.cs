using System.IO.Compression;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class TranslatedEpubTests
    {
        // The book job translates a small book with the fake TranslateGemma; the translated works list and the Reader then offer its EPUB.
        [TestMethod]
        [Timeout(300_000)]
        public async Task TranslatedBook_DownloadsAsEpub_FromTheTranslatedWorksListAndTheReader()
        {
            using var folders = new Folders("Books");
            Steps.WriteEpub(Path.Combine(folders.Library, "Books", "translated-export.epub"), "Translated Export", "Local fixture", paragraphsPerChapter: 3);
            await using var ollama = ReaderTranslationTests.FakeOllama.Start(echo: true);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Storage:LocalPaths:0"] = folders.Library,
                ["LocalTranslation:Endpoint"] = ollama.Address.ToString(),
                ["HTTPS_PROXY"] = "http://127.0.0.1:9"
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            try
            {
                await ReadingPathTests.AddBookAsync(page, folders, "Translated Export");
                await jularr.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
                await ReaderTranslationTests.CheckMachinesAsync(page);

                // The book is prepared by opening it once, and its reader wants it in German.
                var opened = await page.GotoAsync("/Read/1");
                Assert.AreEqual(200, opened!.Status);
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Chapter One");
                await jularr.RunSqlAsync("""
                    INSERT INTO "ReadingPreferences" ("ProfileId", "WorkId", "Language")
                    SELECT profile."Id", asset."WorkId", 'de' FROM "Profiles" AS profile, "MediaAssets" AS asset WHERE asset."Id" = 1
                    ON CONFLICT ("ProfileId", "WorkId") DO UPDATE SET "Language" = 'de'
                    """);

                // The job translates it; the list shows the work with its model and, once every section is done, the EPUB.
                await page.GotoAsync("/Admin/Jobs?q=translations");
                var job = page.Locator(".list-row", new() { HasText = "Book translations" });
                await Steps.FollowAsync(page, job.GetByRole(AriaRole.Button, new() { Name = "Run now", Exact = true }));
                var download = page.Locator("[data-translated-epub=de]");
                var deadline = DateTime.UtcNow.AddMinutes(3);
                while (true)
                {
                    await page.GotoAsync("/Admin/Instance/Translation?tab=books");
                    if (await download.CountAsync() > 0) break;
                    Assert.IsTrue(DateTime.UtcNow < deadline, "The book was not translated: " + await page.Locator(".translation-books").InnerTextAsync());
                    await Task.Delay(2000);
                }

                var row = page.Locator(".translation-books .list-row", new() { HasText = "Translated Export" });
                await Assertions.Expect(row).ToContainTextAsync("ollama:translategemma:4b");
                await Assertions.Expect(row).ToContainTextAsync("2 of 2 sections");
                await Evidence.SaveAsync(page, "translated-works-desktop");
                var fromList = await page.RunAndWaitForDownloadAsync(() => download.ClickAsync());
                await AssertEpubAsync(fromList);

                // On a phone the row is a card, nothing runs wider than the screen and the download is a full tap target.
                await page.SetViewportSizeAsync(390, 844);
                await page.ReloadAsync();
                await Assertions.Expect(download).ToBeVisibleAsync();
                Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"), "The list runs wider than the phone.");
                var box = (await download.BoundingBoxAsync())!;
                Assert.IsTrue(box.X >= 0 && box.X + box.Width <= 390, box.X + "/" + box.Width);
                await AssertTapAsync(download);
                await Evidence.SaveAsync(page, "translated-works-phone");

                // The Reader offers the same EPUB in its download panel.
                await page.SetViewportSizeAsync(1280, 900);
                await page.GotoAsync("/Read/1");
                await page.WaitForFunctionAsync("() => document.querySelector('.reader')?.dataset.bookReady !== undefined");
                await page.Locator("[data-action=more]").ClickAsync();
                await page.Locator("[data-panel=more] [data-action=download]").ClickAsync();
                var reader = page.Locator("[data-panel=download] a[href*='ebook?language=de']");
                await Assertions.Expect(reader).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "translated-epub-reader-desktop");
                await AssertEpubAsync(await page.RunAndWaitForDownloadAsync(() => reader.ClickAsync()));

                await page.SetViewportSizeAsync(390, 844);
                await page.GotoAsync("/Read/1");
                await page.WaitForFunctionAsync("() => document.querySelector('.reader')?.dataset.bookReady !== undefined");
                await page.Locator("[data-action=more]").ClickAsync();
                await page.Locator("[data-panel=more] [data-action=download]").ClickAsync();
                await Assertions.Expect(reader).ToBeVisibleAsync();
                await AssertTapAsync(reader);
                await Evidence.SaveAsync(page, "translated-epub-reader-phone");
                Assert.IsFalse(session.Problems.Any(), "Browser errors: " + string.Join(" | ", session.Problems));
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-translated-epub");
                throw;
            }
        }

        // What a finger reaches of the control on a phone, as the phone tap test measures it.
        private static async Task AssertTapAsync(ILocator control)
        {
            var extent = await control.EvaluateAsync<string[]>(PhoneTapTargetsTests.Extent);
            Assert.IsTrue(double.Parse(extent[0], System.Globalization.CultureInfo.InvariantCulture) >= 44 && double.Parse(extent[1], System.Globalization.CultureInfo.InvariantCulture) >= 44,
                string.Join(" ", extent));
        }

        // A German EPUB of the server's translation that names its model.
        private static async Task AssertEpubAsync(IDownload download)
        {
            Assert.AreEqual("Translated Export (de).epub", download.SuggestedFilename);
            using var archive = new ZipArchive(File.OpenRead(await download.PathAsync()));
            using var package = new StreamReader(archive.GetEntry("OEBPS/content.opf")!.Open());
            var opf = await package.ReadToEndAsync();
            StringAssert.Contains(opf, "<dc:language>de</dc:language>");
            StringAssert.Contains(opf, "ollama:translategemma:4b");
            using var first = new StreamReader(archive.GetEntry("OEBPS/s00000.xhtml")!.Open());
            StringAssert.Contains(await first.ReadToEndAsync(), "DE Chapter One");
            Assert.IsNotNull(archive.GetEntry("OEBPS/colophon.xhtml"));
        }
    }
}
