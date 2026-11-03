using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Translation.V1;
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
            StringAssert.Contains(readerHtml, "data-local-model=" + (char)34 + "ollama:translategemma:4b" + (char)34);
            StringAssert.Contains(readerHtml, "reader__footer");
            StringAssert.Contains(readerHtml, "data-role=" + (char)34 + "seek" + (char)34);
            StringAssert.Contains(readerHtml, "data-role=" + (char)34 + "turn-tools" + (char)34);
            // The administration view must expose model management and the live status
            // without changing the ordinary Reader's permissions.
            using var adminPage = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Translation?tab=setup");
            Assert.AreEqual(HttpStatusCode.OK, adminPage.StatusCode, await adminPage.Content.ReadAsStringAsync());
            var adminHtml = await adminPage.Content.ReadAsStringAsync();
            StringAssert.Contains(adminHtml, "data-translation-download");
            StringAssert.Contains(adminHtml, "translategemma:4b");

            ollama.Unavailable = true;
            using var offlineAdmin = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Translation?tab=setup");
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

            var baseUrl = "/api/v1/reading/documents/" + document.DocumentId + "/sections/0/local-translations/de/ollama:translategemma:4b";
            // A model id without its provider is no model.
            using var bare = await host.SendAsync(HttpMethod.Get, baseUrl.Replace("ollama:", "", StringComparison.Ordinal) + "?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.BadRequest, bare.StatusCode);
            var request = baseUrl + "/blocks/0";

            using var before = await host.SendAsync(HttpMethod.Get, baseUrl + "?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.OK, before.StatusCode, await before.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(Array.Empty<string>(), JsonSerializer.Deserialize<string[]>((await before.Content.ReadFromJsonAsync<LocalReadingTranslationV1>(JsonSerializerOptions.Web))!.Blocks));

            using var generated = await host.SendAsync(HttpMethod.Post, request, new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.OK, generated.StatusCode, await generated.Content.ReadAsStringAsync());
            Assert.AreEqual("Kapitel Eins", (await generated.Content.ReadFromJsonAsync<LocalReadingBlockV1>())!.Text);
            Assert.AreEqual(1, ollama.GenerationCount);

            using var cached = await host.SendAsync(HttpMethod.Post, request, new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.OK, cached.StatusCode, await cached.Content.ReadAsStringAsync());
            Assert.AreEqual(1, ollama.GenerationCount, "The cached block should not be regenerated.");
            using var shared = await host.SendAsync(HttpMethod.Get, baseUrl + "?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.OK, shared.StatusCode, await shared.Content.ReadAsStringAsync());
            Assert.AreEqual("Kapitel Eins", JsonSerializer.Deserialize<string[]>((await shared.Content.ReadFromJsonAsync<LocalReadingTranslationV1>(JsonSerializerOptions.Web))!.Blocks)![0]);

            using var reopened = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + first + "/open");
            Assert.AreEqual(HttpStatusCode.OK, reopened.StatusCode);
            Assert.IsFalse((await reopened.Content.ReadFromJsonAsync<ReadingDocumentV1>())!.TranslatedLanguages.Contains("local-v1"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/%'"));

            // A new version of the model: the earlier text stays visible, marked stale, until this version has replaced it.
            ollama.Digest = new string('b', 64);
            using var stale = await host.SendAsync(HttpMethod.Get, baseUrl + "?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.OK, stale.StatusCode);
            var staleRead = (await stale.Content.ReadFromJsonAsync<LocalReadingTranslationV1>(JsonSerializerOptions.Web))!;
            Assert.AreEqual("Kapitel Eins", JsonSerializer.Deserialize<string?[]>(staleRead.Blocks)![0]);
            Assert.AreEqual(("ollama:translategemma:4b", true), (staleRead.Models[0], staleRead.Stale[0]));
            using var regenerated = await host.SendAsync(HttpMethod.Post, request, new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.OK, regenerated.StatusCode, await regenerated.Content.ReadAsStringAsync());
            Assert.AreEqual(2, ollama.GenerationCount);
            using var renewed = await host.SendAsync(HttpMethod.Get, baseUrl + "?sourceLanguage=en");
            Assert.IsFalse((await renewed.Content.ReadFromJsonAsync<LocalReadingTranslationV1>(JsonSerializerOptions.Web))!.Stale[0]);

            // A block someone chose another model for shows that model's text, for every reader; the other blocks keep the default's.
            await host.Fixture.RunSqlAsync($$"""
                INSERT INTO "ReadingResources" ("ReadingDocumentId", "Path", "MediaType", "Bytes")
                SELECT resource."ReadingDocumentId", ':localtranslation/v4/translategemma:12b/de/00000', resource."MediaType",
                    convert_to(replace(convert_from(resource."Bytes", 'UTF8'), 'Kapitel Eins', 'Erstes Kapitel'), 'UTF8')
                FROM "ReadingResources" AS resource
                WHERE resource."ReadingDocumentId" = {{document.DocumentId}} AND resource."Path" = ':localtranslation/v4/translategemma:4b/de/00000';
                INSERT INTO "ReadingTranslationChoices" ("ReadingDocumentId", "SectionIndex", "Language", "BlockIndex", "Model")
                VALUES ({{document.DocumentId}}, 0, 'de', 0, 'ollama:translategemma:12b'), ({{document.DocumentId}}, 0, 'fr', 1, 'ollama:translategemma:12b');
                """);
            using var mixed = await host.SendAsync(HttpMethod.Get, baseUrl + "?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.OK, mixed.StatusCode, await mixed.Content.ReadAsStringAsync());
            var mixedRead = (await mixed.Content.ReadFromJsonAsync<LocalReadingTranslationV1>(JsonSerializerOptions.Web))!;
            var mixedBlocks = JsonSerializer.Deserialize<string?[]>(mixedRead.Blocks)!;
            Assert.AreEqual(("Erstes Kapitel", "ollama:translategemma:12b", false), (mixedBlocks[0], mixedRead.Models[0], mixedRead.Stale[0]));
            Assert.IsTrue(mixedBlocks.Length > 1 && mixedBlocks.Skip(1).All(block => block is null) && mixedRead.Models.Skip(1).All(model => model is null), mixedRead.Blocks);

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

        [TestMethod]
        public async Task DefaultModel_TheOwnerChoosesAnInstalledModel_AndEveryServerTranslationFollowsIt()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434" },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");

            // Nothing chosen: the largest installed TranslateGemma, offered on the setup tab among the installed models.
            Assert.AreEqual("ollama:translategemma:4b", await AvailableAsync(host));
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Translation?tab=setup");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "?handler=Model");
            StringAssert.Contains(html, "value=" + (char)34 + "ollama:translategemma:4b" + (char)34);

            // A model no machine has translates nothing rather than another model; an id without provider or an unapproved model is refused.
            using (var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/translation/model", new TranslationModelDataV1("ollama:translategemma:12b")))
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.IsNull(await AvailableAsync(host));
            using (var models = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/translation/models"))
            {
                var read = (await models.Content.ReadFromJsonAsync<LocalTranslationModelsV1>(JsonSerializerOptions.Web))!;
                Assert.AreEqual("ollama:translategemma:12b", read.DefaultModel);
                CollectionAssert.AreEqual(new[] { "ollama:translategemma:4b" }, read.Choices);
            }
            foreach (var refused in new[] { "translategemma:4b", "ollama:untrusted:90b", "codex:gpt-5" })
            {
                using var response = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/translation/model", new TranslationModelDataV1(refused));
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, refused);
            }

            using (var installed = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/translation/model", new TranslationModelDataV1("ollama:translategemma:4b")))
                Assert.AreEqual(HttpStatusCode.OK, installed.StatusCode);
            Assert.AreEqual("ollama:translategemma:4b", await AvailableAsync(host));
            using (var automatic = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/translation/model", new TranslationModelDataV1(null)))
                Assert.AreEqual(HttpStatusCode.OK, automatic.StatusCode);
            Assert.IsNull(await host.ScalarAsync("SELECT \"TranslationModel\" FROM \"InstanceSettings\" WHERE \"Id\" = 1") as string);
        }

        private static async Task<string?> AvailableAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/translation/local/availability");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<LocalTranslationAvailabilityV1>(JsonSerializerOptions.Web))!.Model;
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
