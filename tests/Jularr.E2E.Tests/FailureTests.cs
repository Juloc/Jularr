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
                var queued = Steps.ListRow(page, FakeServices.MovieTitle);
                await Assertions.Expect(queued).ToContainTextAsync("Downloading");
                await Assertions.Expect(queued).ToContainTextAsync("40 %");

                // The client moves on: the queue and the request show it without a reload.
                await page.EvaluateAsync("window.liveMarker = 1");
                fakes.HeldPercent = 70;
                var slow = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
                await Assertions.Expect(queued).ToContainTextAsync("70 %", slow);
                await Assertions.Expect(queued.Locator("progress")).ToHaveAttributeAsync("value", "70");

                // The tiles count the running download and follow it too.
                await Assertions.Expect(page.Locator("[data-live=queue][data-live-text=active]")).ToContainTextAsync("1", slow);
                await Assertions.Expect(page.Locator("[data-live=queue][data-live-text=errors]")).ToContainTextAsync("0", slow);
                Assert.AreEqual(1, await page.EvaluateAsync<int>("window.liveMarker ?? 0"), "The queue was reloaded instead of updated.");
                await page.GotoAsync("/Requests");
                var request = page.Locator(".request-row", new() { HasText = FakeServices.MovieTitle });
                await Assertions.Expect(request).ToContainTextAsync("Downloading 70 %", slow);
                await page.EvaluateAsync("window.liveMarker = 2");
                fakes.HeldPercent = 90;
                await Assertions.Expect(request).ToContainTextAsync("Downloading 90 %", slow);
                Assert.AreEqual(2, await page.EvaluateAsync<int>("window.liveMarker ?? 0"), "The requests were reloaded instead of updated.");
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

                // A broken download lands in the queue; after Retry the broken release is blocked and the next good one is taken.
                fakes.FailDownloads = true;
                fakes.Offer(Film with { File = video }, LowerFilm with { File = video });
                await Steps.OpenSearchAsync(page, FakeServices.MovieTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                await Steps.GrabAsync(page, Film);
                var failed = await WaitForFailedDownloadAsync(page);
                fakes.FailDownloads = false;

                // The error tile counts it and the Errors filter lists it, next to the history.
                await Assertions.Expect(page.Locator("[data-live=queue][data-live-text=errors]")).ToHaveTextAsync("1");
                await Steps.FollowAsync(page, page.Locator(".list-toolbar").GetByRole(AriaRole.Link, new() { Name = "Errors", Exact = false }));
                await Assertions.Expect(Steps.ListRow(page, "Download failed")).ToHaveCountAsync(1);
                var filters = page.GetByRole(AriaRole.Navigation, new() { Name = "Queue states", Exact = true });
                foreach (var filter in new[] { "All", "Active", "Waiting", "Import", "Errors" })
                {
                    var address = filter == "All" ? "/Admin/Activity" : "/Admin/Activity?Filter=" + filter;
                    await Assertions.Expect(filters.Locator(".navigation-tabs__divider ~ a[href='" + address + "']")).ToHaveCountAsync(1);
                }
                await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "History", Exact = true }).And(page.Locator(".view-switch__link")))
                    .ToHaveAttributeAsync("href", "/Admin/Activity?Filter=History");
                await Steps.SubmitAsync(page, failed.Locator("form").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Retry" }) }));
                await Steps.OpenSearchAsync(page, FakeServices.MovieTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                await Assertions.Expect(page.Locator("tr", new() { HasText = Film.Title })).ToContainTextAsync("Failed or blocked before");

                // The retried search may take it in the background first; then the page says it is already downloading.
                if (!fakes.Grabbed.Contains(fakes.NzbAddress(LowerFilm).ToString()))
                {
                    await Steps.FollowAsync(page, page.Locator("tr", new() { HasText = LowerFilm.Title }).Locator("form")
                        .Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Select and download" }) }).Locator("button[type=submit]"));
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

        // A download whose files do not clearly name the film waits in the queue with its reason; removing and blocking it takes the next good release at once.
        [TestMethod]
        [Timeout(600_000)]
        public async Task UnclearDownload_WaitsInTheQueue_AndRemoveAndBlockTakesTheNextRelease()
        {
            using var folders = new Folders("Movies");
            var video = await Steps.WriteVideoAsync(null, Path.Combine(folders.Fixtures, "video.webm"));

            // The first release holds a file of another year.
            var wrong = Path.Combine(folders.Fixtures, "wrong-year");
            Directory.CreateDirectory(wrong);
            File.Copy(video, Path.Combine(wrong, "Big.Buck.Bunny.2005.1080p.WEB-DL.H264-JLR.webm"));
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await StartAsync(fakes, folders);
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.SetTmdbKeyAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Movies", "Movie"));
                await Steps.AddUsenetAsync(page, fakes, "2000,2040", searchesAutomatically: true);

                // Nobody searches by hand: the only release on offer is taken.
                fakes.Offer(Film with { File = wrong });
                await Steps.RequestAsync(page, "Bunny", FakeServices.MovieTitle);
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (!fakes.Grabbed.Contains(fakes.NzbAddress(Film).ToString()))
                {
                    Assert.IsTrue(waited.Elapsed < TimeSpan.FromMinutes(6), "The release was not taken automatically within six minutes.");
                    await Task.Delay(3000);
                }

                // The doubt shows with its reason and the actions that decide it; there is nothing to pause while it waits.
                var waiting = await WaitForQueueRowAsync(page, "does not clearly match the film");
                await Assertions.Expect(waiting.GetByRole(AriaRole.Button, new() { Name = "Retry import", Exact = true })).ToBeVisibleAsync();
                await Assertions.Expect(waiting.GetByRole(AriaRole.Button, new() { Name = "Pause", Exact = true })).ToHaveCountAsync(0);
                await Evidence.SaveAsync(page, "queue-waiting");

                // Removing is in the entry's panel and asks first: the summary opens the button that deletes the partial files.
                await waiting.GetByRole(AriaRole.Link, new() { Name = "Details", Exact = true }).ClickAsync();
                var panel = page.Locator("dialog.drawer[open]");
                await Assertions.Expect(panel.Locator("details.activity-confirm summary", new() { HasText = "Remove" }).First).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "queue-panel");
                fakes.Offer(LowerFilm with { File = video });
                await panel.Locator("details.activity-confirm summary", new() { HasText = "Remove and block" }).ClickAsync();
                await Steps.SubmitAsync(page, panel.Locator("form").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Remove, delete its partial files and block this release" }) }));
                CollectionAssert.AreEqual(new[] { "queue:delete:SABnzbd_nzo_bunny", "history:delete:SABnzbd_nzo_bunny" }, fakes.Commands.ToArray());

                // The blocked release is never taken again; the next one is searched at once and imports.
                waited.Restart();
                while (!fakes.Grabbed.Contains(fakes.NzbAddress(LowerFilm).ToString()))
                {
                    Assert.IsTrue(waited.Elapsed < TimeSpan.FromMinutes(6), "The next release was not taken within six minutes.");
                    await Task.Delay(3000);
                }

                var details = await Steps.WaitForAvailableAsync(page, FakeServices.MovieTitle);
                CollectionAssert.AreEqual(new[] { fakes.NzbAddress(Film).ToString(), fakes.NzbAddress(LowerFilm).ToString() }, fakes.Grabbed.ToArray());
                await page.GotoAsync("/Admin/Activity?Filter=History");
                await Assertions.Expect(Steps.ListRow(page, "Removed from the queue")).ToHaveCountAsync(1);
                Assert.IsTrue(details.Length > 0);
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-unclear-download");
                throw;
            }
        }

        // The queue row with this reason once the download monitor has read the finished download.
        private static async Task<ILocator> WaitForQueueRowAsync(IPage page, string reason)
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (DateTime.UtcNow < deadline)
            {
                await page.GotoAsync("/Admin/Activity");
                var row = Steps.ListRow(page, reason);
                if (await row.CountAsync() > 0)
                {
                    return row.First;
                }

                await page.WaitForTimeoutAsync(3000);
            }

            throw new AssertFailedException("No download waited with '" + reason + "' within two minutes.");
        }

        private static Task<JularrInstance> StartAsync(FakeServices fakes, Folders folders) => JularrInstance.StartAsync(new Dictionary<string, string>
        {
            ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
            ["Providers:Tvdb"] = fakes.Tvdb4.ToString(),
            ["Providers:OpenLibrary"] = fakes.OpenLibrary.ToString(),
            ["Providers:AniList"] = fakes.AniList.ToString(),
            ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
            ["Providers:Images:artworks.thetvdb.com"] = fakes.Images.ToString(),
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

        // The queue row once the download monitor reports the failure.
        private static async Task<ILocator> WaitForFailedDownloadAsync(IPage page)
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (DateTime.UtcNow < deadline)
            {
                await page.GotoAsync("/Admin/Activity");
                var row = Steps.ListRow(page, "Download failed");
                if (await row.CountAsync() > 0)
                {
                    return row.First;
                }

                // The download monitor runs every fifteen seconds.
                await page.WaitForTimeoutAsync(3000);
            }

            throw new AssertFailedException("No failed download in the queue within two minutes.");
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
            await Steps.FollowAsync(page, page.Locator(".feature-card", new() { HasText = "Library" }).GetByRole(AriaRole.Button, new() { Name = "Check now", Exact = true }));
            return await page.Locator(".feature-card", new() { HasText = "Library" }).InnerTextAsync();
        }
    }
}
