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
            Steps.WriteEpub(Path.Combine(folders.Library, "Books", "translation-reader.epub"), "Translation Reader", "Local fixture", paragraphsPerChapter: 8);
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
            await CheckMachinesAsync(page);

            // Benchmarks and model download state now live on the Setup tab.
            // Admin: model availability, benchmark and live status on the same
            // authenticated V1 endpoint used by the management page.
            var admin = await page.GotoAsync("/Admin/Instance/Translation?tab=setup");
            Assert.AreEqual(200, admin!.Status);
            await Assertions.Expect(page.Locator("form[data-translation-submit=benchmark]")).ToHaveCountAsync(1);
            await page.Locator("form[data-translation-submit=benchmark] button[type=submit]").ClickAsync();
            await Assertions.Expect(page.Locator("body")).ToContainTextAsync("id → de: Kapitel Eins", new() { Timeout = 60_000 });
            var benchmarkGenerations = ollama.Generations;

            await page.RouteAsync("**/api/v1/admin/translation/models", route => route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "application/json",
                Body = """{"downloadState":"downloading","downloadModel":"translategemma:4b","downloadProgress":50}"""
            }));
            // Exercise the entrypoint on a normal Admin GET, rather than the form
            // POST's response, and force an immediate refresh on the visible tab.
            await page.GotoAsync("/Admin/Instance/Translation?tab=setup");
            await page.EvaluateAsync("() => document.dispatchEvent(new Event('visibilitychange'))");
            await page.WaitForFunctionAsync("() => document.querySelector('[data-translation-download]')?.textContent?.includes('50%') === true", null,
                new() { Timeout = 15_000 });
            await page.UnrouteAsync("**/api/v1/admin/translation/models");

            var response = await page.GotoAsync("/Read/1");
            Assert.AreEqual(200, response!.Status);
            await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Chapter One");
            // The language choices live inside the initially closed language panel; the server's TranslateGemma is the only translator.
            await page.Locator("[data-action=language]").ClickAsync();
            await Assertions.Expect(page.Locator("[data-language-choice=de]")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("[data-local-quality-toggle]")).ToHaveCountAsync(0);
            // A German profile immediately applies its default reading language.
            // Confirm the engine, then deliberately switch to Original and back.
            await Assertions.Expect(page.Locator("[data-translation-provider]")).ToContainTextAsync("TranslateGemma");
            await page.Locator("[data-language-choice=original]").ClickAsync();
            await Assertions.Expect(page.Locator("[data-translation-provider]")).ToContainTextAsync("Original");
            await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Chapter One");
            await page.Locator("[data-language-choice=de]").ClickAsync();
            await Assertions.Expect(page.Locator("[data-translation-provider]")).ToContainTextAsync("TranslateGemma");
            await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Kapitel Eins", new() { Timeout = 60_000 });
            // Visible text is progressive; do not switch away after the first block and
            // then mistake the remaining work for a cache miss.
            await page.WaitForFunctionAsync("() => document.querySelector('.reader')?.dataset.translationState === 'ready'", null, new() { Timeout = 120_000 });
            Assert.IsTrue(ollama.Generations > benchmarkGenerations, "The Reader must call local TranslateGemma separately from the Admin benchmark.");
            var firstCount = ollama.Generations;
            await page.Locator("[data-language-choice=original]").ClickAsync();
            await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Chapter One");
            await page.Locator("[data-language-choice=de]").ClickAsync();
            await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Kapitel Eins");
            await page.WaitForFunctionAsync("() => document.querySelector('.reader')?.dataset.translationState === 'ready'", null, new() { Timeout = 60_000 });
            Assert.AreEqual(firstCount, ollama.Generations, "Switching back must reuse the validated local cache.");

            Assert.AreEqual("de", await jularr.ScalarAsync(
                """SELECT "Language" FROM "ReadingPreferences" WHERE "WorkId" = (SELECT "WorkId" FROM "MediaAssets" WHERE "Id" = 1)"""),
                "The language must remain persisted for the same book.");
            response = await page.GotoAsync("/Read/1?s=1");
            Assert.AreEqual(200, response!.Status);
            var nextReader = page.Locator(".reader");
            await Assertions.Expect(nextReader).ToHaveAttributeAsync("data-reading-language", "de");
            await Assertions.Expect(nextReader).ToHaveAttributeAsync("data-local-model", "ollama:translategemma:4b");
            try
            {
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Kapitel Eins", new() { Timeout = 60_000 });
            }
            catch
            {
                var state = await nextReader.EvaluateAsync<string>("el => JSON.stringify({ language: el.dataset.language, chosen: el.dataset.readingLanguage, model: el.dataset.localModel, engine: el.dataset.translationEngine, translation: el.dataset.translationState, url: el.dataset.localTranslationUrl })");
                Evidence.Log("[reader-translation] chapter 2: " + state + "; browser problems: " + string.Join(" | ", session.Problems));
                await Evidence.SaveAsync(page, "reader-translation-chapter-2-failed");
                throw;
            }
            await page.WaitForFunctionAsync("() => document.querySelector('.reader')?.dataset.translationState === 'ready'", null, new() { Timeout = 120_000 });
            Assert.IsTrue(ollama.Generations > firstCount, "A second section must be translated too.");
            await page.SetViewportSizeAsync(390, 844);
            await Assertions.Expect(page.Locator(".reader__footer")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("[data-role=seek]")).ToBeVisibleAsync();

            response = await page.ReloadAsync();
            Assert.AreEqual(200, response!.Status);
            await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Kapitel Eins", new() { Timeout = 60_000 });
        }

        [TestMethod]
        [Timeout(180_000)]
        public async Task AnotherModel_RetranslatesTheParagraph_ForEveryReader_AndNamesItsModel()
        {
            using var folders = new Folders("Books");
            Steps.WriteEpub(Path.Combine(folders.Library, "Books", "retranslation-reader.epub"), "Retranslation Reader", "Local fixture", paragraphsPerChapter: 4);
            await using var ollama = FakeOllama.Start(larger: true);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Storage:LocalPaths:0"] = folders.Library,
                ["LocalTranslation:Endpoint"] = ollama.Address.ToString(),
                ["HTTPS_PROXY"] = "http://127.0.0.1:9"
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await ReadingPathTests.AddBookAsync(page, folders, "Retranslation Reader");
            await jularr.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            await CheckMachinesAsync(page);
            await jularr.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"TranslationModel\" = 'ollama:translategemma:4b' WHERE \"Id\" = 1");

            // The book in German, with the Owner's default model; the re-translation shows only while the server's translation does.
            Assert.AreEqual(200, (await page.GotoAsync("/Read/1"))!.Status);
            await page.Locator("[data-action=language]").ClickAsync();
            var area = page.Locator("[data-role=retranslate]");
            await page.Locator("[data-language-choice=original]").ClickAsync();
            await Assertions.Expect(area).ToBeHiddenAsync();
            await page.Locator("[data-language-choice=de]").ClickAsync();
            await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Kapitel Eins", new() { Timeout = 60_000 });
            await page.WaitForFunctionAsync("() => document.querySelector('.reader')?.dataset.translationState === 'ready'", null, new() { Timeout = 120_000 });
            await Assertions.Expect(page.Locator("[data-role=content] [data-translation-model]")).ToHaveCountAsync(0);

            // The language panel offers the other model and names the model of the paragraph at the top of the page.
            await Assertions.Expect(area).ToBeVisibleAsync();
            await Assertions.Expect(area.Locator("[data-retranslate-model='ollama:translategemma:12b']")).ToHaveAttributeAsync("aria-pressed", "true");
            await Assertions.Expect(area.Locator("[data-role=retranslate-status]")).ToContainTextAsync("translategemma:4b");
            await Evidence.SaveAsync(page, "reader-retranslate-menu-desktop");

            // The paragraph alone takes the larger model's text and names it; the others keep theirs.
            await area.Locator("[data-retranslate=block]").ClickAsync();
            try
            {
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Erstes Kapitel", new() { Timeout = 60_000 });
            }
            catch
            {
                Evidence.Log("[reader-retranslate] browser problems: " + string.Join(" | ", session.Problems));
                await Evidence.SaveAsync(page, "reader-retranslate-failed");
                throw;
            }
            await Assertions.Expect(area.Locator("[data-role=retranslate-status]")).ToContainTextAsync("translategemma:12b");
            await Assertions.Expect(page.Locator("[data-role=content] [data-translation-model='ollama:translategemma:12b']")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Kapitel Eins");
            await Evidence.SaveAsync(page, "reader-retranslate-result-desktop");

            // Kept on the server for every reader: on a phone the reopened book shows it again, and the menu fits the narrow screen.
            await page.SetViewportSizeAsync(390, 844);
            Assert.AreEqual(200, (await page.ReloadAsync())!.Status);
            await page.WaitForFunctionAsync("() => document.querySelector('.reader')?.dataset.translationState === 'ready'", null, new() { Timeout = 120_000 });
            await Assertions.Expect(page.Locator("[data-role=content] [data-translation-model='ollama:translategemma:12b']")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Erstes Kapitel");
            await Evidence.SaveAsync(page, "reader-retranslate-result-phone");
            var tool = page.Locator(".reader__top [data-action=language]");
            if (!await tool.IsVisibleAsync())
            {
                await page.Locator("[data-action=more]").ClickAsync();
                tool = page.Locator("[data-panel=more] [data-overflow-for=language]");
            }
            await tool.ClickAsync();
            await Assertions.Expect(area).ToBeVisibleAsync();
            var box = await page.Locator("[data-panel=language]").BoundingBoxAsync();
            Assert.IsNotNull(box);
            Assert.IsTrue(box.X >= 0 && box.X + box.Width <= 390, "The language panel fits the phone: " + box.X + " + " + box.Width);
            foreach (var button in await area.Locator("button:visible").AllAsync())
            {
                var size = await button.BoundingBoxAsync();
                Assert.IsTrue(size is { Height: >= 32 } && size.X + size.Width <= 390, "A re-translation button fits the phone.");
            }
            await Evidence.SaveAsync(page, "reader-retranslate-menu-phone");
        }

        // The machines are checked every thirty seconds; Check now on the translation page asks them at once.
        internal static async Task CheckMachinesAsync(IPage page)
        {
            await page.GotoAsync("/Admin/Instance/Translation?tab=setup");
            await page.Locator("form[data-translation-submit=check] button[type=submit]").ClickAsync();
            await Assertions.Expect(page.Locator("[data-translation-machine]").First).ToContainTextAsync("Checked at");
        }

        // A private loopback Ollama fixture: the test executes the real Reader UI,
        // HTTP API, profile permissions and cache without pulling a multi-GB model.
        internal sealed class FakeOllama : IAsyncDisposable
        {
            private readonly HttpListener _listener = new();
            private readonly Task _running;
            private int _generations;
            // A second, larger model answers "Erstes Kapitel" for every block.
            private readonly bool _larger;
            public Uri Address { get; }
            public int Generations => Volatile.Read(ref _generations);

            // Echoing answers every block with "DE " and its own text, which passes the translation checks; otherwise every answer is "Kapitel Eins".
            private readonly bool _echo;

            private FakeOllama(int port, bool larger, bool echo)
            {
                _larger = larger;
                Address = new Uri("http://127.0.0.1:" + port + "/");
                _echo = echo;
                _listener.Prefixes.Add(Address.ToString());
                _listener.Start();
                _running = ServeAsync();
            }

            public static FakeOllama Start(bool larger = false, bool echo = false)
            {
                using var socket = new TcpListener(IPAddress.Loopback, 0);
                socket.Start();
                var port = ((IPEndPoint)socket.LocalEndpoint).Port;
                socket.Stop();
                return new FakeOllama(port, larger, echo);
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
                            body = _larger
                                ? """{"models":[{"name":"translategemma:4b","digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},{"name":"translategemma:12b","digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}]}"""
                                : """{"models":[{"name":"translategemma:4b","digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}""";
                        else if (path == "/api/ps")
                            body = """{"models":[]}""";
                        else if (path == "/api/generate")
                        {
                            Interlocked.Increment(ref _generations);
                            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                            var request = await reader.ReadToEndAsync();
                            var answer = request.Contains("\"translategemma:12b\"", StringComparison.Ordinal) ? "Erstes Kapitel" : "Kapitel Eins";
                            if (_echo)
                            {
                                var prompt = System.Text.Json.JsonDocument.Parse(request).RootElement.GetProperty("prompt").GetString()!;
                                answer = "DE " + prompt[(prompt.LastIndexOf('\n') + 1)..];
                            }
                            body = System.Text.Json.JsonSerializer.Serialize(new { response = answer, eval_count = 10, eval_duration = 1000000000 });
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
