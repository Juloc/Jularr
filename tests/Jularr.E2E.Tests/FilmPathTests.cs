using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // A film from an empty instance to playing it again after signing in anew, only through the browser and fake services.
    [TestClass]
    public sealed class FilmPathTests
    {
        private static readonly TimeSpan MediaTimeout = TimeSpan.FromSeconds(30);

        [ClassInitialize]
        public static void InstallBrowser(TestContext _)
        {
            if (Microsoft.Playwright.Program.Main(["install", "chromium"]) != 0)
            {
                JularrInstance.Unavailable("Chromium for Playwright could not be installed.");
            }
        }

        [TestMethod]
        [Timeout(600_000)]
        public async Task Film_FromSetupToRequestDownloadImportPlayAndResume()
        {
            var ffmpeg = Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG");
            using var folders = new Folders();
            var film = await WriteFilmAsync(ffmpeg, folders.Fixtures);
            await using var fakes = await FakeServices.StartAsync(folders.Downloads, film);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library,
                ["Media:FfmpegPath"] = ffmpeg ?? "",
                ["Media:FfprobePath"] = ffmpeg is null ? "" : Path.Combine(Path.GetDirectoryName(ffmpeg)!, Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.Ordinal))
            });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync();
            var context = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = jularr.Address.ToString() });
            var page = await context.NewPageAsync();
            var problems = Watch(page);

            try
            {
                await Owner.SetUpAndSignInAsync(context, page);

                // TMDB answers through its key; the library is a local folder with a film path.
                await page.GotoAsync("/Admin/Providers");
                var tmdb = page.Locator("form[action*='handler=Secret']:not(:has([name=ClientId]))");
                await tmdb.Locator("[name=Secret]").FillAsync(FakeServices.TmdbToken);
                await Submit(page, tmdb);
                await page.GotoAsync("/Admin/Storage");
                await page.GetByRole(AriaRole.Link, new() { Name = "Add local path" }).ClickAsync();
                await page.FillAsync("form [name=Name]", "Library");
                await page.SelectOptionAsync("form [name=SharePath]", folders.Library);
                await page.SelectOptionAsync("form [name=IsReadOnly]", "False");
                await Submit(page, page.Locator("form", new() { Has = page.Locator("[name=SharePath]") }));
                await page.GetByRole(AriaRole.Link, new() { Name = "Browse folders" }).ClickAsync();
                await page.GetByRole(AriaRole.Link, new() { Name = "Movies", Exact = true }).ClickAsync();
                var root = FormWith(page, "Add library path");
                await root.Locator("[name=Name]").FillAsync("Movies");
                await root.Locator("[name=MediaTypeId]").SelectOptionAsync(new SelectOptionValue { Label = "Movie" });
                await Submit(page, root);
                StringAssert.Contains(await page.Locator("main table").Last.InnerTextAsync(), "/Movies");

                // The indexer and the download client are the fake ones; finished jobs appear under the downloads folder.
                await page.GotoAsync("/Admin/Acquisition");
                await page.GetByRole(AriaRole.Link, new() { Name = "Add indexer" }).ClickAsync();
                await page.FillAsync("form [name=Name]", "Indexer");
                await page.FillAsync("form [name=BaseUrl]", new Uri(fakes.Address, "newznab/").ToString());
                await page.FillAsync("form [name=ApiKey]", FakeServices.IndexerKey);
                await page.FillAsync("form [name=Categories]", "2000,2040");
                await Submit(page, page.Locator("form", new() { Has = page.Locator("[name=BaseUrl]") }));
                await page.GotoAsync("/Admin/Acquisition");
                await page.GetByRole(AriaRole.Link, new() { Name = "Add download client" }).ClickAsync();
                await page.FillAsync("form [name=Name]", "SABnzbd");
                await page.FillAsync("form [name=BaseUrl]", new Uri(fakes.Address, "sabnzbd/").ToString());
                await page.FillAsync("form [name=ApiKey]", FakeServices.ClientKey);
                await page.FillAsync("form [name=RemotePath]", "/downloads");
                await page.FillAsync("form [name=LocalPath]", folders.Downloads);
                await Submit(page, page.Locator("form", new() { Has = page.Locator("[name=BaseUrl]") }));

                // The film is found in Discover and requested; the Owner's request is approved at once.
                await page.GotoAsync("/?q=Bunny");
                var card = page.Locator(".media-card", new() { HasText = FakeServices.MovieTitle });
                await card.HoverAsync();
                await card.GetByRole(AriaRole.Link, new() { Name = "Request this work" }).ClickAsync();
                await Submit(page, FormWith(page, "Request this work"));

                // Wanted searches the indexer and shows every candidate with its decision; the chosen one is downloaded.
                fakes.OfferReleases();
                await page.GotoAsync("/Admin/Wanted");
                await page.Locator("tr", new() { HasText = FakeServices.MovieTitle }).GetByRole(AriaRole.Link, new() { Name = "Open" }).ClickAsync();
                await Submit(page, FormWith(page, "Start search"));
                StringAssert.Contains(await page.Locator("tr", new() { HasText = FakeServices.OtherRelease }).InnerTextAsync(), "Different title");
                StringAssert.Contains(await page.Locator("tr", new() { HasText = FakeServices.Release }).InnerTextAsync(), "Only eligible release");
                await Submit(page, page.Locator("tr", new() { HasText = FakeServices.Release }).Locator("form")
                    .Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Select and download" }) }));
                CollectionAssert.AreEqual(new[] { new Uri(fakes.Address, "newznab/getnzb/bunny").ToString() }, fakes.Grabbed.ToArray());

                // The download monitor imports the finished job into the library.
                var details = await WaitForAvailableAsync(page);
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Movies"), "*.webm", SearchOption.AllDirectories).Count(), "The film was not imported once.");

                // Playing needs a real file; FFmpeg makes one.
                if (ffmpeg is null)
                {
                    JularrInstance.Unavailable("JULARR_TEST_FFMPEG is not configured; playback was not checked.");
                }

                await page.GotoAsync(details);
                await page.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).First.ClickAsync();
                await page.WaitForFunctionAsync("() => document.querySelector('video')?.readyState >= 1", null, new() { Timeout = (float)MediaTimeout.TotalMilliseconds });
                await page.Locator("video").EvaluateAsync("video => { video.currentTime = 4; return video.play(); }");
                await page.WaitForFunctionAsync("() => document.querySelector('video').currentTime > 5", null, new() { Timeout = (float)MediaTimeout.TotalMilliseconds });

                // Pausing saves the position.
                await page.RunAndWaitForResponseAsync(() => page.Locator("video").EvaluateAsync("video => video.pause()"),
                    response => response.Request.Method == "PUT" && response.Ok, new() { Timeout = (float)MediaTimeout.TotalMilliseconds });

                // Signed out and in again, the film continues where it stopped.
                await page.GotoAsync("/Account/Logout");
                await Submit(page, FormWith(page, "Sign out"));
                await page.GotoAsync("/Account/Login");
                await page.FillAsync("[name=Email]", Owner.Email);
                await page.FillAsync("[name=Password]", Owner.Password);
                await page.Locator("form button[type=submit]").First.ClickAsync();
                await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));
                await page.GotoAsync(details);
                await page.GetByRole(AriaRole.Link, new() { Name = "Continue at" }).First.ClickAsync();
                await page.WaitForFunctionAsync("() => document.querySelector('video')?.readyState >= 1", null, new() { Timeout = (float)MediaTimeout.TotalMilliseconds });
                var resumed = await page.Locator("video").EvaluateAsync<double>("video => video.currentTime");
                Assert.IsTrue(resumed >= 4.5, "Playback started at " + resumed + " s instead of continuing.");

                CollectionAssert.AreEqual(Array.Empty<string>(), problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "film-path");
                throw;
            }
        }

        // The details page of the requested film once it reads available.
        private static async Task<string> WaitForAvailableAsync(IPage page)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromMinutes(2))
            {
                await page.GotoAsync("/Library");
                var card = page.GetByRole(AriaRole.Link, new() { Name = FakeServices.MovieTitle });
                if (await card.CountAsync() > 0)
                {
                    await card.First.ClickAsync();
                    if (await page.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).CountAsync() > 0)
                    {
                        return new Uri(page.Url).PathAndQuery;
                    }
                }

                // The download monitor runs every fifteen seconds.
                await page.WaitForTimeoutAsync(3000);
            }

            Assert.Fail("The film did not become available within two minutes.");
            return "";
        }

        // A short test pattern with sound, as WebM the browser plays directly; without FFmpeg only bytes for the import.
        private static async Task<string> WriteFilmAsync(string? ffmpeg, string folder)
        {
            var file = Path.Combine(folder, "film.webm");
            if (ffmpeg is null)
            {
                await File.WriteAllBytesAsync(file, new byte[64 * 1024]);
                return file;
            }

            using var process = Process.Start(new ProcessStartInfo(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=24:duration=10",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=10", "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-b:v", "200k",
                "-c:a", "libopus", "-shortest", "-y", file])
            { RedirectStandardError = true })!;
            var errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(0, process.ExitCode, errors);
            return file;
        }

        private static ILocator FormWith(IPage page, string button) =>
            page.Locator("main form").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = button, Exact = true }) });

        // Submits, waits for the answering page and fails on the error it shows.
        private static async Task Submit(IPage page, ILocator form)
        {
            await page.RunAndWaitForNavigationAsync(() => form.Locator("button[type=submit]").Last.ClickAsync());
            var alert = page.Locator("main [role=alert]");
            if (await alert.CountAsync() > 0)
            {
                Assert.Fail("The form answered: " + await alert.First.InnerTextAsync());
            }
        }

        private static ConcurrentQueue<string> Watch(IPage page)
        {
            var problems = new ConcurrentQueue<string>();
            page.Console += (_, message) =>
            {
                if (message.Type == "error") problems.Enqueue(page.Url + ": " + message.Text);
            };
            page.PageError += (_, error) => problems.Enqueue(page.Url + ": " + error);
            page.Response += (_, response) =>
            {
                if (response.Status >= 500) problems.Enqueue(response.Url + ": HTTP " + response.Status);
            };
            return problems;
        }

        // Fixture, download and library folders; disposed after the instance, which may still hold files.
        private sealed class Folders : IDisposable
        {
            private readonly string _root = Directory.CreateTempSubdirectory("jularr-e2e-film-").FullName;

            public Folders()
            {
                Directory.CreateDirectory(Fixtures);
                Directory.CreateDirectory(Downloads);
                Directory.CreateDirectory(Path.Combine(Library, "Movies"));
            }

            public string Fixtures => Path.Combine(_root, "fixtures");
            public string Downloads => Path.Combine(_root, "downloads");
            public string Library => Path.Combine(_root, "library");

            public void Dispose()
            {
                try
                {
                    Directory.Delete(_root, true);
                }
                catch (IOException exception)
                {
                    Evidence.Log("[test] " + _root + " stays: " + exception.Message);
                }
            }
        }
    }
}
