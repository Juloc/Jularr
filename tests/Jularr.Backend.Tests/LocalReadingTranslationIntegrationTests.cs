using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Data.User.Translation.V1;
using Jularr.Integrations.Logic.Translation;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class LocalReadingTranslationIntegrationTests
    {
        [TestMethod]
        public async Task Reader_LocalModelCachesAuthorizedBlocksWithoutInventingEpubLanguage()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            var mozilla = new UnavailableMozilla();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434" },
                services: services =>
                {
                    services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama);
                    services.AddHttpClient<MozillaTranslationsClient>().ConfigurePrimaryHttpMessageHandler(() => mozilla);
                });
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            var (first, _) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            using var opened = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + first + "/open");
            Assert.AreEqual(HttpStatusCode.OK, opened.StatusCode, await opened.Content.ReadAsStringAsync());
            var document = (await opened.Content.ReadFromJsonAsync<ReadingDocumentV1>())!;
            using var reader = await host.SendAsync(HttpMethod.Get, "/Read/" + first);
            Assert.AreEqual(HttpStatusCode.OK, reader.StatusCode, await reader.Content.ReadAsStringAsync());
            var readerHtml = await reader.Content.ReadAsStringAsync();
            StringAssert.Contains(readerHtml, "data-language-choice=" + (char)34 + "de" + (char)34);
            StringAssert.Contains(readerHtml, "data-local-model=" + (char)34 + "translategemma:4b" + (char)34);
            StringAssert.Contains(readerHtml, "reader__footer");
            StringAssert.Contains(readerHtml, "data-role=" + (char)34 + "seek" + (char)34);
            StringAssert.Contains(readerHtml, "data-role=" + (char)34 + "turn-tools" + (char)34);
            // The administration view must expose model management and the live status
            // without changing the ordinary Reader's permissions.
            using var adminPage = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Translation");
            Assert.AreEqual(HttpStatusCode.OK, adminPage.StatusCode, await adminPage.Content.ReadAsStringAsync());
            var adminHtml = await adminPage.Content.ReadAsStringAsync();
            StringAssert.Contains(adminHtml, "data-translation-download");
            StringAssert.Contains(adminHtml, "translategemma:4b");

            ollama.Unavailable = true;
            using var offlineAdmin = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Translation");
            Assert.AreEqual(HttpStatusCode.OK, offlineAdmin.StatusCode, await offlineAdmin.Content.ReadAsStringAsync());
            var offlineHtml = await offlineAdmin.Content.ReadAsStringAsync();
            StringAssert.Contains(offlineHtml, "data-translation-download");
            StringAssert.Contains(offlineHtml, "translategemma:4b", "Model choices must stay visible when Ollama is offline.");
            ollama.Unavailable = false;

            // The server's TranslateGemma is the Reader's only translator; Mozilla is neither asked nor offered (#1726).
            StringAssert.Contains(readerHtml, "data-translation-engine=" + (char)34 + "gemma" + (char)34);
            Assert.IsFalse(readerHtml.Contains("data-local-quality-toggle", StringComparison.Ordinal));
            Assert.IsFalse(readerHtml.Contains("data-route-url", StringComparison.Ordinal));
            Assert.AreEqual(0, mozilla.Requests, "Opening the Reader must not reach Mozilla.");

            var baseUrl = "/api/v1/reading/documents/" + document.DocumentId + "/sections/0/local-translations/de/translategemma:4b";
            var request = baseUrl + "/blocks/0";

            using var before = await host.SendAsync(HttpMethod.Get, baseUrl + "?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.OK, before.StatusCode, await before.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(Array.Empty<string>(), JsonSerializer.Deserialize<string[]>((await before.Content.ReadFromJsonAsync<ReadingTranslationV1>())!.Blocks));

            using var generated = await host.SendAsync(HttpMethod.Post, request, new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.OK, generated.StatusCode, await generated.Content.ReadAsStringAsync());
            Assert.AreEqual("Kapitel Eins", (await generated.Content.ReadFromJsonAsync<LocalReadingBlockV1>())!.Text);
            Assert.AreEqual(1, ollama.GenerationCount);

            using var cached = await host.SendAsync(HttpMethod.Post, request, new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.OK, cached.StatusCode, await cached.Content.ReadAsStringAsync());
            Assert.AreEqual(1, ollama.GenerationCount, "The cached block should not be regenerated.");
            using var shared = await host.SendAsync(HttpMethod.Get, baseUrl + "?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.OK, shared.StatusCode, await shared.Content.ReadAsStringAsync());
            Assert.AreEqual("Kapitel Eins", JsonSerializer.Deserialize<string[]>((await shared.Content.ReadFromJsonAsync<ReadingTranslationV1>())!.Blocks)![0]);

            using var reopened = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + first + "/open");
            Assert.AreEqual(HttpStatusCode.OK, reopened.StatusCode);
            Assert.IsFalse((await reopened.Content.ReadFromJsonAsync<ReadingDocumentV1>())!.TranslatedLanguages.Contains("local-v1"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/%'"));

            ollama.Digest = new string('b', 64);
            using var stale = await host.SendAsync(HttpMethod.Get, baseUrl + "?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.OK, stale.StatusCode);
            CollectionAssert.AreEqual(Array.Empty<string>(), JsonSerializer.Deserialize<string[]>((await stale.Content.ReadFromJsonAsync<ReadingTranslationV1>())!.Blocks));
            using var regenerated = await host.SendAsync(HttpMethod.Post, request, new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.OK, regenerated.StatusCode, await regenerated.Content.ReadAsStringAsync());
            Assert.AreEqual(2, ollama.GenerationCount);

            // The order's first machine answers but fails the block: the same request takes it to the next machine at once.
            ollama.FailingHost = "10.9.9.9";
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"TranslationMachines\" = '{http://10.9.9.9:11434/,http://ollama:11434/}'");
            using var passedOn = await host.SendAsync(HttpMethod.Post, baseUrl.Replace("/de/", "/fr/", StringComparison.Ordinal) + "/blocks/0", new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.OK, passedOn.StatusCode, await passedOn.Content.ReadAsStringAsync());
            Assert.AreEqual(3, ollama.GenerationCount);
            Assert.AreEqual(1, ollama.FailedGenerations);

            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Translation'");
            using var unavailable = await host.SendAsync(HttpMethod.Post, request, new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.Forbidden, unavailable.StatusCode);
        }

        private sealed class UnavailableMozilla : HttpMessageHandler
        {
            public int Requests { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }
        }

        private sealed class FakeOllama : HttpMessageHandler
        {
            public string Digest { get; set; } = new string('a', 64);
            public bool Unavailable { get; set; }
            public int GenerationCount { get; private set; }
            public string? FailingHost { get; set; }
            public int FailedGenerations { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (Unavailable) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                string body;
                if (request.RequestUri!.AbsolutePath == "/api/tags")
                    body = JsonSerializer.Serialize(new { models = new[] { new { name = "translategemma:4b", digest = Digest } } });
                else if (request.RequestUri.AbsolutePath == "/api/ps")
                    body = """{"models":[]}""";
                else if (request.RequestUri.AbsolutePath == "/api/generate" && request.RequestUri.Host == FailingHost)
                {
                    FailedGenerations++;
                    throw new HttpRequestException("The machine dropped out.");
                }
                else if (request.RequestUri.AbsolutePath == "/api/generate")
                {
                    var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    Assert.AreEqual("translategemma:4b", input.RootElement.GetProperty("model").GetString());
                    GenerationCount++;
                    body = """{"response":"Kapitel Eins","eval_count":2,"eval_duration":200000000}""";
                }
                else throw new InvalidOperationException("Unexpected Ollama API: " + request.RequestUri.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
        }
    }
}
