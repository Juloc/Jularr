using System.Net;
using System.Text;
using Jularr.Integrations.Logic.Browser;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    // The browser reads what a plain request cannot; it needs a browser of its own.
    [TestClass]
    public sealed class SourceBrowserTests
    {
        [TestMethod]
        public async Task ManagedBrowser_RemovesAbandonedStagingWithoutTouchingOtherFolders()
        {
            using var folder = new TemporaryDirectory();
            var managed = Path.Combine(folder.Path, "managed", "staging");
            var unrelated = Path.Combine(folder.Path, "library");
            Directory.CreateDirectory(managed);
            Directory.CreateDirectory(unrelated);
            await File.WriteAllTextAsync(Path.Combine(managed, "stale.part"), "incomplete");
            await File.WriteAllTextAsync(Path.Combine(unrelated, "keep.epub"), "keep");
            var packages = new BrowserPackages(new BrowserOptions { ProfilePath = folder.Path });

            Assert.IsTrue(packages.HasManagedData);
            await packages.RemoveAsync(CancellationToken.None);
            Assert.IsFalse(packages.HasManagedData);
            Assert.IsTrue(File.Exists(Path.Combine(unrelated, "keep.epub")));
        }

        [TestMethod]
        public async Task Browser_ReadsAPageBehindACheckAndFetchesItsFile()
        {
            var browser = Environment.GetEnvironmentVariable("JULARR_BROWSER");
            if (string.IsNullOrWhiteSpace(browser))
            {
                Assert.Inconclusive("JULARR_BROWSER names no browser.");
            }

            using var folder = new TemporaryDirectory();
            using var server = new CheckPage();
            using var source = new SourceBrowser(new BrowserOptions { Path = browser, ProfilePath = folder.Path, PageTimeoutSeconds = 60, AllowPrivateAddressesForTests = true });
            Assert.IsTrue(source.IsAvailable);

            // The page answers a browser that passed its own check.
            var page = await source.ReadPageAsync(server.Address, CancellationToken.None);
            Assert.IsNotNull(page);
            StringAssert.Contains(page, "The page behind the check");

            // The file of that page is fetched the same way.
            var destination = Path.Combine(folder.Path, "book.epub");
            await source.SaveFileAsync(new Uri(server.Address, "/book.epub"), destination, null, null, CancellationToken.None);
            Assert.AreEqual("the book", await File.ReadAllTextAsync(destination));
        }

        [TestMethod]
        public async Task Browser_UsesARunningBrowser()
        {
            var browser = Environment.GetEnvironmentVariable("JULARR_BROWSER");
            if (string.IsNullOrWhiteSpace(browser))
            {
                Assert.Inconclusive("JULARR_BROWSER names no browser.");
            }

            using var folder = new TemporaryDirectory();
            using var server = new CheckPage();
            var port = FreePort();
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(browser)
            {
                UseShellExecute = false,
                Arguments = $"--remote-debugging-port={port} --user-data-dir=\"{Path.Combine(folder.Path, "profile")}\" --no-first-run --no-default-browser-check about:blank"
            });

            try
            {
                // The browser that already runs is used as it is; its profile is its own.
                using var source = new SourceBrowser(new BrowserOptions { RemoteAddress = $"http://127.0.0.1:{port}", ProfilePath = folder.Path, PageTimeoutSeconds = 60, AllowPrivateAddressesForTests = true });
                Assert.IsTrue(source.IsAvailable);
                var page = await source.ReadPageAsync(server.Address, CancellationToken.None);
                Assert.IsNotNull(page);
                StringAssert.Contains(page, "The page behind the check");
            }
            finally
            {
                process.Kill(true);
                process.WaitForExit(5000);
            }
        }

        [TestMethod]
        public async Task Browser_UsesTheBrowserThatStillRunsWithItsProfile()
        {
            var browser = Environment.GetEnvironmentVariable("JULARR_BROWSER");
            if (string.IsNullOrWhiteSpace(browser))
            {
                Assert.Inconclusive("JULARR_BROWSER names no browser.");
            }

            using var folder = new TemporaryDirectory();
            using var server = new CheckPage();
            using var first = new SourceBrowser(new BrowserOptions { Path = browser, ProfilePath = folder.Path, PageTimeoutSeconds = 60, AllowPrivateAddressesForTests = true });
            var page = await first.ReadPageAsync(server.Address, CancellationToken.None);
            Assert.IsNotNull(page);

            // A browser that still runs with this profile is used as it is; a second one is not started.
            using var second = new SourceBrowser(new BrowserOptions { Path = browser, ProfilePath = folder.Path, PageTimeoutSeconds = 60, AllowPrivateAddressesForTests = true });
            var again = await second.ReadPageAsync(server.Address, CancellationToken.None);
            Assert.IsNotNull(again);
            StringAssert.Contains(again, "The page behind the check");
        }

        [TestMethod]
        public async Task Browser_ResetEndsTheBrowserThatHoldsTheProfile()
        {
            var browser = Environment.GetEnvironmentVariable("JULARR_BROWSER");
            if (string.IsNullOrWhiteSpace(browser))
            {
                Assert.Inconclusive("JULARR_BROWSER names no browser.");
            }

            using var folder = new TemporaryDirectory();
            using var server = new CheckPage();
            using var first = new SourceBrowser(new BrowserOptions { Path = browser, ProfilePath = folder.Path, PageTimeoutSeconds = 60, AllowPrivateAddressesForTests = true });
            var page = await first.ReadPageAsync(server.Address, CancellationToken.None);
            Assert.IsNotNull(page);

            // A browser that runs with the profile is used as it is; the reset ends it, so a new one can start.
            using var second = new SourceBrowser(new BrowserOptions { Path = browser, ProfilePath = folder.Path, PageTimeoutSeconds = 60, AllowPrivateAddressesForTests = true });
            await second.ResetAsync();
            Assert.IsFalse(Directory.Exists(Path.Combine(folder.Path, "profile")));

            using var third = new SourceBrowser(new BrowserOptions { Path = browser, ProfilePath = folder.Path, PageTimeoutSeconds = 60, AllowPrivateAddressesForTests = true });
            var again = await third.ReadPageAsync(server.Address, CancellationToken.None);
            Assert.IsNotNull(again);
            StringAssert.Contains(again, "The page behind the check");
        }

        [TestMethod]
        public async Task Browser_ReadsSeveralAddressesAtTheSameTime()
        {
            var browser = Environment.GetEnvironmentVariable("JULARR_BROWSER");
            if (string.IsNullOrWhiteSpace(browser))
            {
                Assert.Inconclusive("JULARR_BROWSER names no browser.");
            }

            using var folder = new TemporaryDirectory();
            using var server = new CheckPage();
            using var source = new SourceBrowser(new BrowserOptions { Path = browser, ProfilePath = folder.Path, PageTimeoutSeconds = 60, AllowPrivateAddressesForTests = true });

            // The first address that answers wins; the mirrors are read at the same time.
            var page = await source.ReadPagesAsync([server.Address, new Uri(server.Address, "/other")], CancellationToken.None);
            Assert.IsNotNull(page);
            StringAssert.Contains(page.Html, "The page behind the check");
        }

        [TestMethod]
        public async Task Browser_PrivateNavigationIsRejectedBeforeConnecting()
        {
            using var folder = new TemporaryDirectory();
            using var source = new SourceBrowser(new BrowserOptions
            {
                Path = "",
                RemoteAddress = "http://127.0.0.1:1",
                ProfilePath = folder.Path
            });

            Assert.IsNull(await source.ReadPageAsync(new Uri("https://127.0.0.1/private"), CancellationToken.None));
            Assert.IsNull(await source.ReadPagesAsync([new Uri("https://10.0.0.2/private")], CancellationToken.None));
            await Assert.ThrowsExactlyAsync<Integrations.Data.Browser.SourceBrowserException>(() =>
                source.SaveFileAsync(new Uri("https://192.168.1.2/file.epub"), Path.Combine(folder.Path, "book.epub"), null, null, CancellationToken.None));
        }

        [TestMethod]
        public async Task Browser_WithoutABinaryAnswersNothing()
        {
            using var folder = new TemporaryDirectory();
            using var source = new SourceBrowser(new BrowserOptions { Path = "", ProfilePath = folder.Path });
            Assert.IsFalse(source.IsAvailable);
            Assert.IsNull(await source.ReadPageAsync(new Uri("https://annas.example.test/"), CancellationToken.None));
            await Assert.ThrowsExactlyAsync<Integrations.Data.Browser.SourceBrowserException>(() =>
                source.SaveFileAsync(new Uri("https://annas.example.test/book.epub"), Path.Combine(folder.Path, "book.epub"), null, null, CancellationToken.None));
        }

        private static int FreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        // A page that asks a browser to come back, like the sources behind a browser check do.
        private sealed class CheckPage : IDisposable
        {
            private readonly HttpListener _listener = new();
            private readonly Task _serving;

            public CheckPage()
            {
                var port = FreePort();
                Address = new Uri($"http://127.0.0.1:{port}/");
                _listener.Prefixes.Add(Address.ToString());
                _listener.Start();
                _serving = Task.Run(ServeAsync);
            }

            public Uri Address { get; }

            public void Dispose()
            {
                _listener.Stop();
                _listener.Close();
                _serving.Wait(TimeSpan.FromSeconds(5));
            }

            private async Task ServeAsync()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
                    {
                        return;
                    }

                    if (context.Request.Url!.AbsolutePath == "/book.epub")
                    {
                        var bytes = Encoding.UTF8.GetBytes("the book");
                        context.Response.AddHeader("Content-Disposition", "attachment; filename=\"book.epub\"");
                        context.Response.ContentType = "application/epub+zip";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }
                    else if (context.Request.Cookies["passed"] is not null)
                    {
                        var page = Encoding.UTF8.GetBytes("<html><body><p>The page behind the check</p></body></html>");
                        context.Response.ContentType = "text/html";
                        context.Response.ContentLength64 = page.Length;
                        await context.Response.OutputStream.WriteAsync(page);
                    }
                    else
                    {
                        var check = Encoding.UTF8.GetBytes("<html><head><title>DDoS-Guard</title></head><body>Checking your browser<script>document.cookie='passed';location.reload();</script></body></html>");
                        context.Response.StatusCode = 403;
                        context.Response.ContentType = "text/html";
                        context.Response.ContentLength64 = check.Length;
                        await context.Response.OutputStream.WriteAsync(check);
                    }

                    context.Response.Close();
                }
            }

        }
    }
}