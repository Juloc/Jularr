using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class ReaderTranslationTests
    {
        [TestMethod]
        [Timeout(180_000)]
        public async Task InstalledTranslateGemma_TranslatesTwoSections_CachesAndRestoresOriginal()
        {
            using var folders = new Folders("Books");
            Steps.WriteEpub(Path.Combine(folders.Library, "Books", "translation-reader.epub"), "Translation Reader", "Local fixture");
            await using var ollama = FakeOllama.Start();
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Storage:LocalPaths:0"] = folders.Library,
                ["LocalTranslation:Endpoint"] = ollama.Address.ToString(),
                ["HTTPS_PROXY"] = "http://127.0.0.1:9" // Simulate Mozilla unavailable, never depend on external services.
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            await ReadingPathTests.AddBookAsync(page, folders, "Translation Reader");
            await jularr.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");

            // Admin: model availability, benchmark and live status on the same
            // authenticated V1 endpoint used by the management page.
            var admin = await page.GotoAsync("/Admin/Instance/Translation");
            Assert.AreEqual(200, admin!.Status);
            await Assertions.Expect(page.Locator("form[data-translation-submit=benchmark]")).ToHaveCountAsync(1);
            await page.Locator("form[data-translation-submit=benchmark] button[type=submit]").ClickAsync();
            await Assertions.Expect(page.Locator("body")).ToContainTextAsync("id → de: Kapitel Eins", new() { Timeout = 60_000 });

            await page.RouteAsync("**/api/v1/admin/translation/models", route => route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "application/json",
                Body = """{"downloadState":"downloading","downloadModel":"translategemma:4b","downloadProgress":50}"""
            }));
            // Exercise the entrypoint on a normal Admin GET, rather than the form
            // POST's response, and force an immediate refresh on the visible tab.
            await page.GotoAsync("/Admin/Instance/Translation");
            await page.EvaluateAsync("() => document.dispatchEvent(new Event('visibilitychange'))");
            await page.WaitForFunctionAsync("() => document.querySelector('[data-translation-download]')?.textContent?.includes('50%') === true", null,
                new() { Timeout = 15_000 });
            await page.UnrouteAsync("**/api/v1/admin/translation/models");

            var response = await page.GotoAsync("/Read/1");
            Assert.AreEqual(200, response!.Status);
            await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Chapter One");
            // The quality toggle lives inside the initially closed language panel.
            await page.Locator("[data-action=language]").ClickAsync();
            await Assertions.Expect(page.Locator("[data-language-choice=de]")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("[data-local-quality-toggle]")).ToBeVisibleAsync();

            await page.Locator("[data-language-choice=de]").ClickAsync();
            await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Kapitel Eins", new() { Timeout = 60_000 });
            Assert.IsTrue(ollama.Generations > 0, "The Reader must actually call local TranslateGemma.");
            var firstCount = ollama.Generations;
            await page.Locator("[data-language-choice=original]").ClickAsync();
            await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Chapter One");
            await page.Locator("[data-language-choice=de]").ClickAsync();
            await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Kapitel Eins");
            Assert.AreEqual(firstCount, ollama.Generations, "Switching back must reuse the validated local cache.");

            response = await page.GotoAsync("/Read/1?s=1");
            Assert.AreEqual(200, response!.Status);
            await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Kapitel Eins", new() { Timeout = 60_000 });
            Assert.IsTrue(ollama.Generations > firstCount, "A second section must be translated too.");
            await page.SetViewportSizeAsync(390, 844);
            await Assertions.Expect(page.Locator(".reader__footer")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("[data-role=seek]")).ToBeVisibleAsync();

            response = await page.ReloadAsync();
            Assert.AreEqual(200, response!.Status);
            await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Kapitel Eins", new() { Timeout = 60_000 });
        }

        // A private loopback Ollama fixture: the test executes the real Reader UI,
        // HTTP API, profile permissions and cache without pulling a multi-GB model.
        private sealed class FakeOllama : IAsyncDisposable
        {
            private readonly HttpListener _listener = new();
            private readonly Task _running;
            private int _generations;
            public Uri Address { get; }
            public int Generations => Volatile.Read(ref _generations);

            private FakeOllama(int port)
            {
                Address = new Uri("http://127.0.0.1:" + port + "/");
                _listener.Prefixes.Add(Address.ToString());
                _listener.Start();
                _running = ServeAsync();
            }

            public static FakeOllama Start()
            {
                using var socket = new TcpListener(IPAddress.Loopback, 0);
                socket.Start();
                var port = ((IPEndPoint)socket.LocalEndpoint).Port;
                socket.Stop();
                return new FakeOllama(port);
            }

            private async Task ServeAsync()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch (HttpListenerException) { break; }
                    catch (ObjectDisposedException) { break; }

                    try
                    {
                        var path = context.Request.Url!.AbsolutePath;
                        string body;
                        if (path == "/api/tags")
                            body = """{"models":[{"name":"translategemma:4b","digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}""";
                        else if (path == "/api/ps")
                            body = """{"models":[]}""";
                        else if (path == "/api/generate")
                        {
                            Interlocked.Increment(ref _generations);
                            body = """{"response":"Kapitel Eins","eval_count":10,"eval_duration":1000000000}""";
                        }
                        else
                        {
                            context.Response.StatusCode = 404;
                            body = """{"error":"unknown fixture endpoint"}""";
                        }

                        var bytes = Encoding.UTF8.GetBytes(body);
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }
                    finally { context.Response.Close(); }
                }
            }

            public async ValueTask DisposeAsync()
            {
                _listener.Stop();
                _listener.Close();
                await _running;
            }
        }
    }
}
