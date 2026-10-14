using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // What an administrator and a viewer do in the browser, shared by the path tests.
    internal static class Steps
    {
        public static readonly float MediaTimeout = 30_000;

        // Browser errors, script errors and server faults a page caused.
        public static ConcurrentQueue<string> Watch(IPage page)
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

        public static ILocator FormWith(IPage page, string button) =>
            page.Locator("main form").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = button, Exact = true }) });

        // Submits, waits for the answering page and fails on the error it shows.
        public static async Task SubmitAsync(IPage page, ILocator form)
        {
            await page.RunAndWaitForResponseAsync(() => form.Locator("button[type=submit]").Last.ClickAsync(),
                response => response.Request.IsNavigationRequest && response.Request.Method == "POST");
            await page.WaitForLoadStateAsync();
            var alert = page.Locator("main [role=alert]");
            if (await alert.CountAsync() > 0)
            {
                Assert.Fail("The form answered: " + await alert.First.InnerTextAsync());
            }
        }

        public static async Task SetTmdbKeyAsync(IPage page)
        {
            await page.GotoAsync("/Admin/Providers");
            var tmdb = page.Locator("form[action*='handler=Secret']:not(:has([name=ClientId]))");
            await tmdb.Locator("[name=Secret]").FillAsync(FakeServices.TmdbToken);
            await SubmitAsync(page, tmdb);
        }

        // The local folder as a writable connection with one library path per media type folder.
        public static async Task AddLibraryAsync(IPage page, string localPath, params (string Folder, string Type)[] roots)
        {
            await page.GotoAsync("/Admin/Storage");
            await page.GetByRole(AriaRole.Link, new() { Name = "Add local path" }).ClickAsync();
            await page.FillAsync("form [name=Name]", "Library");
            await page.SelectOptionAsync("form [name=SharePath]", localPath);
            await page.SelectOptionAsync("form [name=IsReadOnly]", "False");
            await SubmitAsync(page, page.Locator("form", new() { Has = page.Locator("[name=SharePath]") }));
            var connection = page.Url;
            foreach (var (folder, type) in roots)
            {
                await page.GotoAsync(connection);
                await page.GetByRole(AriaRole.Link, new() { Name = "Browse folders" }).ClickAsync();
                await page.GetByRole(AriaRole.Link, new() { Name = folder, Exact = true }).ClickAsync();
                var root = FormWith(page, "Add library path");
                await root.Locator("[name=Name]").FillAsync(folder);
                await root.Locator("[name=MediaTypeId]").SelectOptionAsync(new SelectOptionValue { Label = type });
                await SubmitAsync(page, root);
                StringAssert.Contains(await page.Locator("main table").Last.InnerTextAsync(), "/" + folder);
            }
        }

        // The fake indexer and SABnzbd; finished jobs appear under the downloads folder.
        public static async Task AddUsenetAsync(IPage page, FakeServices fakes, string categories)
        {
            await page.GotoAsync("/Admin/Acquisition");
            await page.GetByRole(AriaRole.Link, new() { Name = "Add indexer" }).ClickAsync();
            await page.FillAsync("form [name=Name]", "Indexer");
            await page.FillAsync("form [name=BaseUrl]", new Uri(fakes.Address, "newznab/").ToString());
            await page.FillAsync("form [name=ApiKey]", FakeServices.IndexerKey);
            await page.FillAsync("form [name=Categories]", categories);
            await SubmitAsync(page, page.Locator("form", new() { Has = page.Locator("[name=BaseUrl]") }));
            await page.GotoAsync("/Admin/Acquisition");
            await page.GetByRole(AriaRole.Link, new() { Name = "Add download client" }).ClickAsync();
            await page.FillAsync("form [name=Name]", "SABnzbd");
            await page.FillAsync("form [name=BaseUrl]", new Uri(fakes.Address, "sabnzbd/").ToString());
            await page.FillAsync("form [name=ApiKey]", FakeServices.ClientKey);
            await page.FillAsync("form [name=RemotePath]", "/downloads");
            await page.FillAsync("form [name=LocalPath]", fakes.Downloads);
            await SubmitAsync(page, page.Locator("form", new() { Has = page.Locator("[name=BaseUrl]") }));
        }

        // Found in Discover and requested; the Owner's request is approved at once.
        public static async Task RequestAsync(IPage page, string query, string title)
        {
            await page.GotoAsync("/?q=" + Uri.EscapeDataString(query));
            var card = page.Locator(".media-card", new() { HasText = title });
            await card.HoverAsync();
            await card.GetByRole(AriaRole.Link, new() { Name = "Request this work" }).ClickAsync();
            await SubmitAsync(page, FormWith(page, "Request this work"));
        }

        public static async Task OpenWantedAsync(IPage page, string title)
        {
            await page.GotoAsync("/Admin/Wanted");
            await page.Locator("tr", new() { HasText = title }).GetByRole(AriaRole.Link, new() { Name = "Open" }).ClickAsync();
        }

        public static async Task GrabAsync(IPage page, FakeRelease release)
        {
            await SubmitAsync(page, page.Locator("tr", new() { HasText = release.Title }).Locator("form")
                .Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Select and download" }) }));
        }

        // The details page of a work once something of it can be played.
        public static async Task<string> WaitForAvailableAsync(IPage page, string title)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromMinutes(2))
            {
                await page.GotoAsync("/Library");
                var card = page.GetByRole(AriaRole.Link, new() { Name = title });
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

            Assert.Fail(title + " did not become available within two minutes.");
            return "";
        }

        // Plays from four seconds on and pauses after five; pausing saves the position.
        public static async Task PlayAndPauseAsync(IPage page, ILocator action)
        {
            await action.ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('video')?.readyState >= 1", null, new() { Timeout = MediaTimeout });
            await page.Locator("video").EvaluateAsync("video => { video.currentTime = 4; return video.play(); }");
            await page.WaitForFunctionAsync("() => document.querySelector('video').currentTime > 5", null, new() { Timeout = MediaTimeout });
            await page.RunAndWaitForResponseAsync(() => page.Locator("video").EvaluateAsync("video => video.pause()"),
                response => response.Request.Method == "PUT" && response.Ok, new() { Timeout = MediaTimeout });
        }

        public static async Task SignOutAndInAsync(IPage page)
        {
            await page.GotoAsync("/Account/Logout");
            await SubmitAsync(page, FormWith(page, "Sign out"));
            await page.GotoAsync("/Account/Login");
            await page.FillAsync("[name=Email]", Owner.Email);
            await page.FillAsync("[name=Password]", Owner.Password);
            await page.Locator("form button[type=submit]").First.ClickAsync();
            await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));
        }

        // Continues and reports where playback starts.
        public static async Task<double> ResumeAsync(IPage page, ILocator action)
        {
            await action.ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('video')?.readyState >= 1", null, new() { Timeout = MediaTimeout });
            return await page.Locator("video").EvaluateAsync<double>("video => video.currentTime");
        }

        // A short test pattern with sound, as WebM the browser plays directly; without FFmpeg only bytes for the import.
        public static async Task<string> WriteVideoAsync(string? ffmpeg, string file)
        {
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

        public static string? FfprobeBeside(string? ffmpeg) =>
            ffmpeg is null ? null : Path.Combine(Path.GetDirectoryName(ffmpeg)!, Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.Ordinal));
    }

    // Fixture, download and library folders; disposed after the instance, which may still hold files.
    internal sealed class Folders : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("jularr-e2e-").FullName;

        public Folders(params string[] libraryFolders)
        {
            Directory.CreateDirectory(Fixtures);
            Directory.CreateDirectory(Downloads);
            foreach (var folder in libraryFolders)
            {
                Directory.CreateDirectory(Path.Combine(Library, folder));
            }
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
