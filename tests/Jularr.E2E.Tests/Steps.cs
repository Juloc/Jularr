using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // What an administrator and a viewer do in the browser, shared by the path tests.
    internal static class Steps
    {
        public static readonly float MediaTimeout = 30_000;

        // A 1x1 PNG: blank provider pictures and comic pages.
        private static readonly byte[] Pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

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

        // Pictures from provider hosts get a blank image; anything else leaving this machine fails visibly.
        public static async Task KeepLocalAsync(IBrowserContext context)
        {
            await context.RouteAsync(url => new Uri(url) is { IsLoopback: false, Scheme: "http" or "https" }, route =>
                route.Request.ResourceType == "image" ? route.FulfillAsync(new() { ContentType = "image/png", BodyBytes = Pixel }) : route.AbortAsync());
        }

        public static ILocator FormWith(IPage page, string button) =>
            page.Locator("main form").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = button, Exact = true }) });

        // Submits, waits for the answering page and fails on the error it shows.
        public static async Task SubmitAsync(IPage page, ILocator form)
        {
            // A new document has replaced the marked one; a search asks every indexer rung in turn and may take its time.
            await page.EvaluateAsync("() => window.jularrSubmitting = true");
            await form.Locator("button[type=submit]").Last.ClickAsync();
            await page.WaitForFunctionAsync("() => !window.jularrSubmitting && document.readyState !== 'loading'", null, new() { Timeout = 120_000 });
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
        public static async Task AddLibraryAsync(IPage page, string localPath, params LibraryPath[] roots)
        {
            await page.GotoAsync("/Admin/Storage");
            await page.GetByRole(AriaRole.Link, new() { Name = "Add local path" }).ClickAsync();
            await page.FillAsync("form [name=Name]", "Library");
            await page.SelectOptionAsync("form [name=SharePath]", localPath);
            await page.SelectOptionAsync("form [name=IsReadOnly]", "False");
            await SubmitAsync(page, page.Locator("form", new() { Has = page.Locator("[name=SharePath]") }));
            var connection = page.Url;
            foreach (var (folder, type, subtype) in roots)
            {
                await page.GotoAsync(connection);
                await page.GetByRole(AriaRole.Link, new() { Name = "Browse folders" }).ClickAsync();
                await page.GetByRole(AriaRole.Link, new() { Name = folder, Exact = true }).ClickAsync();
                var root = FormWith(page, "Add library path");
                await root.Locator("[name=Name]").FillAsync(folder);
                await root.Locator("[name=MediaTypeId]").SelectOptionAsync(new SelectOptionValue { Label = type });
                if (subtype is not null)
                {
                    await root.Locator("[name=MediaSubtypeId]").SelectOptionAsync(new SelectOptionValue { Label = subtype });
                }

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

        // The details page of a work once something of it can be played or read.
        public static async Task<string> WaitForAvailableAsync(IPage page, string title, string action = "Play")
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromMinutes(2))
            {
                await page.GotoAsync("/Library");
                var card = page.GetByRole(AriaRole.Link, new() { Name = title });
                if (await card.CountAsync() > 0)
                {
                    await card.First.ClickAsync();
                    if (await page.GetByRole(AriaRole.Link, new() { Name = action, Exact = true }).CountAsync() > 0)
                    {
                        return new Uri(page.Url).PathAndQuery;
                    }
                }

                // The download monitor runs every fifteen seconds.
                await page.WaitForTimeoutAsync(3000);
            }

            throw new AssertFailedException(title + " did not become available within two minutes.");
        }

        // An album of the given tracks as FLAC, a tone each; without FFmpeg only bytes for the import.
        public static async Task<string> WriteAlbumAsync(string? ffmpeg, string folder, params string[] tracks)
        {
            Directory.CreateDirectory(folder);
            for (var number = 1; number <= tracks.Length; number++)
            {
                var file = Path.Combine(folder, number.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + " - " + tracks[number - 1] + ".flac");
                if (ffmpeg is null)
                {
                    await File.WriteAllBytesAsync(file, new byte[16 * 1024]);
                    continue;
                }

                using var process = Process.Start(new ProcessStartInfo(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=" + (220 * number) + ":duration=10", "-y", file])
                { RedirectStandardError = true })!;
                var errors = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.AreEqual(0, process.ExitCode, errors);
            }

            return folder;
        }

        // A Work from the type's metadata provider through the Works page, then requested on its details.
        public static async Task<string> ImportAndRequestAsync(IPage page, string type, string? subtype, string title, string? provider = null)
        {
            await page.GotoAsync("/Admin/Works");
            var search = page.Locator("main form", new() { Has = page.Locator("[name=Query]") });
            await search.Locator("[name=MediaTypeId]").SelectOptionAsync(new SelectOptionValue { Label = type });
            if (subtype is not null)
            {
                await search.Locator("[name=MediaSubtypeId]").SelectOptionAsync(new SelectOptionValue { Label = subtype });
            }

            await search.Locator("[name=Query]").FillAsync(title);
            await SubmitAsync(page, search);
            var result = page.Locator("tr", new() { HasText = title });
            if (provider is not null)
            {
                result = result.Filter(new() { Has = page.Locator("input[name=Namespace][value='" + provider + "']") });
            }

            await SubmitAsync(page, result.First.Locator("form"));
            var details = "/Details/" + new Uri(page.Url).Segments.Last();
            await page.GotoAsync(details);
            await SubmitAsync(page, FormWith(page, "Request this work"));
            return details;
        }

        public static async Task OpenReaderAsync(IPage page, string details)
        {
            await page.GotoAsync(details);
            await page.GetByRole(AriaRole.Link, new() { Name = "Read", Exact = true }).First.ClickAsync();
        }

        // Opens the next section and reads a little in it; the reader saves the position shortly after scrolling.
        public static async Task ReadNextSectionAsync(IPage page)
        {
            await page.Locator("[data-role=next]").First.ClickAsync();
            await page.WaitForURLAsync(url => url.Contains("s=1", StringComparison.Ordinal));
            await page.Locator("[data-role=content]").HoverAsync();
            await page.RunAndWaitForResponseAsync(() => page.Mouse.WheelAsync(0, 800),
                response => response.Request.Method == "PUT" && response.Ok, new() { Timeout = MediaTimeout });
        }

        // Turns to the next page; the comic reader saves it at once.
        public static async Task TurnAsync(IPage page)
        {
            await page.RunAndWaitForResponseAsync(() => page.Locator("[data-role=next]").First.ClickAsync(),
                response => response.Request.Method == "PUT" && response.Ok, new() { Timeout = MediaTimeout });
        }

        // A two-chapter EPUB 3.
        public static string WriteEpub(string file, string title, string author)
        {
            using var archive = ZipFile.Open(file, ZipArchiveMode.Create);
            var mimetype = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(mimetype.Open()))
            {
                writer.Write("application/epub+zip");
            }

            Entry(archive, "META-INF/container.xml", """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");
            Entry(archive, "OEBPS/content.opf", $"""<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">e2e-book</dc:identifier><dc:title>{SecurityElement.Escape(title)}</dc:title><dc:creator>{SecurityElement.Escape(author)}</dc:creator><dc:language>en</dc:language><meta property="dcterms:modified">2026-01-01T00:00:00Z</meta></metadata><manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/><item id="one" href="one.xhtml" media-type="application/xhtml+xml"/><item id="two" href="two.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="one"/><itemref idref="two"/></spine></package>""");
            Entry(archive, "OEBPS/nav.xhtml", """<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="one.xhtml">Chapter One</a></li><li><a href="two.xhtml">Chapter Two</a></li></ol></nav></body></html>""");
            // Chapters long enough to scroll through.
            var text = string.Concat(Enumerable.Range(1, 60).Select(number => "<p>Paragraph " + number + " of a long chapter, written to be read on.</p>"));
            Entry(archive, "OEBPS/one.xhtml", """<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>Chapter One</title></head><body><h1>Chapter One</h1>""" + text + "</body></html>");
            Entry(archive, "OEBPS/two.xhtml", """<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>Chapter Two</title></head><body><h1>Chapter Two</h1>""" + text + "</body></html>");
            return file;
        }

        // A comic archive of numbered pages.
        public static string WriteComic(string file, int pages)
        {
            using var archive = ZipFile.Open(file, ZipArchiveMode.Create);
            for (var number = 1; number <= pages; number++)
            {
                using var stream = archive.CreateEntry(number.ToString("000", System.Globalization.CultureInfo.InvariantCulture) + ".png").Open();
                stream.Write(Pixel);
            }

            return file;
        }

        private static void Entry(ZipArchive archive, string name, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
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

    // A library path: its folder below the connection, media type and optional subtype as the form names them.
    internal sealed record LibraryPath(string Folder, string Type, string? Subtype = null);

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
