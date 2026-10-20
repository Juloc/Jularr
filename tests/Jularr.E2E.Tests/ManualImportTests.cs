using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class ManualImportTests
    {
        // A folder nobody downloaded for Jularr is chosen on a connected storage, read with the name each file would get, checked and placed as the film.
        [TestMethod]
        [Timeout(600_000)]
        public async Task Film_FromAFolderOnTheStorage_IsReadCheckedAndPlacedInTheLibrary()
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

                // The film is found by its title.
                await page.GotoAsync("/Admin/ManualImport");
                await page.Locator("form [name=Query]").FillAsync("Bunny");
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Search"));
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Import to this", Exact = true }));

                // The folder is chosen on the storage connection and its files are read.
                await page.GetByRole(AriaRole.Link, new() { Name = "Browse folders" }).ClickAsync();
                var picker = page.GetByRole(AriaRole.Dialog, new() { Name = "Browse folders", Exact = true });
                await picker.Locator("a", new() { HasText = "Library" }).First.ClickAsync();
                await picker.GetByRole(AriaRole.Link, new() { Name = "Incoming", Exact = true }).ClickAsync();
                await picker.GetByRole(AriaRole.Link, new() { Name = "Release", Exact = true }).ClickAsync();
                await picker.GetByRole(AriaRole.Button, new() { Name = "Use this folder", Exact = true }).ClickAsync();
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Show files"));

                // Its name, quality and the name the library would give it show; the film is ticked.
                var row = page.Locator(".ui-form__row").First;
                await Assertions.Expect(row).ToContainTextAsync("Big.Buck.Bunny.2008.1080p.WEB-DL.H264-JLR.webm");
                await Assertions.Expect(row).ToContainTextAsync("→ Big Buck Bunny (2008)");
                await Assertions.Expect(row.Locator("input[type=checkbox]")).ToBeCheckedAsync();
                await Evidence.SaveAsync(page, "manual-import-files");

                await Steps.SubmitAsync(page, Steps.FormWith(page, "Import"));
                await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Imported files: 1" })).ToBeVisibleAsync();
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Movies"), "*.webm", SearchOption.AllDirectories).Count(), "The film was not placed once.");
                Assert.IsTrue(File.Exists(source), "A hardlinked import leaves the original.");
                await Steps.WaitForAvailableAsync(page, FakeServices.MovieTitle);
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-manual-import");
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
                ILocator? waiting = null;
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (waiting is null)
                {
                    Assert.IsTrue(deadline.Elapsed < TimeSpan.FromMinutes(6), "The download did not wait in the queue within six minutes.");
                    await page.GotoAsync("/Admin/Activity?Tab=Queue");
                    var row = page.Locator("tr", new() { HasText = "does not clearly match the film" });
                    if (await row.CountAsync() > 0) waiting = row.First;
                    else await page.WaitForTimeoutAsync(3000);
                }

                await Steps.FollowAsync(page, waiting.GetByRole(AriaRole.Link, new() { Name = "Import by hand", Exact = true }));
                var file = page.Locator(".ui-form__row").First;
                await Assertions.Expect(file).ToContainTextAsync("Big.Buck.Bunny.2005.1080p.WEB-DL.H264-JLR.webm");
                await Assertions.Expect(file.Locator("input[type=checkbox]")).ToBeCheckedAsync();
                await Evidence.SaveAsync(page, "manual-import-queue");
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Import"));

                // It leaves the queue's waiting entries and the film is in the library.
                await Assertions.Expect(page.Locator("tr", new() { HasText = "does not clearly match the film" })).ToHaveCountAsync(0);
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
    }
}
