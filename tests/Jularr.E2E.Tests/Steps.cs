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
                // A cover that its provider does not deliver (these tests have no real catalogs) is no fault of the page.
                if (message.Type == "error" && !(message.Text.StartsWith("Failed to load resource", StringComparison.Ordinal) && message.Location.Contains("/api/v1/covers/", StringComparison.Ordinal)))
                {
                    problems.Enqueue(page.Url + ": " + message.Text);
                }
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

        // Clicks a link or button and waits for the page it leads to, swapped in place or loaded as a new document.
        public static async Task FollowAsync(IPage page, ILocator element)
        {
            // A search asks every indexer rung in turn and may take its time.
            // The page counts as arrived when its content (or the region an action swapped) was replaced and stopped changing for a moment: scripts of the new page still rearrange it right after the swap.
            await page.EvaluateAsync("""
                () => {
                    window.jularrFollowing = true;
                    window.jularrChangedAt = window.jularrStartedAt = performance.now();
                    window.jularrWatcher ??= new MutationObserver(() => { window.jularrChangedAt = performance.now(); });
                    window.jularrWatcher.observe(document.documentElement, { subtree: true, childList: true, attributes: true, characterData: true });
                    document.getElementById('app-content')?.setAttribute('data-followed', '');
                    document.querySelectorAll('[data-region]').forEach(region => region.setAttribute('data-followed', ''));
                    window.jularrRegions = document.querySelectorAll('[data-region]').length;
                }
                """);
            await element.ClickAsync();
            await page.WaitForFunctionAsync("""
                () => (!window.jularrFollowing || !!document.getElementById('app-content') && (!document.querySelector('#app-content[data-followed]')
                        || document.querySelectorAll('[data-region][data-followed]').length < window.jularrRegions))
                    && document.readyState !== 'loading' && !document.querySelector('.htmx-request, .htmx-swapping')
                    && (!window.jularrWatcher || performance.now() - window.jularrChangedAt > 150 || performance.now() - window.jularrStartedAt > 5000)
                """, null, new() { Timeout = 120_000 });
        }

        // Submits, waits for the answering page and fails on the error it shows.
        public static async Task SubmitAsync(IPage page, ILocator form)
        {
            // A form saved through the page's pill has no visible button of its own.
            if (await form.GetAttributeAsync("data-save") is not null && await page.Locator("[data-save-pill]").CountAsync() > 0)
            {
                await SaveAsync(page);
                return;
            }

            await FollowAsync(page, form.Locator("button[type=submit]").Last);
            var alert = page.Locator("main [role=alert]");
            if (await alert.CountAsync() > 0)
            {
                Assert.Fail("The form answered: " + await alert.First.InnerTextAsync());
            }
        }

        public static async Task SetTmdbKeyAsync(IPage page)
        {
            await page.GotoAsync("/Admin/Providers");
            await page.Locator("#provider-tmdb").GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
            var tmdb = page.Locator("#provider-settings-tmdb form[action*='handler=Secret']");
            await tmdb.Locator("[name=Secret]").FillAsync(FakeServices.TmdbToken);
            await SubmitAsync(page, tmdb);
        }

        // Switches a module on the Instance page; its form is labelled with the module.s description.
        public static async Task SetModuleAsync(IPage page, string description, bool enabled)
        {
            await page.GotoAsync("/Admin/Instance");
            var form = page.Locator("main form[data-save]").Filter(new() { Has = page.Locator("legend", new() { HasText = description }) });
            await form.Locator("[name=IsEnabled][value=" + (enabled ? "True" : "False") + "]").CheckAsync();
            await SaveAsync(page);
        }

        // Saves every changed form of the page through its save pill and fails on an error the page shows.
        public static async Task SaveAsync(IPage page)
        {
            await FollowAsync(page, page.Locator("[data-save-pill] [data-save-submit]"));
            var alert = page.Locator("main [role=alert]");
            if (await alert.CountAsync() > 0)
            {
                Assert.Fail("The page answered: " + await alert.First.InnerTextAsync());
            }
        }

        // The local folder as a writable connection with one library path per media type folder.
        public static async Task AddLibraryAsync(IPage page, string localPath, params LibraryPath[] roots)
        {
            await page.GotoAsync("/Admin/Storage");
            await FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Add local path" }));
            await page.FillAsync("form [name=Name]", "Library");
            await page.SelectOptionAsync("form [name=SharePath]", localPath);
            await page.Locator("form [name=IsReadOnly][value=False]").CheckAsync();
            await SubmitAsync(page, page.Locator("form", new() { Has = page.Locator("[name=SharePath]") }));
            var connection = page.Url;
            foreach (var (folder, type, subtype) in roots)
            {
                await page.GotoAsync(connection);
                await page.GetByRole(AriaRole.Link, new() { Name = "Browse folders" }).ClickAsync();
                var picker = page.GetByRole(AriaRole.Dialog, new() { Name = "Browse folders", Exact = true });
                await picker.GetByRole(AriaRole.Link, new() { Name = folder, Exact = true }).ClickAsync();
                await picker.GetByRole(AriaRole.Button, new() { Name = "Use this folder", Exact = true }).ClickAsync();
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

        // Manual-flow tests use a manual-only indexer; automatic-flow tests explicitly opt in to automatic searching.
        // The fake indexer and SABnzbd; finished jobs appear under the downloads folder.
        public static async Task AddUsenetAsync(IPage page, FakeServices fakes, string categories, bool searchesAutomatically = false)
        {
            await page.GotoAsync("/Admin/Acquisition");
            await FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Add indexer" }));
            await page.FillAsync("form [name=Name]", "Indexer");
            await page.FillAsync("form [name=BaseUrl]", new Uri(fakes.Address, "newznab/").ToString());
            await page.FillAsync("form [name=ApiKey]", FakeServices.IndexerKey);
            await page.FillAsync("form [name=Categories]", categories);
            // Two short choices are a segmented control of radios.
            await page.Locator("form [name=SearchesAutomatically][value=" + (searchesAutomatically ? "True" : "False") + "]").CheckAsync(new() { Force = true });
            await SubmitAsync(page, page.Locator("form", new() { Has = page.Locator("[name=BaseUrl]") }));
            await page.GotoAsync("/Admin/Acquisition/Downloads");
            await FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Add download client" }));
            await page.FillAsync("form [name=Name]", "SABnzbd");
            await page.FillAsync("form [name=BaseUrl]", new Uri(fakes.Address, "sabnzbd/").ToString());
            await page.FillAsync("form [name=ApiKey]", FakeServices.ClientKey);
            await page.FillAsync("form [name=LocalPath]", fakes.Downloads);
            await SubmitAsync(page, page.Locator("form", new() { Has = page.Locator("[name=BaseUrl]") }));
        }

        // Found in Discover and requested; the Owner's request is approved at once.
        public static async Task RequestAsync(IPage page, string query, string title)
        {
            await page.GotoAsync("/?q=" + Uri.EscapeDataString(query));
            var card = page.Locator(".media-card", new() { HasText = title });
            await card.HoverAsync();
            await FollowAsync(page, card.GetByRole(AriaRole.Link, new() { Name = "Request", Exact = true }));
            await SubmitAsync(page, FormWith(page, "Request this work"));
        }

        // Found in its catalog on Discover and requested on its own page.
        public static async Task RequestFromCatalogAsync(IPage page, string type, string title, string source)
        {
            await page.GotoAsync("/?type=" + type + "&q=" + Uri.EscapeDataString(title));
            var card = page.Locator(".media-card", new() { HasText = title }).Filter(new() { Has = page.Locator("a[href*='/" + type + "/" + source + "/']") }).First;
            await card.HoverAsync();
            await FollowAsync(page, card.GetByRole(AriaRole.Link, new() { Name = "Request", Exact = true }));
            await SubmitAsync(page, FormWith(page, "Request this work"));
        }

        public static async Task OpenWantedAsync(IPage page, string title)
        {
            await page.GotoAsync("/Admin/Wanted");
            await FollowAsync(page, page.Locator("tr", new() { HasText = title }).GetByRole(AriaRole.Link, new() { Name = "Edit target" }));
        }

        // The manual search page of a target.
        public static async Task OpenSearchAsync(IPage page, string title)
        {
            await OpenWantedAsync(page, title);

            // Films open on their management page, the other types on their wanted page.
            var interactive = page.GetByRole(AriaRole.Link, new() { Name = "Interactive search", Exact = true });
            await FollowAsync(page, interactive.Or(page.GetByRole(AriaRole.Link, new() { Name = "Search manually", Exact = true })));
        }

        // The releases of one episode, searched from its row on the series page.
        public static async Task SearchEpisodeAsync(IPage page, string title, string episodeNumber)
        {
            await OpenWantedAsync(page, title);
            var row = page.Locator("tr.work-row").Filter(new() { Has = page.Locator("td[data-col=number]").GetByText(episodeNumber, new() { Exact = true }) });
            await SubmitAsync(page, row.Locator("form").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Interactive search for this episode" }) }));
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
                    await FollowAsync(page, card.First);
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
            await page.GetByRole(AriaRole.Button, new() { Name = "Add title", Exact = true }).ClickAsync();
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
            await page.Locator(".reader__canvas").HoverAsync();
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
        // A text PDF: one Helvetica line per entry, top to bottom, each page in its own content stream.
        public static string WritePdf(string file, params string[][] pages) => WritePdf(file, null, null, pages);

        // The pages with a coloured picture below the text of one of them.
        public static string WritePdfWithPicture(string file, int picturePage, params string[][] pages) => WritePdf(file, picturePage, null, pages);

        // The pages with a coloured picture that fills one of them, as a plate in a printed book does.
        public static string WritePdfWithPlate(string file, int platePage, params string[][] pages) => WritePdf(file, null, platePage, pages);

        // A scan: every page is one coloured picture over the whole page, a different colour each, and there is no text at all.
        public static string WriteScannedPdf(string file, int pages)
        {
            var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "" };
            var kids = new List<string>();
            for (var number = 0; number < pages; number++)
            {
                var samples = new string([.. Enumerable.Range(0, 64 * 48).SelectMany(pixel => new[] { (char)(40 + number * 70), (char)(pixel / 64 * 5), (char)(200 - number * 50) })]);
                objects.Add("<< /Type /XObject /Subtype /Image /Width 64 /Height 48 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length " + samples.Length + " >>\nstream\n" + samples + "\nendstream");
                var image = objects.Count;
                var stream = "q 595 0 0 842 0 0 cm /Im1 Do Q\n";
                objects.Add("<< /Length " + stream.Length + " >>\nstream\n" + stream + "endstream");
                objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /XObject << /Im1 " + image + " 0 R >> >> /Contents " + (objects.Count) + " 0 R >>");
                kids.Add(objects.Count + " 0 R");
            }

            objects[1] = "<< /Type /Pages /Kids [" + string.Join(' ', kids) + "] /Count " + kids.Count + " >>";
            var body = new System.Text.StringBuilder("%PDF-1.4\n");
            var offsets = new List<int>();
            for (var index = 0; index < objects.Count; index++)
            {
                offsets.Add(body.Length);
                body.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
            }

            var xref = body.Length;
            body.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
            foreach (var offset in offsets)
            {
                body.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            }

            body.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
            File.WriteAllText(file, body.ToString(), System.Text.Encoding.Latin1);
            return file;
        }

        private static string WritePdf(string file, int? picturePage, int? platePage, string[][] pages)
        {
            static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
            var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>" };
            var kids = new List<string>();
            for (var number = 0; number < pages.Length; number++)
            {
                var lines = pages[number];
                var stream = string.Concat(lines.Select((line, index) => "BT /F1 11 Tf 54 " + (780 - index * 15) + " Td (" + Escape(line) + ") Tj ET\n"));
                var pictures = "";
                if (number == picturePage || number == platePage)
                {
                    // 64 by 48 RGB samples, uncompressed, drawn below the text.
                    var samples = new string([.. Enumerable.Range(0, 64 * 48).SelectMany(pixel => new[] { (char)(pixel % 64 * 4), (char)(pixel / 64 * 5), (char)160 })]);
                    objects.Add("<< /Type /XObject /Subtype /Image /Width 64 /Height 48 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length " + samples.Length + " >>\nstream\n" + samples
                        + "\nendstream");
                    pictures = " /XObject << /Im1 " + objects.Count + " 0 R >>";
                    stream += number == platePage ? "q 515 0 0 640 40 100 cm /Im1 Do Q\n" : "q 192 0 0 144 54 " + (780 - lines.Length * 15 - 160) + " cm /Im1 Do Q\n";
                }

                objects.Add("<< /Length " + stream.Length + " >>\nstream\n" + stream + "endstream");
                objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >>" + pictures + " >> /Contents " + objects.Count + " 0 R >>");
                kids.Add(objects.Count + " 0 R");
            }

            objects[1] = "<< /Type /Pages /Kids [" + string.Join(' ', kids) + "] /Count " + kids.Count + " >>";
            var body = new System.Text.StringBuilder("%PDF-1.4\n");
            var offsets = new List<int>();
            for (var index = 0; index < objects.Count; index++)
            {
                offsets.Add(body.Length);
                body.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
            }

            var xref = body.Length;
            body.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
            foreach (var offset in offsets)
            {
                body.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            }

            body.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
            File.WriteAllText(file, body.ToString(), System.Text.Encoding.Latin1);
            return file;
        }

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
        public static string WriteComic(string file, int pages, byte[][]? pictures = null)
        {
            using var archive = ZipFile.Open(file, ZipArchiveMode.Create);
            for (var number = 1; number <= pages; number++)
            {
                using var stream = archive.CreateEntry(number.ToString("000", System.Globalization.CultureInfo.InvariantCulture) + ".png").Open();
                stream.Write(pictures is { Length: > 0 } ? pictures[(number - 1) % pictures.Length] : Pixel);
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
            await page.FillAsync("[name=Username]", Owner.Username);
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
