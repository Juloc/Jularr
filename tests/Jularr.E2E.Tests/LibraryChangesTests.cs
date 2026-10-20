using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // A doubtful release waits for a person, a better one replaces the film without losing the old file, and a file gone from disk stops being offered.
    [TestClass]
    public sealed class LibraryChangesTests
    {
        [TestMethod]
        [Timeout(600_000)]
        public async Task AmbiguousRelease_Upgrade_AndMissingFile_StayVisibleAndSafe()
        {
            using var folders = new Folders("Movies");
            var video = await Steps.WriteVideoAsync(null, Path.Combine(folders.Fixtures, "video.webm"));
            var lower = new FakeRelease("Big.Buck.Bunny.2008.720p.WEB-DL.H264-JLR", "bunny720", 2040, video);
            var fanCut = new FakeRelease("Big.Buck.Bunny.Fan.Cut.2008.1080p.WEB-DL.H264-JLR", "fancut", 2040, video);
            var better = new FakeRelease("Big.Buck.Bunny.2008.1080p.WEB-DL.H264-JLR", "bunny", 2040, video);
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            var movies = Path.Combine(folders.Library, "Movies");

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.SetTmdbKeyAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Movies", "Movie"));
                await Steps.AddUsenetAsync(page, fakes, "2000,2040");

                // Replaced files go to the library's recycle bin.
                await page.GotoAsync("/Admin/Library/Versions");
                var replacement = page.Locator("main form", new() { Has = page.Locator("[name=RecycleModeId]") });
                await replacement.Locator("[name=RecycleModeId]").SelectOptionAsync(new SelectOptionValue { Label = "Recycle bin in the library" });
                await Steps.SubmitAsync(page, replacement);
                await Steps.RequestAsync(page, "Bunny", FakeServices.MovieTitle);

                // A title with extra words is left to a person; the plain one is taken.
                fakes.Offer(lower, fanCut);
                await Steps.OpenSearchAsync(page, FakeServices.MovieTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                var doubtful = await page.Locator("tr", new() { HasText = fanCut.Title }).InnerTextAsync();
                StringAssert.Contains(doubtful, "Review");
                StringAssert.Contains(doubtful, "Title has extra words");
                await Steps.GrabAsync(page, lower);
                var details = await Steps.WaitForAvailableAsync(page, FakeServices.MovieTitle);
                var first = Directory.EnumerateFiles(movies, "*.webm", SearchOption.AllDirectories).Single();

                // The better release replaces it; the earlier file waits in the recycle bin.
                fakes.Offer(better);
                await Steps.OpenSearchAsync(page, FakeServices.MovieTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                await Assertions.Expect(page.Locator("tr", new() { HasText = better.Title })).ToContainTextAsync("Eligible");
                await Steps.GrabAsync(page, better);
                await WaitForAsync(() => Directory.Exists(Path.Combine(movies, ".jularr-recycle"))
                    && Directory.EnumerateFiles(Path.Combine(movies, ".jularr-recycle"), "*.webm", SearchOption.AllDirectories).Any(), "The earlier file did not reach the recycle bin.");
                Assert.IsFalse(File.Exists(first), "The earlier file is still in place.");
                var files = Directory.EnumerateFiles(movies, "*.webm", SearchOption.AllDirectories).ToArray();
                Assert.AreEqual(1, files.Count(file => !file.Contains(".jularr-recycle", StringComparison.Ordinal)), string.Join(", ", files));
                Assert.AreEqual(1, files.Count(file => file.Contains(".jularr-recycle", StringComparison.Ordinal)), "The replaced file is not in the recycle bin: " + string.Join(", ", files));
                CollectionAssert.AreEqual(new[] { lower, better }.Select(release => fakes.NzbAddress(release).ToString()).ToArray(), fakes.Grabbed.ToArray());

                // A file deleted outside Jularr is found missing by the next scan and no longer offered.
                await page.GotoAsync(details);
                Assert.IsTrue(await page.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).CountAsync() > 0, "The upgraded film is not offered.");
                File.Delete(files.Single(file => !file.Contains(".jularr-recycle", StringComparison.Ordinal)));
                await page.GotoAsync("/Admin/Storage");
                await Steps.FollowAsync(page, page.Locator("tr", new() { HasText = "Library" }).GetByRole(AriaRole.Link, new() { Name = "Open" }));
                await Steps.SubmitAsync(page, page.Locator("tr", new() { HasText = "/Movies" }).Locator("form").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Scan" }) }));
                await WaitForAsync(async () =>
                {
                    await page.GotoAsync(details);
                    return await page.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).CountAsync() == 0;
                }, "The missing file is still offered.");
                await Assertions.Expect(page.Locator("main")).ToContainTextAsync(FakeServices.MovieTitle);

                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "library-changes");
                throw;
            }
        }

        // Background work (download monitor, scan) finishes within two minutes.
        private static Task WaitForAsync(Func<bool> done, string failure) => WaitForAsync(() => Task.FromResult(done()), failure);

        private static async Task WaitForAsync(Func<Task<bool>> done, string failure)
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (!await done())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new AssertFailedException(failure);
                }

                await Task.Delay(2000);
            }
        }
    }
}
