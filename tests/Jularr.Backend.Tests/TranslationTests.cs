using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.User.Instance.V1;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Data.User.Translation.V1;
using Jularr.Integrations.Logic.Translation;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class TranslationTests
    {
        [TestMethod]
        public async Task Route_UsesTheNewestModelForEveryBuildAndBridgesThroughEnglish()
        {
            using var storage = new TemporaryDirectory();
            using var models = new TemporaryDirectory();
            var mozilla = new FakeMozilla();
            await using var host = await CreateHostAsync(storage.Path, models.Path, mozilla);

            // English to German: the engine of the supported major version, then the newest released en-de files.
            using var direct = await host.SendAsync(HttpMethod.Get, "/api/v1/translation/routes/en/de");
            Assert.AreEqual(HttpStatusCode.OK, direct.StatusCode, await direct.Content.ReadAsStringAsync());
            var files = (await direct.Content.ReadFromJsonAsync<BaseDataList<TranslationFileV1>>())!;
            CollectionAssert.AreEqual(new[] { "0:engine", "1:model", "1:lex", "1:vocab" }, files.Select(file => file.Step + ":" + file.FileType).ToArray());
            StringAssert.Contains(files[0].Url, FakeMozilla.Hash("engine-3.1") + ".wasm");
            StringAssert.Contains(files[1].Url, FakeMozilla.Hash("ende-2.1-model") + ".bin");
            Assert.AreEqual("int8shiftAlphaAll", files[1].Precision);

            // German to Japanese goes through English; Japanese files for Android only are left out.
            using var pivot = await host.SendAsync(HttpMethod.Get, "/api/v1/translation/routes/de/ja");
            var steps = (await pivot.Content.ReadFromJsonAsync<BaseDataList<TranslationFileV1>>())!;
            CollectionAssert.AreEqual(new[] { "0:engine", "1:model", "1:lex", "1:vocab", "2:model", "2:srcvocab", "2:trgvocab" },
                steps.Select(file => file.Step + ":" + file.FileType).ToArray());
            StringAssert.Contains(steps[4].Url, FakeMozilla.Hash("enja-1.0-model"));

            // Every language with released models into English can be read from, every one out of it read in.
            using var languages = await host.SendAsync(HttpMethod.Get, "/api/v1/translation/languages");
            var offered = (await languages.Content.ReadFromJsonAsync<TranslationLanguagesV1>())!;
            Assert.AreEqual(HttpStatusCode.OK, languages.StatusCode, await languages.Content.ReadAsStringAsync());
            Assert.AreEqual(("de en", "de en ja"), (string.Join(" ", offered.Sources.Order()), string.Join(" ", offered.Targets.Order())),
                "Regional or script-tagged Mozilla models must not invalidate the Reader language contract.");
        }

        [TestMethod]
        public async Task Files_AreFetchedOnceCheckedAgainstTheirHashAndKeptForBrowsers()
        {
            using var storage = new TemporaryDirectory();
            using var models = new TemporaryDirectory();
            var mozilla = new FakeMozilla();
            await using var host = await CreateHostAsync(storage.Path, models.Path, mozilla);
            var name = FakeMozilla.Hash("engine-3.1") + ".wasm";

            using var first = await host.SendAsync(HttpMethod.Get, "/api/v1/translation/files/" + name);
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("engine-3.1"), await first.Content.ReadAsByteArrayAsync());
            Assert.AreEqual("application/wasm", first.Content.Headers.ContentType!.MediaType);
            StringAssert.Contains(first.Headers.CacheControl!.ToString(), "immutable");
            using var second = await host.SendAsync(HttpMethod.Get, "/api/v1/translation/files/" + name);
            Assert.AreEqual(1, mozilla.Downloads.Count(download => download.Contains("engine-3.1", StringComparison.Ordinal)));

            // A file that does not match its published hash is not kept; names Mozilla does not list are unknown.
            using var tampered = await host.SendAsync(HttpMethod.Get, "/api/v1/translation/files/" + FakeMozilla.Hash("ende-2.1-lex") + ".bin");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, tampered.StatusCode);
            Assert.AreEqual(1, Directory.GetFiles(models.Path).Length);
            using var unknown = await host.SendAsync(HttpMethod.Get, "/api/v1/translation/files/" + new string('a', 64) + ".bin");
            Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        [TestMethod]
        public async Task Sections_KeepOneTranslationPerLanguageForEveryReader()
        {
            using var storage = new TemporaryDirectory();
            using var models = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path, models.Path, new FakeMozilla());
            var (first, _) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            using var opened = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + first + "/open");
            var document = (await opened.Content.ReadFromJsonAsync<ReadingDocumentV1>())!;
            using var sectionResponse = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + document.DocumentId + "/sections/0");
            var blocks = (await sectionResponse.Content.ReadFromJsonAsync<ReadingSectionV1>())!.Blocks;
            var route = "/api/v1/reading/documents/" + document.DocumentId + "/sections/0/translations/de";

            using var missing = await host.SendAsync(HttpMethod.Get, route);
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);

            // Every block gets its text; a wrong count or anything but texts is refused.
            using var wrong = await host.SendAsync(HttpMethod.Put, route, new SaveReadingTranslationDataV1(JsonSerializer.Serialize(new[] { "Nur einer" })));
            Assert.AreEqual(HttpStatusCode.BadRequest, wrong.StatusCode);
            using var markup = await host.SendAsync(HttpMethod.Put, route, new SaveReadingTranslationDataV1("{\"html\":\"<script>\"}"));
            Assert.AreEqual(HttpStatusCode.BadRequest, markup.StatusCode);
            var texts = Enumerable.Range(0, blocks).Select(index => "Block " + index).ToArray();
            using var saved = await host.SendAsync(HttpMethod.Put, route, new SaveReadingTranslationDataV1(JsonSerializer.Serialize(texts)));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var read = await host.SendAsync(HttpMethod.Get, route);
            CollectionAssert.AreEqual(texts, JsonSerializer.Deserialize<string[]>((await read.Content.ReadFromJsonAsync<ReadingTranslationV1>())!.Blocks));

            // Once every section is translated the book downloads in that language, marked as machine-translated; before, it does not.
            var ebook = "/api/v1/reading/documents/" + document.DocumentId + "/ebook";
            for (var index = 1; index < document.SectionCount; index++)
            {
                using var next = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + document.DocumentId + "/sections/" + index);
                var count = (await next.Content.ReadFromJsonAsync<ReadingSectionV1>())!.Blocks;
                if (count == 0)
                {
                    continue;
                }

                using var unfinished = await host.SendAsync(HttpMethod.Get, ebook + "?language=de");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unfinished.StatusCode);
                using var more = await host.SendAsync(HttpMethod.Put, "/api/v1/reading/documents/" + document.DocumentId + "/sections/" + index + "/translations/de",
                    new SaveReadingTranslationDataV1(JsonSerializer.Serialize(Enumerable.Range(0, count).Select(block => "Abschnitt " + index + " Block " + block))));
                Assert.AreEqual(HttpStatusCode.OK, more.StatusCode, await more.Content.ReadAsStringAsync());
            }

            using var reopened = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + first + "/open");
            CollectionAssert.AreEqual(new[] { "de" }, (await reopened.Content.ReadFromJsonAsync<ReadingDocumentV1>())!.TranslatedLanguages);
            using var untranslated = await host.SendAsync(HttpMethod.Get, ebook);
            Assert.AreEqual(HttpStatusCode.NotFound, untranslated.StatusCode);
            using var translated = await host.SendAsync(HttpMethod.Get, ebook + "?language=de");
            Assert.AreEqual(HttpStatusCode.OK, translated.StatusCode, await translated.Content.ReadAsStringAsync());
            Assert.AreEqual("First Book (de).epub", translated.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
            using var epub = new System.IO.Compression.ZipArchive(await translated.Content.ReadAsStreamAsync());
            var package = ReadingTests.Entry(epub, "OEBPS/content.opf");
            StringAssert.Contains(package, "<dc:language>de</dc:language>");
            StringAssert.Contains(package, "Machine-translated from");
            var section = ReadingTests.Entry(epub, "OEBPS/s00000.xhtml");
            ReadingTests.Xml(section);
            StringAssert.Contains(section, ">Block 0<");

            // The reader offers it; switched off, translation is gone.
            using var page = await host.SendAsync(HttpMethod.Get, "/Read/" + first);
            var reader = await page.Content.ReadAsStringAsync();
            StringAssert.Contains(reader, "data-translation-url=\"/api/v1/reading/documents/" + document.DocumentId + "/sections/0/translations/\"");
            StringAssert.Contains(reader, "href=\"" + ebook + "?language=de\"");

            // A book opens in the profile's language; its language button offers the original and the other allowed languages the server's TranslateGemma translates into.
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" = 'de-DE') WHERE \"Id\" = 101");
            Assert.AreEqual("de", await ReadingLanguageAsync(host, first));
            using var options = await host.SendAsync(HttpMethod.Get, "/Read/" + first);
            var offeredChoices = System.Text.RegularExpressions.Regex.Matches(await options.Content.ReadAsStringAsync(), "data-language-choice=\"([a-z]+)\"").Select(match => match.Groups[1].Value);
            CollectionAssert.AreEqual(new[] { "original", "de", "en" }, offeredChoices.ToArray());

            // The choice belongs to the book on every device of the profile; none follows the profile's language again.
            var choice = "/api/v1/reading/assets/" + first + "/language";
            using var original = await host.SendAsync(HttpMethod.Put, choice, new SaveReadingLanguageDataV1("original"));
            Assert.AreEqual(HttpStatusCode.OK, original.StatusCode, await original.Content.ReadAsStringAsync());
            Assert.AreEqual("original", await ReadingLanguageAsync(host, first));
            using var invalid = await host.SendAsync(HttpMethod.Put, choice, new SaveReadingLanguageDataV1("Klingon!"));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var reset = await host.SendAsync(HttpMethod.Put, choice, new SaveReadingLanguageDataV1(null));
            Assert.AreEqual(HttpStatusCode.OK, reset.StatusCode, await reset.Content.ReadAsStringAsync());
            Assert.AreEqual("de", await ReadingLanguageAsync(host, first));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Translation'");
            using var off = await host.SendAsync(HttpMethod.Get, route);
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
            using var capabilities = await host.SendAsync(HttpMethod.Get, "/api/v1/instance/capabilities");
            Assert.IsFalse((await capabilities.Content.ReadFromJsonAsync<InstanceCapabilitiesDetailsV1>())!.Modules.Contains("Translation"));
        }

        private static async Task<string> ReadingLanguageAsync(Host host, long asset)
        {
            using var page = await host.SendAsync(HttpMethod.Get, "/Read/" + asset);
            return System.Text.RegularExpressions.Regex.Match(await page.Content.ReadAsStringAsync(), "data-reading-language=\"([a-z]+)\"").Groups[1].Value;
        }

        private static async Task<Host> CreateHostAsync(string storage, string models, FakeMozilla mozilla)
        {
            // The server's TranslateGemma is the Reader's only translator (#1726): it offers languages only where a model is installed.
            var host = await LibraryTests.CreateHostAsync(storage, razor: true,
                settings: new Dictionary<string, string> { ["Media:TranslationPath"] = models, ["LocalTranslation:Endpoint"] = "http://ollama:11434" },
                services: services =>
                {
                    services.AddHttpClient<MozillaTranslationsClient>().ConfigurePrimaryHttpMessageHandler(() => mozilla);
                    services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => new FakeOllama());
                });
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            return host;
        }

        // An Ollama machine with the approved translation model installed.
        private sealed class FakeOllama : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(request.RequestUri!.AbsolutePath == "/api/tags"
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { models = new[] { new { name = "translategemma:4b", digest = new string('a', 64) } } })) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        // Mozilla's Remote Settings: engines 2.0, 3.0 and 3.1; en-de 1.0, 2.1 and a preview; en-ja 1.0 and a newer one for Android only; de-en 1.0. One file is served altered.
        private sealed class FakeMozilla : HttpMessageHandler
        {
            public List<string> Downloads { get; } = [];

            public static string Hash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

            private static readonly (string Name, string Version, string? FileType, string? From, string? To, string? Filter)[] Models =
            [
                ("model.ende.intgemm.alphas.bin", "1.0", "model", "en", "de", null), ("vocab.ende.spm", "1.0", "vocab", "en", "de", null),
                ("model.ende.intgemm.alphas.bin", "2.1", "model", "en", "de", null), ("lex.50.50.ende.s2t.bin", "2.1", "lex", "en", "de", null), ("vocab.ende.spm", "2.1", "vocab", "en", "de", null),
                ("model.ende.intgemm.alphas.bin", "3.0a1", "model", "en", "de", null), ("vocab.ende.spm", "3.0a1", "vocab", "en", "de", null),
                ("model.deen.intgemm.alphas.bin", "1.0", "model", "de", "en", null), ("lex.50.50.deen.s2t.bin", "1.0", "lex", "de", "en", null), ("vocab.deen.spm", "1.0", "vocab", "de", "en", null),
                ("model.enja.intgemm.alphas.bin", "1.0", "model", "en", "ja", null), ("srcvocab.enja.spm", "1.0", "srcvocab", "en", "ja", null), ("trgvocab.enja.spm", "1.0", "trgvocab", "en", "ja", null),
                ("model.enja.intgemm.alphas.bin", "2.0", "model", "en", "ja", "env.appinfo.OS == 'Android'"), ("vocab.enja.spm", "2.0", "vocab", "en", "ja", "env.appinfo.OS == 'Android'"),
                // Published regional and script language tags must not invalidate the short-code API response.
                ("model.enpt.intgemm.alphas.bin", "1.0", "model", "en", "pt-BR", null), ("vocab.enpt.spm", "1.0", "vocab", "en", "pt-BR", null),
                ("model.enzh.intgemm.alphas.bin", "1.0", "model", "en", "zh-Hans", null), ("vocab.enzh.spm", "1.0", "vocab", "en", "zh-Hans", null)
            ];

            private static string Content(string? fileType, string? from, string? to, string version) =>
                fileType is null ? "engine-" + version : from + to + "-" + version + "-" + fileType;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/translations-wasm/records", StringComparison.Ordinal))
                {
                    return Json(new[] { "2.0", "3.0", "3.1" }.Select(version => Record("bergamot-translator", version, null, null, null, null)));
                }

                if (path.EndsWith("/translations-models/records", StringComparison.Ordinal))
                {
                    return Json(Models.Select(model => Record(model.Name, model.Version, model.FileType, model.From, model.To, model.Filter)));
                }

                // Attachments live under their content's name; the en-de 2.1 lex arrives altered.
                var content = Uri.UnescapeDataString(path.TrimStart('/'));
                Downloads.Add(content);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content == "ende-2.1-lex" ? "ende-2.1-lexX" : content)) });
            }

            private static object Record(string name, string version, string? fileType, string? from, string? to, string? filter)
            {
                var content = Content(fileType, from, to, version);
                return new
                {
                    name, version, fileType, fromLang = from, toLang = to, filter_expression = filter ?? "",
                    attachment = new { hash = Hash(content), size = Encoding.UTF8.GetByteCount(content), location = content }
                };
            }

            private static Task<HttpResponseMessage> Json(IEnumerable<object> records) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data = records }), Encoding.UTF8, "application/json") });
        }
    }
}
