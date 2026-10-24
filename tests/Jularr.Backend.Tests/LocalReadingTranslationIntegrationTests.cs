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

            // An empty Mozilla catalogue still reports English as a bridge; a German book
            // must not offer that as a usable browser route without a de->en model.
            mozilla.EmptyCatalogue = true;
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'de' WHERE \"Id\" = " + document.DocumentId);
            using var germanReader = await host.SendAsync(HttpMethod.Get, "/Read/" + first);
            Assert.AreEqual(HttpStatusCode.OK, germanReader.StatusCode, await germanReader.Content.ReadAsStringAsync());
            StringAssert.Contains(await germanReader.Content.ReadAsStringAsync(), "data-browser-translation-targets=\"\"");
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'en' WHERE \"Id\" = " + document.DocumentId);

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
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v2/%'"));

            ollama.Digest = new string('b', 64);
            using var stale = await host.SendAsync(HttpMethod.Get, baseUrl + "?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.OK, stale.StatusCode);
            CollectionAssert.AreEqual(Array.Empty<string>(), JsonSerializer.Deserialize<string[]>((await stale.Content.ReadFromJsonAsync<ReadingTranslationV1>())!.Blocks));
            using var regenerated = await host.SendAsync(HttpMethod.Post, request, new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.OK, regenerated.StatusCode, await regenerated.Content.ReadAsStringAsync());
            Assert.AreEqual(2, ollama.GenerationCount);

            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Translation'");
            using var unavailable = await host.SendAsync(HttpMethod.Post, request, new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.Forbidden, unavailable.StatusCode);
        }

        private sealed class UnavailableMozilla : HttpMessageHandler
        {
            public bool EmptyCatalogue { get; set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(EmptyCatalogue
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""") }
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        private sealed class FakeOllama : HttpMessageHandler
        {
            public string Digest { get; set; } = new string('a', 64);
            public int GenerationCount { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body;
                if (request.RequestUri!.AbsolutePath == "/api/tags")
                    body = JsonSerializer.Serialize(new { models = new[] { new { name = "translategemma:4b", digest = Digest } } });
                else if (request.RequestUri.AbsolutePath == "/api/ps")
                    body = """{"models":[]}""";
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
