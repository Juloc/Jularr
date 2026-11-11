using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class ManualImportTests
    {
        // A folder nobody downloaded for Jularr is chosen on a connected storage from the queue; its film is recognised by the file's name, and importing moves it into the library
        // while its row shows what happens and where it went. The former address leads to the same page.
        [TestMethod]
        [Timeout(600_000)]
        public async Task Film_FromAFolderOnTheStorage_IsRecognisedAndMovedIntoTheLibrary()
        {
            var ffmpeg = Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG");
            using var folders = new Folders("Movies", "Incoming");
            var video = await Steps.WriteVideoAsync(ffmpeg, Path.Combine(folders.Fixtures, "video.webm"));
            var release = Path.Combine(folders.Library, "Incoming", "Release");
            Directory.CreateDirectory(release);
            var source = Path.Combine(release, "Big.Buck.Bunny.2008.1080p.WEB-DL.H264-JLR.webm");
            File.Copy(video, source);
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Tvdb"] = fakes.Tvdb4.ToString(),
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Providers:Images:artworks.thetvdb.com"] = fakes.Images.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library,
                ["Media:FfmpegPath"] = ffmpeg ?? "",
                ["Media:FfprobePath"] = Steps.FfprobeBeside(ffmpeg) ?? ""
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.SetTmdbKeyAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Movies", "Movie"));
                await Steps.RequestAsync(page, "Bunny", FakeServices.MovieTitle);

                // The former address opens the import, a dialog over the queue, which the queue's dock opens for any folder.
                await page.GotoAsync("/Admin/ManualImport");
                await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/Admin/Queue\\?import=true$"));
                await page.GotoAsync("/Admin/Queue");
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Manual import", Exact = true }));
                var importDialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Manual import", Exact = true });
                await Assertions.Expect(importDialog).ToBeVisibleAsync();

                // The folder is chosen on the storage connection and its files are read.
                await page.GetByRole(AriaRole.Link, new() { Name = "Browse folders" }).ClickAsync();
                var picker = page.GetByRole(AriaRole.Dialog, new() { Name = "Browse folders", Exact = true });
                await picker.Locator("a", new() { HasText = "Library" }).First.ClickAsync();
                await picker.GetByRole(AriaRole.Link, new() { Name = "Incoming", Exact = true }).ClickAsync();
                await picker.GetByRole(AriaRole.Link, new() { Name = "Release", Exact = true }).ClickAsync();
                await picker.GetByRole(AriaRole.Button, new() { Name = "Use this folder", Exact = true }).ClickAsync();
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Show files"));

                // The film is recognised: its title, quality, release group, language and the name the library would give it show; it fits and is chosen.
                var row = page.Locator(".manual-import-files .list-row").First;
                await Assertions.Expect(row).ToContainTextAsync("Big.Buck.Bunny.2008.1080p.WEB-DL.H264-JLR.webm");
                await Assertions.Expect(row).ToContainTextAsync(FakeServices.MovieTitle);
                await Assertions.Expect(row).ToContainTextAsync("→ Big Buck Bunny (2008)");
                await Assertions.Expect(row).ToContainTextAsync("WEBDL-1080p");
                await Assertions.Expect(row).ToContainTextAsync("JLR");
                await Assertions.Expect(row).ToContainTextAsync("EN");
                await Assertions.Expect(row).ToContainTextAsync("Fits");
                await Assertions.Expect(row.Locator("input[type=checkbox]")).ToBeCheckedAsync();

                // The files take the dialog's whole width, though only its content was swapped.
                var box = await importDialog.BoundingBoxAsync();
                Assert.IsTrue(box is { Width: > 700 }, "The dialog stayed as small as the folder step: " + box?.Width);
                await Evidence.SaveAsync(page, "manual-import-files");

                // A tablet and a phone show the same dialog within their screen, the file as a card and Import at the bottom.
                var desktop = page.ViewportSize!;
                foreach (var (width, height, name) in new[] { (768, 1024, "tablet"), (375, 812, "phone") })
                {
                    await page.SetViewportSizeAsync(width, height);
                    var fits = await importDialog.BoundingBoxAsync();
                    Assert.IsTrue(fits is { X: >= 0 } && fits.X + fits.Width <= width + 1, name + ": the dialog leaves the screen.");
                    await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Import", Exact = true })).ToBeInViewportAsync();
                    await Evidence.SaveAsync(page, "manual-import-files-" + name);
                }

                await page.SetViewportSizeAsync(desktop.Width, desktop.Height);

                // The match dialog gives it another quality; the name the library would give follows.
                await Steps.FollowAsync(page, row.GetByRole(AriaRole.Button, new() { Name = "Change", Exact = true }));
                var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Match file", Exact = true });
                await Assertions.Expect(dialog).ToBeVisibleAsync();
                await dialog.Locator("select[name=MatchedQualityKey]").SelectOptionAsync("Bluray-1080p");
                await Evidence.SaveAsync(page, "manual-import-match");
                await Steps.FollowAsync(page, dialog.GetByRole(AriaRole.Button, new() { Name = "Apply", Exact = true }));
                await Assertions.Expect(page.GetByRole(AriaRole.Dialog, new() { Name = "Match file", Exact = true })).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator(".manual-import-files .list-row").First).ToContainTextAsync("Bluray-1080p");

                // Importing moves the chosen file: its row says where it went, the figures count it, and the library holds it while the folder no longer does.
                await page.GetByRole(AriaRole.Button, new() { Name = "Import", Exact = true }).ClickAsync();
                row = page.Locator(".manual-import-files .list-row").First;
                await Assertions.Expect(row).ToContainTextAsync("Moved to", new() { Timeout = 60_000 });
                await Assertions.Expect(row).ToContainTextAsync("/Movies/Big Buck Bunny (2008)");
                await Assertions.Expect(page.Locator("[data-manual-count=imported]")).ToHaveTextAsync("1");
                await Assertions.Expect(page.Locator("[data-manual-count=failed]")).ToHaveTextAsync("0");
                await Evidence.SaveAsync(page, "manual-import-report");
                var placed = Directory.EnumerateFiles(Path.Combine(folders.Library, "Movies"), "*.webm", SearchOption.AllDirectories).ToArray();
                Assert.AreEqual(1, placed.Length, "The film was not placed once.");
                StringAssert.Contains(Path.GetFileName(placed[0]), "Bluray-1080p", "The quality a person gave names the file.");
                Assert.IsFalse(File.Exists(source), "An import moves its file.");
                await Steps.WaitForAvailableAsync(page, FakeServices.MovieTitle);

                // The history keeps who assigned it.
                await page.GotoAsync("/Admin/Queue/History");
                await Assertions.Expect(page.Locator("main")).ToContainTextAsync("Assigned by hand");
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-manual-import");
                throw;
            }
        }

        // The two files of one waiting download are matched to two titles (a film and an episode of a series), one with the languages a person chose; the download finishes and the history assigns both by hand.
        [TestMethod]
        [Timeout(600_000)]
        public async Task WaitingDownload_WithTwoFiles_IsMatchedToTwoTitlesWithALanguage()
        {
            var ffmpeg = Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG");
            using var folders = new Folders("Movies", "Series");
            var video = await Steps.WriteVideoAsync(ffmpeg, Path.Combine(folders.Fixtures, "video.webm"));
            var pack = Path.Combine(folders.Fixtures, "pack");
            Directory.CreateDirectory(pack);
            File.Copy(video, Path.Combine(pack, "Big.Buck.Bunny.2005.1080p.WEB-DL.H264-JLR.webm"));
            File.WriteAllBytes(Path.Combine(pack, "second.webm"), new byte[2048]);
            var film = new FakeRelease("Big.Buck.Bunny.2008.1080p.WEB-DL.H264-JLR", "bunny", 2040, pack);
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Tvdb"] = fakes.Tvdb4.ToString(),
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Providers:Images:artworks.thetvdb.com"] = fakes.Images.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library,
                ["Media:FfmpegPath"] = ffmpeg ?? "",
                ["Media:FfprobePath"] = Steps.FfprobeBeside(ffmpeg) ?? ""
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.SetTmdbKeyAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Movies", "Movie"), new LibraryPath("Series", "Series"));
                await Steps.AddUsenetAsync(page, fakes, "2000,2040,5000,5040", searchesAutomatically: true);
                fakes.Offer(film);
                await Steps.RequestAsync(page, "Test Series", FakeServices.SeriesTitle);
                await Steps.RequestAsync(page, "Bunny", FakeServices.MovieTitle);

                var waiting = Steps.ListRow(page, "does not clearly match the film").First;
                await Steps.WaitForDownloadMonitorAsync(page, async () =>
                {
                    await page.GotoAsync("/Admin/Queue");
                    return await waiting.CountAsync() > 0;
                }, "The download did not wait in the queue", TimeSpan.FromMinutes(6));

                await Steps.FollowAsync(page, waiting.GetByRole(AriaRole.Link, new() { Name = "Import", Exact = true }));
                var rows = page.Locator(".manual-import-files .list-row");
                await Assertions.Expect(rows).ToHaveCountAsync(2);
                var filmRow = rows.Filter(new() { HasText = "Big.Buck.Bunny.2005" });
                var secondRow = rows.Filter(new() { HasText = "second.webm" });
                await Assertions.Expect(filmRow.Locator("input[type=checkbox]")).ToBeCheckedAsync();

                // The smaller file of the same film wants a look and is not chosen.
                await Assertions.Expect(secondRow).ToContainTextAsync("Please check");
                await Assertions.Expect(secondRow.Locator("input[type=checkbox]")).Not.ToBeCheckedAsync();

                // The film keeps its title; its audio is English and its subtitles German.
                await Steps.FollowAsync(page, filmRow.GetByRole(AriaRole.Button, new() { Name = "Change", Exact = true }));
                var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Match file", Exact = true });
                await dialog.Locator("fieldset", new() { HasText = "Audio languages" }).Locator("label", new() { HasText = "English" }).ClickAsync();
                await dialog.Locator("fieldset", new() { HasText = "Subtitle languages" }).Locator("label", new() { HasText = "German" }).ClickAsync();
                await Evidence.SaveAsync(page, "manual-import-multi-language");
                await Steps.FollowAsync(page, dialog.GetByRole(AriaRole.Button, new() { Name = "Apply", Exact = true }));
                await Assertions.Expect(filmRow).ToContainTextAsync("EN · subtitles DE");

                // The second file is the second episode of the series.
                await Steps.FollowAsync(page, secondRow.GetByRole(AriaRole.Button, new() { Name = "Change", Exact = true }));
                dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Match file", Exact = true });
                await dialog.Locator("input[name=MatchQuery]").FillAsync("Test Series");
                await Steps.FollowAsync(page, dialog.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }));
                dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Match file", Exact = true });
                var series = dialog.Locator("select[name=MatchedWantedItemId]");
                await series.SelectOptionAsync(await series.Locator("option", new() { HasText = FakeServices.SeriesTitle }).First.GetAttributeAsync("value") ?? "");
                await dialog.Locator("input[name=MatchedSeason]").FillAsync("1");
                await dialog.Locator("input[name=MatchedEpisode]").FillAsync("2");
                await Steps.FollowAsync(page, dialog.GetByRole(AriaRole.Button, new() { Name = "Apply", Exact = true }));
                await Assertions.Expect(page.Locator(".manual-import-files .list-row", new() { HasText = "second.webm" })).ToContainTextAsync("S01E02");
                await Assertions.Expect(page.Locator(".manual-import-files .list-row", new() { HasText = "second.webm" }).Locator("input[type=checkbox]")).ToBeCheckedAsync();
                await Evidence.SaveAsync(page, "manual-import-multi-title");

                // Both are imported one after the other; each row says where its file went, the figures count two.
                await page.GetByRole(AriaRole.Button, new() { Name = "Import", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("[data-manual-count=imported]")).ToHaveTextAsync("2", new() { Timeout = 60_000 });
                await Assertions.Expect(page.Locator(".manual-import-files .list-row", new() { HasText = "Big.Buck.Bunny.2005" })).ToContainTextAsync("Moved to");
                await Assertions.Expect(page.Locator(".manual-import-files .list-row", new() { HasText = "second.webm" })).ToContainTextAsync("/Series/");
                await Evidence.SaveAsync(page, "manual-import-multi-report");

                // The download is out of the queue, both titles are in the library and the history assigns each by hand.
                await page.GotoAsync("/Admin/Queue");
                await Assertions.Expect(Steps.ListRow(page, "does not clearly match the film")).ToHaveCountAsync(0);
                await Steps.WaitForAvailableAsync(page, FakeServices.MovieTitle);
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Movies"), "*.webm", SearchOption.AllDirectories).Count());
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Series"), "*.webm", SearchOption.AllDirectories).Count());
                await page.GotoAsync("/Admin/Queue/History");
                await Assertions.Expect(Steps.ListRow(page, "Assigned by hand")).ToHaveCountAsync(2);
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-manual-import-multi");
                throw;
            }
        }

        // A download whose file names another year waits in the queue; the person imports its file by hand from there, and it finishes like any import.
        [TestMethod]
        [Timeout(600_000)]
        public async Task WaitingDownload_FromTheQueue_IsImportedByHand()
        {
            var ffmpeg = Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG");
            using var folders = new Folders("Movies");
            var video = await Steps.WriteVideoAsync(ffmpeg, Path.Combine(folders.Fixtures, "video.webm"));
            var wrong = Path.Combine(folders.Fixtures, "wrong-year");
            Directory.CreateDirectory(wrong);
            File.Copy(video, Path.Combine(wrong, "Big.Buck.Bunny.2005.1080p.WEB-DL.H264-JLR.webm"));
            var film = new FakeRelease("Big.Buck.Bunny.2008.1080p.WEB-DL.H264-JLR", "bunny", 2040, wrong);
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Tvdb"] = fakes.Tvdb4.ToString(),
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Providers:Images:artworks.thetvdb.com"] = fakes.Images.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library,
                ["Media:FfmpegPath"] = ffmpeg ?? "",
                ["Media:FfprobePath"] = Steps.FfprobeBeside(ffmpeg) ?? ""
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.SetTmdbKeyAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Movies", "Movie"));
                await Steps.AddUsenetAsync(page, fakes, "2000,2040", searchesAutomatically: true);
                fakes.Offer(film);
                await Steps.RequestAsync(page, "Bunny", FakeServices.MovieTitle);

                // The doubt shows in the queue with its reason and the way to settle it.
                var waiting = Steps.ListRow(page, "does not clearly match the film").First;
                await Steps.WaitForDownloadMonitorAsync(page, async () =>
                {
                    await page.GotoAsync("/Admin/Queue");
                    return await waiting.CountAsync() > 0;
                }, "The download did not wait in the queue", TimeSpan.FromMinutes(6));

                await Steps.FollowAsync(page, waiting.GetByRole(AriaRole.Link, new() { Name = "Import", Exact = true }));
                var file = page.Locator(".manual-import-files .list-row").First;
                await Assertions.Expect(file).ToContainTextAsync("Big.Buck.Bunny.2005.1080p.WEB-DL.H264-JLR.webm");
                await Assertions.Expect(file.Locator("input[type=checkbox]")).ToBeCheckedAsync();
                await Evidence.SaveAsync(page, "manual-import-queue");
                await page.GetByRole(AriaRole.Button, new() { Name = "Import", Exact = true }).ClickAsync();
                await Assertions.Expect(file).ToContainTextAsync("Moved to", new() { Timeout = 60_000 });

                // It leaves the queue's waiting entries and the film is in the library.
                await page.GotoAsync("/Admin/Queue");
                await Assertions.Expect(Steps.ListRow(page, "does not clearly match the film")).ToHaveCountAsync(0);
                await Steps.WaitForAvailableAsync(page, FakeServices.MovieTitle);
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Movies"), "*.webm", SearchOption.AllDirectories).Count());
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-manual-import-queue");
                throw;
            }
        }

        // A book download nothing of which the import could place waits in the queue with each file's reason; from there the person assigns the file to the book by hand, and it lands under the import's own name.
        [TestMethod]
        [Timeout(600_000)]
        public async Task WaitingBook_FromTheQueue_IsAssignedByHand()
        {
            using var folders = new Folders("Books");
            var refused = Path.Combine(folders.Fixtures, "book.epub");
            File.WriteAllText(refused, "<html>The check refuses this as an EPUB.</html>");
            var book = new FakeRelease("Jane Austen - Pride and Prejudice (epub)", "pride", 7020, refused);
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Tvdb"] = fakes.Tvdb4.ToString(),
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Providers:Images:artworks.thetvdb.com"] = fakes.Images.ToString(),
                ["Providers:OpenLibrary"] = fakes.OpenLibrary.ToString(),
                ["Providers:Images:covers.openlibrary.org"] = fakes.Images.ToString(),
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
                fakes.Offer(book);
                await Steps.OpenSearchAsync(page, FakeServices.BookTitle);
                await Steps.GrabAsync(page, book);

                // The download waits with its reason; the drawer names the file and why.
                var waiting = Steps.ListRow(page, "Nothing in the download could be placed").First;
                await Steps.WaitForDownloadMonitorAsync(page, async () =>
                {
                    await page.GotoAsync("/Admin/Queue");
                    return await waiting.CountAsync() > 0;
                }, "The download did not wait in the queue", TimeSpan.FromMinutes(6));

                await Steps.FollowAsync(page, waiting.GetByRole(AriaRole.Link, new() { Name = "Import", Exact = true }));

                // The file says why it was left out and is not chosen by itself.
                var file = page.Locator(".manual-import-files .list-row").First;
                await Assertions.Expect(file).ToContainTextAsync("Not a valid EPUB");
                await Assertions.Expect(file).ToContainTextAsync("EPUB");
                await Assertions.Expect(file.Locator("input[type=checkbox]")).Not.ToBeCheckedAsync();

                // Matched as the whole book, it is chosen and shows the name the library gives it.
                await Steps.FollowAsync(page, file.GetByRole(AriaRole.Button, new() { Name = "Change", Exact = true }));
                var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Match file", Exact = true });
                await Assertions.Expect(dialog.Locator("input[name=MatchedSeason]")).ToBeVisibleAsync();
                await Assertions.Expect(dialog.Locator("fieldset", new() { HasText = "Subtitle languages" })).ToHaveCountAsync(0);
                await dialog.Locator("fieldset", new() { HasText = "Language" }).Locator("label", new() { HasText = "English" }).ClickAsync();
                await Evidence.SaveAsync(page, "manual-import-book-match");
                await Steps.FollowAsync(page, dialog.GetByRole(AriaRole.Button, new() { Name = "Apply", Exact = true }));
                file = page.Locator(".manual-import-files .list-row").First;
                await Assertions.Expect(file).ToContainTextAsync("Whole work");
                await Assertions.Expect(file).ToContainTextAsync("→ Jane Austen/");
                await Assertions.Expect(file.Locator("input[type=checkbox]")).ToBeCheckedAsync();
                await Evidence.SaveAsync(page, "manual-import-book-files");

                // Imported, the download leaves the queue and the book is in its library.
                await page.GetByRole(AriaRole.Button, new() { Name = "Import", Exact = true }).ClickAsync();
                await Assertions.Expect(file).ToContainTextAsync("Moved to", new() { Timeout = 60_000 });
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Books", "Jane Austen"), "*.epub", SearchOption.AllDirectories).Count());
                await page.GotoAsync("/Admin/Queue");
                await Assertions.Expect(Steps.ListRow(page, "Nothing in the download could be placed")).ToHaveCountAsync(0);
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-manual-import-book");
                throw;
            }
        }
    }
}
