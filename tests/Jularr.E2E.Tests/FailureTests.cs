using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Outages and failures along the film path: each ends visible and recoverable, nothing is lost.
    [TestClass]
    public sealed class FailureTests
    {
        private static readonly FakeRelease Film = new("Big.Buck.Bunny.2008.1080p.WEB-DL.H264-JLR", "bunny", 2040, "");
        private static readonly FakeRelease LowerFilm = new("Big.Buck.Bunny.2008.720p.WEB-DL.H264-JLR", "bunny720", 2040, "");

        [TestMethod]
        [Timeout(600_000)]
        public async Task RestartDuringDownload_AndLostDatabaseConnections_Recover()
        {
            using var folders = new Folders("Movies");
            var video = await Steps.WriteVideoAsync(null, Path.Combine(folders.Fixtures, "video.webm"));
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await StartAsync(fakes, folders);
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await PrepareAsync(page, fakes, folders);

                // The download is still running when Jularr stops.
                fakes.HoldDownloads = true;
                fakes.Offer(Film with { File = video });
                await Steps.OpenSearchAsync(page, FakeServices.MovieTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                await Steps.GrabAsync(page, Film);
                await jularr.RestartAsync();

                // Still signed in; the running job, not a new one, is picked up again and imported once it finishes.
                await page.GotoAsync("/Admin/Activity");
                StringAssert.Contains(await page.Locator("tr", new() { HasText = FakeServices.MovieTitle }).InnerTextAsync(), "Downloading");
                fakes.FinishDownloads();
                var details = await Steps.WaitForAvailableAsync(page, FakeServices.MovieTitle);
                Assert.AreEqual(1, fakes.Grabbed.Count);
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Movies"), "*.webm", SearchOption.AllDirectories).Count());

                // Every database connection ends as in a PostgreSQL restart; pages work again without restarting Jularr.
                await page.GotoAsync("/Library");
                Assert.IsTrue(await jularr.DropConnectionsAsync() > 0, "The host held no database connection to lose.");
                await RecoverAsync(page, details);
                session.Problems.Clear();
                await page.GotoAsync("/Library");
                await page.GotoAsync(details);
                Assert.IsTrue(await page.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).CountAsync() > 0);

                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-restart");
                throw;
            }
        }

        [TestMethod]
        [Timeout(600_000)]
        public async Task FailedDownloadRetries_AndNasOutageKeepsBrowsing()
        {
            using var folders = new Folders("Movies");
            var video = await Steps.WriteVideoAsync(null, Path.Combine(folders.Fixtures, "video.webm"));
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await StartAsync(fakes, folders);
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await PrepareAsync(page, fakes, folders);

                // A broken download lands in To-Do; after Retry the broken release is blocked and the next good one is taken.
                fakes.FailDownloads = true;
                fakes.Offer(Film with { File = video }, LowerFilm with { File = video });
                await Steps.OpenSearchAsync(page, FakeServices.MovieTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                await Steps.GrabAsync(page, Film);
                var failed = await WaitForFailedDownloadAsync(page);
                fakes.FailDownloads = false;
                await Steps.SubmitAsync(page, failed.Locator("form").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Retry" }) }));
                await Steps.OpenSearchAsync(page, FakeServices.MovieTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                StringAssert.Contains(await page.Locator("tr", new() { HasText = Film.Title }).InnerTextAsync(), "Failed or blocked before");

                // The retried search may take it in the background first; then the page says it is already downloading.
                if (!fakes.Grabbed.Contains(fakes.NzbAddress(LowerFilm).ToString()))
                {
                    await page.Locator("tr", new() { HasText = LowerFilm.Title }).Locator("form")
                        .Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Select and download" }) }).Locator("button[type=submit]").ClickAsync();
                }

                var details = await Steps.WaitForAvailableAsync(page, FakeServices.MovieTitle);
                CollectionAssert.AreEqual(new[] { fakes.NzbAddress(Film).ToString(), fakes.NzbAddress(LowerFilm).ToString() }, fakes.Grabbed.ToArray());
                // Taken between the check and the click, the page answers that it is already downloading.
                if (session.Problems.All(problem => problem.Contains("409", StringComparison.Ordinal)))
                {
                    session.Problems.Clear();
                }

                // The library folder disappears: its state says so, browsing goes on, nothing plays.
                var offline = folders.Library + ".offline";
                await MoveAsync(folders.Library, offline);
                StringAssert.Contains(await CheckLibraryAsync(page), "Offline");
                // Playing says why it cannot start; only the refused start itself answers 503.
                await page.GotoAsync(details);
                await page.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).First.ClickAsync();
                var status = page.Locator("[data-role=status]");
                await Assertions.Expect(status).ToHaveTextAsync("The storage holding this file is unavailable.", new() { Timeout = Steps.MediaTimeout });
                Assert.IsTrue(session.Problems.All(problem => problem.Contains("503", StringComparison.Ordinal)), string.Join(Environment.NewLine, session.Problems));
                session.Problems.Clear();

                // Back again, it can be played as before.
                await MoveAsync(offline, folders.Library);
                StringAssert.Contains(await CheckLibraryAsync(page), "Online");
                await page.GotoAsync(details);
                Assert.IsTrue(await page.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).CountAsync() > 0);

                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-download-nas");
                throw;
            }
        }

        private static Task<JularrInstance> StartAsync(FakeServices fakes, Folders folders) => JularrInstance.StartAsync(new Dictionary<string, string>
        {
            ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
            ["Storage:LocalPaths:0"] = folders.Library
        });

        private static async Task PrepareAsync(IPage page, FakeServices fakes, Folders folders)
        {
            await Owner.SetUpAndSignInAsync(page);
            await Steps.SetTmdbKeyAsync(page);
            await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Movies", "Movie"));
            await Steps.AddUsenetAsync(page, fakes, "2000,2040");
            await Steps.RequestAsync(page, "Bunny", FakeServices.MovieTitle);
        }

        // Pages may fail while the connections are gone; within half a minute they answer again.
        private static async Task RecoverAsync(IPage page, string address)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                var response = await page.GotoAsync(address);
                if (response is { Status: < 500 })
                {
                    return;
                }

                Assert.IsTrue(DateTime.UtcNow < deadline, "Jularr did not answer again after the database connections ended.");
                await page.WaitForTimeoutAsync(1000);
            }
        }

        // The To-Do row once the download monitor reports the failure.
        private static async Task<ILocator> WaitForFailedDownloadAsync(IPage page)
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (DateTime.UtcNow < deadline)
            {
                await page.GotoAsync("/Admin/Activity?Tab=ToDo");
                var row = page.Locator("tr", new() { HasText = "Download failed" });
                if (await row.CountAsync() > 0)
                {
                    return row.First;
                }

                // The download monitor runs every fifteen seconds.
                await page.WaitForTimeoutAsync(3000);
            }

            throw new AssertFailedException("No failed download in To-Do within two minutes.");
        }

        // Another process may hold a file inside for a moment.
        private static async Task MoveAsync(string from, string to)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Move(from, to);
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < 10)
                {
                    await Task.Delay(300);
                }
            }
        }

        // Checks the library connection and reports the state it shows.
        private static async Task<string> CheckLibraryAsync(IPage page)
        {
            await page.GotoAsync("/Admin/Storage");
            await page.Locator("tr", new() { HasText = "Library" }).GetByRole(AriaRole.Link, new() { Name = "Open" }).ClickAsync();
            await Steps.SubmitAsync(page, Steps.FormWith(page, "Check mount status"));
            return await page.Locator(".storage__health").InnerTextAsync();
        }
    }
}
