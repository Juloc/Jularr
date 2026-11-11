using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Data.User.Translation.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Reading.V1;
using Jularr.Integrations.Logic.Translation;
using Microsoft.Extensions.DependencyInjection;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    // The library is translated ahead of reading into the languages profiles use, books being read first, into the cache the Reader reads.
    [TestClass]
    public sealed class BookTranslationTests
    {
        [TestMethod]
        public async Task Job_TranslatesTheBookBeingReadFirst_IntoTheProfilesLanguages_AndTheReaderGetsItAtOnce()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434" },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama));
            var (first, second) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var firstDocument = await ReadingTests.OpenAsync(host, first);
            var secondDocument = await ReadingTests.OpenAsync(host, second);
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'en'");

            // Off, there is no translating at all.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                await Assert.ThrowsExactlyAsync<System.Security.SecurityException>(() => Translate(scope));
            }
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");

            // Every profile reads English, the books' own language: nothing to do.
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" LIKE 'en%' ORDER BY \"Locale\" LIMIT 1)");
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsFalse(await Translate(scope));
            }
            Assert.AreEqual(0, ollama.GenerationCount);

            // A profile in German, reading the second book: that book comes first, then the first one.
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" LIKE 'de%' ORDER BY \"Locale\" LIMIT 1)");
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "ProgressPositionTypeId", "Locator", "Progression", "IsCompleted", "UpdatedAt")
                SELECT profile."Id", asset."WorkId", 2, 's0/b0', 0.1, FALSE, now()
                FROM "Profiles" AS profile, "MediaAssets" AS asset WHERE asset."Id" =
                """ + second);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsTrue(await Translate(scope));
            }
            Assert.IsTrue(ollama.GenerationCount > 0);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/translategemma:4b/de/%'"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/translategemma:4b/de/%' AND \"ReadingDocumentId\" = " + secondDocument.DocumentId));

            // Round after round until the library is done; then there is nothing left, and nothing is translated twice.
            var rounds = 0;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                while (await Translate(scope)) Assert.IsTrue(++rounds < 200, "The job never finishes.");
            }
            var generations = ollama.GenerationCount;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsFalse(await Translate(scope));
            }
            Assert.AreEqual(generations, ollama.GenerationCount);
            foreach (var document in new[] { firstDocument, secondDocument })
            {
                var sections = Convert.ToInt64(await host.ScalarAsync("SELECT jsonb_array_length(\"Structure\" -> 'Sections') FROM \"ReadingDocuments\" WHERE \"Id\" = " + document.DocumentId), System.Globalization.CultureInfo.InvariantCulture);
                Assert.AreEqual(sections, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/translategemma:4b/de/%' AND \"ReadingDocumentId\" = " + document.DocumentId));
            }

            // The Reader finds the chapter translated without asking the model.
            using var translated = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + firstDocument.DocumentId + "/sections/0/local-translations/de/ollama:translategemma:4b?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.OK, translated.StatusCode, await translated.Content.ReadAsStringAsync());
            var blocks = JsonSerializer.Deserialize<string[]>((await translated.Content.ReadFromJsonAsync<LocalReadingTranslationV1>(JsonSerializerOptions.Web))!.Blocks)!;
            Assert.IsTrue(blocks.Length > 0 && blocks.Any(block => block.StartsWith("DE ", StringComparison.Ordinal)), string.Join(" | ", blocks));

            // A language chosen for a book is a target too; a book in that language is left alone.
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'fr' WHERE \"Id\" = " + secondDocument.DocumentId);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "ReadingPreferences" ("ProfileId", "WorkId", "Language")
                SELECT profile."Id", asset."WorkId", 'fr' FROM "Profiles" AS profile, "MediaAssets" AS asset WHERE asset."Id" =
                """ + first);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                while (await Translate(scope)) Assert.IsTrue(++rounds < 400, "The job never finishes.");
            }
            Assert.IsTrue(await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/translategemma:4b/fr/%' AND \"ReadingDocumentId\" = " + firstDocument.DocumentId) is > 0L);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/translategemma:4b/fr/%' AND \"ReadingDocumentId\" = " + secondDocument.DocumentId));
        }

        [TestMethod]
        public async Task Job_BooksNobodyReads_AreTranslatedOneAfterTheOther()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434" },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama));
            var (first, second) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var firstDocument = await ReadingTests.OpenAsync(host, first);
            var secondDocument = await ReadingTests.OpenAsync(host, second);
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'en'");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" LIKE 'de%' ORDER BY \"Locale\" LIMIT 1)");

            // The book with the lower id is finished before a single section of the other one is translated.
            var (early, late) = firstDocument.DocumentId < secondDocument.DocumentId ? (firstDocument, secondDocument) : (secondDocument, firstDocument);
            var earlySections = Convert.ToInt64(await host.ScalarAsync("SELECT jsonb_array_length(\"Structure\" -> 'Sections') FROM \"ReadingDocuments\" WHERE \"Id\" = " + early.DocumentId), System.Globalization.CultureInfo.InvariantCulture);
            var rounds = 0;
            await using var scope = host.Services.CreateAsyncScope();
            while (await Translate(scope))
            {
                Assert.IsTrue(++rounds < 200, "The job never finishes.");
                var lateStarted = Convert.ToInt64(await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/%' AND \"ReadingDocumentId\" = " + late.DocumentId), System.Globalization.CultureInfo.InvariantCulture);
                if (lateStarted == 0) continue;
                var earlyDone = Convert.ToInt64(await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/%' AND \"ReadingDocumentId\" = " + early.DocumentId), System.Globalization.CultureInfo.InvariantCulture);
                Assert.AreEqual(earlySections, earlyDone, "The second book started before the first one was done.");
            }
        }

        [TestMethod]
        public async Task Job_WithTwoBooksBeingRead_TranslatesTheMostRecentlyReadFirst()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await CreateHostAsync(storage.Path, ollama);
            var (first, second) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var firstDocument = await ReadingTests.OpenAsync(host, first);
            var secondDocument = await ReadingTests.OpenAsync(host, second);
            await PrepareGermanReadersAsync(host);

            // The book with the higher id was read last, so the order cannot come from the ids.
            var (early, late) = firstDocument.DocumentId < secondDocument.DocumentId ? ((first, firstDocument), (second, secondDocument)) : ((second, secondDocument), (first, firstDocument));
            await ReadAsync(host, early.Item1, "s0/b0", "3 days");
            await ReadAsync(host, late.Item1, "s0/b0", "1 hour");

            await using var scope = host.Services.CreateAsyncScope();
            Assert.IsTrue(await Translate(scope));
            Assert.IsTrue(await TranslatedSectionCountAsync(host, late.Item2.DocumentId) > 0, "The book read last is translated first.");
            Assert.AreEqual(0L, await TranslatedSectionCountAsync(host, early.Item2.DocumentId));

            // The book read earlier follows once the other one is done.
            var rounds = 0;
            while (await TranslatedSectionCountAsync(host, late.Item2.DocumentId) < 2)
            {
                Assert.IsTrue(await Translate(scope));
                Assert.IsTrue(++rounds < 200, "The job never finishes the first book.");
                if (await TranslatedSectionCountAsync(host, late.Item2.DocumentId) < 2) Assert.AreEqual(0L, await TranslatedSectionCountAsync(host, early.Item2.DocumentId));
            }
            while (await Translate(scope)) Assert.IsTrue(++rounds < 400, "The job never finishes.");
            Assert.AreEqual(2L, await TranslatedSectionCountAsync(host, early.Item2.DocumentId));
        }

        [TestMethod]
        public async Task Job_ABookBeingRead_StartsAtTheChapterTheReaderIsIn_ThenWrapsAround()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await CreateHostAsync(storage.Path, ollama);
            var (first, _) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var document = await ReadingTests.OpenAsync(host, first);
            await PrepareGermanReadersAsync(host);

            // Four chapters (the two of the book twice); the reader is in the third one.
            var id = document.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "ReadingResources" ("ReadingDocumentId", "Path", "MediaType", "Bytes")
                SELECT resource."ReadingDocumentId", ':section/' || lpad((substring(resource."Path" FROM 10)::integer + 2)::text, 5, '0'), resource."MediaType", resource."Bytes"
                FROM "ReadingResources" AS resource
                WHERE resource."ReadingDocumentId" = {{id}} AND resource."Path" LIKE ':section/%';
                UPDATE "ReadingDocuments" SET "Structure" = jsonb_set("Structure", '{Sections}', ("Structure" -> 'Sections') || ("Structure" -> 'Sections')) WHERE "Id" = {{id}};
                """.Replace("{{id}}", id));
            await ReadAsync(host, first, "s2/b0", "1 hour");

            // Each chapter is recorded when its translation starts: the third and fourth, then the first and second.
            var started = new List<string>();
            var rounds = 0;
            await using var scope = host.Services.CreateAsyncScope();
            while (await Translate(scope))
            {
                Assert.IsTrue(++rounds < 200, "The job never finishes.");
                var paths = Convert.ToString(await host.ScalarAsync("SELECT coalesce(string_agg(right(\"Path\", 5), ',' ORDER BY \"Path\"), '') FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/translategemma:4b/de/%' AND \"ReadingDocumentId\" = " + id), System.Globalization.CultureInfo.InvariantCulture)!;
                foreach (var section in paths.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (!started.Contains(section)) started.Add(section);
            }
            CollectionAssert.AreEqual(new[] { "00002", "00003", "00000", "00001" }, started);
        }

        [TestMethod]
        public async Task Job_ANewModelVersion_KeepsTheEarlierTextReadableAndUnfinished_UntilItIsReplacedAfterTheMissingSections()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await CreateHostAsync(storage.Path, ollama);
            var (first, second) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var recent = await ReadingTests.OpenAsync(host, first);
            var earlier = await ReadingTests.OpenAsync(host, second);
            await PrepareGermanReadersAsync(host);
            await ReadAsync(host, first, "s0/b0", "1 hour");
            await ReadAsync(host, second, "s0/b0", "3 days");
            var rounds = 0;
            await using var scope = host.Services.CreateAsyncScope();
            while (await Translate(scope)) Assert.IsTrue(++rounds < 200, "The job never finishes.");
            var translated = await ReadSectionAsync(host, recent.DocumentId, 0);
            var generations = ollama.GenerationCount;

            // A new version of the model, and one section of the book read earlier is missing.
            ollama.Digest = new string('b', 64);
            await LocalReadingTranslationIntegrationTests.CheckMachinesAsync(host);
            await host.Fixture.RunSqlAsync("DELETE FROM \"ReadingResources\" WHERE \"Path\" = ':localtranslation/v4/translategemma:4b/de/00001' AND \"ReadingDocumentId\" = " + earlier.DocumentId);

            // The Reader keeps showing the earlier version's text, marked stale, without asking the model.
            var stale = await ReadSectionAsync(host, recent.DocumentId, 0);
            Assert.AreEqual(translated.Blocks, stale.Blocks);
            Assert.IsTrue(stale.Stale.Length > 0 && stale.Stale.All(value => value), stale.Blocks);
            Assert.IsTrue(stale.Models.All(model => model == "ollama:translategemma:4b"));
            Assert.AreEqual(generations, ollama.GenerationCount);

            // The missing section comes first, although the book read last is only stale; finished counts only the new version.
            Assert.IsTrue(await Translate(scope));
            Assert.AreEqual(new string('b', 64), await DigestAsync(host, earlier.DocumentId, "00001"));
            Assert.AreEqual(new string('a', 64), await DigestAsync(host, recent.DocumentId, "00000"));
            var progress = await ReadProgressAsync(host);
            Assert.AreEqual(1, progress.Languages.Single().Done);
            Assert.IsTrue(progress.Languages.Single().Total > 1);

            // Then the stale sections are translated again with the new version, which replaces the earlier one.
            while (await Translate(scope)) Assert.IsTrue(++rounds < 400, "The job never finishes.");
            Assert.IsTrue(ollama.GenerationCount > generations);
            var renewed = await ReadSectionAsync(host, recent.DocumentId, 0);
            Assert.AreEqual(translated.Blocks, renewed.Blocks);
            Assert.IsTrue(renewed.Stale.All(value => !value));
            Assert.AreEqual(0L, await host.ScalarAsync("""
                SELECT count(*) FROM "ReadingResources"
                WHERE "Path" LIKE ':localtranslation/v4/%'
                    AND (convert_from("Bytes", 'UTF8')::jsonb ->> 'ModelDigest' <> repeat('b', 64) OR jsonb_typeof(convert_from("Bytes", 'UTF8')::jsonb -> 'Stale') = 'object')
                """));
            var done = await ReadProgressAsync(host);
            Assert.AreEqual(done.Languages.Single().Total, done.Languages.Single().Done);
        }

        [TestMethod]
        public async Task Job_WithACodexDefaultModel_TranslatesASectionInOneRequest_AndStopsAtTheDailyLimit()
        {
            using var storage = new TemporaryDirectory();
            using var codex = new CodexTranslationTests.Folder();
            codex.SignIn();
            var ollama = new FakeOllama();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434", ["Ai:CodexPath"] = codex.Executable, ["Ai:CodexHome"] = codex.Home },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama));
            var (first, _) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var document = await ReadingTests.OpenAsync(host, first);
            await PrepareGermanReadersAsync(host);
            await ReadAsync(host, first, "s0/b0", "1 hour");
            await using var scope = host.Services.CreateAsyncScope();

            // The account's models are known once it was asked; the Owner's default model names Codex, and 100 tokens a day are allowed (a request costs 150).
            await scope.ServiceProvider.GetRequiredService<CodexConnection>().ReadCatalogAsync(default);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"TranslationModel\" = 'codex:gpt-test', \"CodexDailyTokens\" = 100 WHERE \"Id\" = 1");

            // The section's blocks go to Codex in one request; every block is translated, and the book's other sections wait: the day's tokens are used up.
            Assert.IsTrue(await Translate(scope));
            Assert.AreEqual(0, ollama.GenerationCount);
            Assert.AreEqual(1, File.ReadAllLines(Path.Combine(codex.Home, "asked")).Length, "one request for the section");
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" = ':localtranslation/v4/codex:gpt-test/de/00000' AND \"ReadingDocumentId\" = " + document.DocumentId));
            var texts = await host.ScalarAsync("SELECT convert_from(\"Bytes\", 'UTF8')::jsonb -> 'Texts' FROM \"ReadingResources\" WHERE \"Path\" = ':localtranslation/v4/codex:gpt-test/de/00000' AND \"ReadingDocumentId\" = " + document.DocumentId);
            StringAssert.Contains(Convert.ToString(texts, System.Globalization.CultureInfo.InvariantCulture), "Translated by gpt-test:");
            Assert.AreEqual(150L, scope.ServiceProvider.GetRequiredService<CodexConnection>().Usage().Today);

            // Used up: nothing more is asked, and nothing falls back to Ollama by itself.
            Assert.IsFalse(await Translate(scope));
            Assert.AreEqual(1, File.ReadAllLines(Path.Combine(codex.Home, "asked")).Length);
            Assert.AreEqual(0, ollama.GenerationCount);
        }

        [TestMethod]
        public async Task Retranslate_ASectionWithCodex_ShowsToEveryReader_WhileOllamaStaysTheDefault()
        {
            using var storage = new TemporaryDirectory();
            using var codex = new CodexTranslationTests.Folder();
            codex.SignIn();
            var ollama = new FakeOllama();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434", ["Ai:CodexPath"] = codex.Executable, ["Ai:CodexHome"] = codex.Home },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama));
            var (first, _) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var document = await ReadingTests.OpenAsync(host, first);
            await PrepareGermanReadersAsync(host);
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<CodexConnection>().ReadCatalogAsync(default);
            var url = "/api/v1/reading/documents/" + document.DocumentId + "/sections/0/local-translations/de/codex:gpt-test/retranslate";

            using var done = await host.SendAsync(HttpMethod.Post, url, new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.OK, done.StatusCode, await done.Content.ReadAsStringAsync());
            var result = (await done.Content.ReadFromJsonAsync<LocalReadingRetranslationV1>(JsonSerializerOptions.Web))!;
            Assert.IsTrue(result.Translated > 0 && result.Kept == 0, result.Translated + "/" + result.Kept);
            Assert.AreEqual(1, File.ReadAllLines(Path.Combine(codex.Home, "asked")).Length, "the chapter in one request");
            Assert.AreEqual((long)result.Translated, await host.ScalarAsync("SELECT count(*) FROM \"ReadingTranslationChoices\" WHERE \"ReadingDocumentId\" = " + document.DocumentId + " AND \"Model\" = 'codex:gpt-test'"));

            // Ollama is still the default, and the Reader shows the Codex text for the blocks chosen, each marked with its model.
            var shown = await ReadSectionAsync(host, document.DocumentId, 0);
            Assert.IsTrue(shown.Models.Count(model => model == "codex:gpt-test") == result.Translated, string.Join(",", shown.Models));
            StringAssert.Contains(shown.Blocks, "Translated by gpt-test:");

            // Again: the new texts replace the earlier ones, and nothing is added twice.
            using var again = await host.SendAsync(HttpMethod.Post, url, new LocalReadingBlockDataV1("en"));
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);
            Assert.AreEqual((long)result.Translated, await host.ScalarAsync("SELECT count(*) FROM \"ReadingTranslationChoices\" WHERE \"ReadingDocumentId\" = " + document.DocumentId));

            // A model nobody offers, or a plan limit, changes nothing.
            using var unknown = await host.SendAsync(HttpMethod.Post, url.Replace("codex:gpt-test", "codex:made-up"), new LocalReadingBlockDataV1("en"));
            Assert.AreNotEqual(HttpStatusCode.OK, unknown.StatusCode);
            using var limited = await host.SendAsync(HttpMethod.Post, url.Replace("gpt-test", "broken"), new LocalReadingBlockDataV1("en"));
            Assert.AreNotEqual(HttpStatusCode.OK, limited.StatusCode);
        }

        [TestMethod]
        public async Task Job_ABookInAnUnknownLanguage_IsSkippedEvenWhenItIsBeingRead()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await CreateHostAsync(storage.Path, ollama);
            var (first, second) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var unknown = await ReadingTests.OpenAsync(host, first);
            var known = await ReadingTests.OpenAsync(host, second);
            await PrepareGermanReadersAsync(host);

            // Neither the book nor its Work names a language.
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = NULL WHERE \"Id\" = " + unknown.DocumentId);
            await host.Fixture.RunSqlAsync("UPDATE \"Works\" SET \"OriginalLanguage\" = NULL");
            await ReadAsync(host, first, "s0/b0", "1 hour");

            var rounds = 0;
            await using var scope = host.Services.CreateAsyncScope();
            while (await Translate(scope)) Assert.IsTrue(++rounds < 200, "The job never finishes.");
            Assert.AreEqual(0L, await TranslatedSectionCountAsync(host, unknown.DocumentId));
            Assert.AreEqual(2L, await TranslatedSectionCountAsync(host, known.DocumentId));

            // The same book with a language is translated, so the language was the only reason.
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'en' WHERE \"Id\" = " + unknown.DocumentId);
            while (await Translate(scope)) Assert.IsTrue(++rounds < 400, "The job never finishes.");
            Assert.AreEqual(2L, await TranslatedSectionCountAsync(host, unknown.DocumentId));
        }

        [TestMethod]
        public async Task Job_ABlockWithoutAVerifiableTranslation_KeepsItsOriginalAndIsDone()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama { Unverifiable = true };
            await using var host = await CreateHostAsync(storage.Path, ollama);
            var (first, _) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var document = await ReadingTests.OpenAsync(host, first);
            await PrepareGermanReadersAsync(host);

            // The model answers, but never with a text that passes the checks: the job still ends, with the books' own texts.
            var rounds = 0;
            await using var scope = host.Services.CreateAsyncScope();
            while (await Translate(scope)) Assert.IsTrue(++rounds < 200, "The job loops on a block it cannot translate.");
            Assert.IsTrue(ollama.GenerationCount > 0);
            Assert.AreEqual(2L, await TranslatedSectionCountAsync(host, document.DocumentId));

            var cache = Convert.ToString(await host.ScalarAsync("SELECT convert_from(\"Bytes\", 'UTF8') FROM \"ReadingResources\" WHERE \"ReadingDocumentId\" = " + document.DocumentId + " AND \"Path\" = ':localtranslation/v4/translategemma:4b/de/00001'"), System.Globalization.CultureInfo.InvariantCulture)!;
            using var parsed = JsonDocument.Parse(cache);
            var texts = parsed.RootElement.GetProperty("Texts").EnumerateArray().Select(text => text.GetString()).ToArray();
            CollectionAssert.AreEqual(new[] { "Part Two", "The second part begins.", "And it ends." }, texts);

            // The translated EPUB keeps those paragraphs as they are and says how many did.
            using var epub = await DownloadEpubAsync(host, document.DocumentId);
            StringAssert.Contains(ReadingTests.Entry(epub, "OEBPS/s00001.xhtml"), "The second part begins.");
            var colophon = ReadingTests.Entry(epub, "OEBPS/colophon.xhtml");
            ReadingTests.Xml(colophon);
            StringAssert.Matches(colophon, new System.Text.RegularExpressions.Regex("[1-9][0-9]* paragraphs keep the original text"));
        }

        [TestMethod]
        public async Task TranslatedEpub_OnlyOnceEverySectionIsTranslated_ChosenModelsWinPerBlock_AndNamesTheModels()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await CreateHostAsync(storage.Path, ollama);
            var (first, _) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var document = await ReadingTests.OpenAsync(host, first);
            await PrepareGermanReadersAsync(host);
            var ebook = "/api/v1/reading/documents/" + document.DocumentId + "/ebook?language=de";

            // Part of the book translated: neither offered nor downloadable, and the admin list shows how far it is without a download.
            await using var scope = host.Services.CreateAsyncScope();
            Assert.IsTrue(await Translate(scope));
            Assert.AreEqual(0, (await ReadingTests.OpenAsync(host, first)).TranslatedLanguages.Length);
            using (var partial = await host.SendAsync(HttpMethod.Get, ebook)) Assert.AreEqual(HttpStatusCode.NotFound, partial.StatusCode);
            var started = (await ReadBooksAsync(host)).Items.Single(book => book.DocumentId == document.DocumentId);
            Assert.AreEqual(("de", "ollama:translategemma:4b", false), (started.Language, started.Models.Single(), started.IsComplete()));
            Assert.IsTrue(started.Done[0] < started.Sections, started.Done[0] + "/" + started.Sections);

            // Every section translated: the Reader offers it, it downloads, and the EPUB names its model in the metadata and on its last page.
            var rounds = 0;
            while (await Translate(scope)) Assert.IsTrue(++rounds < 200, "The job never finishes.");
            CollectionAssert.AreEqual(new[] { "de" }, (await ReadingTests.OpenAsync(host, first)).TranslatedLanguages);
            using (var reader = await host.SendAsync(HttpMethod.Get, "/Read/" + first)) StringAssert.Contains(await reader.Content.ReadAsStringAsync(), "href=\"" + ebook + "\"");
            var finished = (await ReadBooksAsync(host)).Items.Single(book => book.DocumentId == document.DocumentId && book.Language == "de");
            Assert.IsTrue(finished.IsComplete() && finished.Done[0] == finished.Sections && finished.Stale[0] == 0, string.Join(",", finished.Done));
            using (var epub = await DownloadEpubAsync(host, document.DocumentId))
            {
                var package = ReadingTests.Entry(epub, "OEBPS/content.opf");
                StringAssert.Contains(package, "<dc:language>de</dc:language>");
                StringAssert.Contains(package, "<dc:contributor id=\"model0\">ollama:translategemma:4b</dc:contributor>");
                StringAssert.Contains(package, "href=\"colophon.xhtml\"");
                var section = ReadingTests.Entry(epub, "OEBPS/s00000.xhtml");
                ReadingTests.Xml(section);
                StringAssert.Contains(section, ">DE ");
                var colophon = ReadingTests.Entry(epub, "OEBPS/colophon.xhtml");
                StringAssert.Contains(colophon, "ollama:translategemma:4b");
                StringAssert.Contains(colophon, "Every paragraph is translated.");
                StringAssert.Contains(ReadingTests.Entry(epub, "OEBPS/nav.xhtml"), "About this translation");
            }

            // A block re-translated with another model shows that model's text in the EPUB too; both models are named.
            await host.Fixture.RunSqlAsync($$"""
                INSERT INTO "ReadingResources" ("ReadingDocumentId", "Path", "MediaType", "Bytes")
                SELECT resource."ReadingDocumentId", ':localtranslation/v4/translategemma:12b/de/00000', resource."MediaType",
                    convert_to(replace(convert_from(resource."Bytes", 'UTF8'), '"DE ', '"ZWOELF '), 'UTF8')
                FROM "ReadingResources" AS resource
                WHERE resource."ReadingDocumentId" = {{document.DocumentId}} AND resource."Path" = ':localtranslation/v4/translategemma:4b/de/00000';
                INSERT INTO "ReadingTranslationChoices" ("ReadingDocumentId", "SectionIndex", "Language", "BlockIndex", "Model")
                VALUES ({{document.DocumentId}}, 0, 'de', 0, 'ollama:translategemma:12b');
                """);
            using (var mixed = await DownloadEpubAsync(host, document.DocumentId))
            {
                var section = ReadingTests.Entry(mixed, "OEBPS/s00000.xhtml");
                Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(section, ">ZWOELF ").Count, section);
                StringAssert.Contains(section, ">DE ");
                var package = ReadingTests.Entry(mixed, "OEBPS/content.opf");
                StringAssert.Contains(package, "ollama:translategemma:4b</dc:contributor>");
                StringAssert.Contains(package, "ollama:translategemma:12b</dc:contributor>");
            }

            // A new version of the model: the earlier texts still make the EPUB, and the list counts them as stale.
            ollama.Digest = new string('c', 64);
            await LocalReadingTranslationIntegrationTests.CheckMachinesAsync(host);
            Assert.IsTrue(await Translate(scope));
            var renewing = (await ReadBooksAsync(host)).Items.Single(book => book.DocumentId == document.DocumentId && book.Language == "de");
            Assert.IsTrue(renewing.Stale[Array.IndexOf(renewing.Models, "ollama:translategemma:4b")] > 0, string.Join(",", renewing.Stale));
            using (var stale = await host.SendAsync(HttpMethod.Get, ebook)) Assert.AreEqual(HttpStatusCode.OK, stale.StatusCode);

            // Whoever reads the book downloads it; a profile that cannot see books cannot, and the list is for administrators only.
            await host.Fixture.RunSqlAsync("""UPDATE "Accounts" SET "AccountRoleTypeId" = 2 WHERE "Id" = 41;""");
            using (var member = await host.SendAsync(HttpMethod.Get, ebook)) Assert.AreEqual(HttpStatusCode.OK, member.StatusCode, await member.Content.ReadAsStringAsync());
            using (var list = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/translation/books")) Assert.IsTrue(list.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, list.StatusCode.ToString());
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "ProfileMediaRules" ("ProfileId", "MediaTypeId", "IsBlocked") SELECT profile."Id", type."Id", TRUE FROM "Profiles" AS profile CROSS JOIN "MediaTypes" AS type;
                INSERT INTO "ProfileMediaRules" ("ProfileId", "MediaSubtypeId", "IsBlocked") SELECT profile."Id", subtype."Id", TRUE FROM "Profiles" AS profile CROSS JOIN "MediaSubtypes" AS subtype;
                """);
            using (var hidden = await host.SendAsync(HttpMethod.Get, ebook)) Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode);
        }

        [TestMethod]
        public async Task TranslatedEpub_TwoModelsThatTogetherCoverTheBook_AreOffered_AndBothNamed()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await CreateHostAsync(storage.Path, ollama);
            var (first, _) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var document = await ReadingTests.OpenAsync(host, first);
            await PrepareGermanReadersAsync(host);
            await using var scope = host.Services.CreateAsyncScope();
            var rounds = 0;
            while (await Translate(scope)) Assert.IsTrue(++rounds < 200, "The job never finishes.");

            // The small model keeps the first section only; the second is the larger model's, chosen for every block of it.
            await host.Fixture.RunSqlAsync($$"""
                UPDATE "ReadingResources" SET "Path" = ':localtranslation/v4/translategemma:12b/de/00001',
                    "Bytes" = convert_to(replace(convert_from("Bytes", 'UTF8'), '"DE ', '"ZWOELF '), 'UTF8')
                WHERE "ReadingDocumentId" = {{document.DocumentId}} AND "Path" = ':localtranslation/v4/translategemma:4b/de/00001';
                INSERT INTO "ReadingTranslationChoices" ("ReadingDocumentId", "SectionIndex", "Language", "BlockIndex", "Model")
                SELECT {{document.DocumentId}}, 1, 'de', block.position - 1, 'ollama:translategemma:12b'
                FROM "ReadingResources" AS resource
                    CROSS JOIN LATERAL jsonb_array_elements(convert_from(resource."Bytes", 'UTF8')::jsonb -> 'Texts') WITH ORDINALITY AS block (value, position)
                WHERE resource."ReadingDocumentId" = {{document.DocumentId}} AND resource."Path" = ':localtranslation/v4/translategemma:12b/de/00001';
                """);

            // No single model covers the book, yet every block has its translation: offered, listed complete and downloadable, naming both models.
            CollectionAssert.AreEqual(new[] { "de" }, (await ReadingTests.OpenAsync(host, first)).TranslatedLanguages);
            var listed = (await ReadBooksAsync(host)).Items.Single(book => book.DocumentId == document.DocumentId && book.Language == "de");
            Assert.IsTrue(listed.IsComplete() && listed.Translated == listed.Sections && listed.Done.All(done => done < listed.Sections), string.Join(",", listed.Done));
            CollectionAssert.AreEquivalent(new[] { "ollama:translategemma:4b", "ollama:translategemma:12b" }, listed.Models);
            using var epub = await DownloadEpubAsync(host, document.DocumentId);
            StringAssert.Contains(ReadingTests.Entry(epub, "OEBPS/s00000.xhtml"), ">DE ");
            StringAssert.Contains(ReadingTests.Entry(epub, "OEBPS/s00001.xhtml"), ">ZWOELF ");
            var package = ReadingTests.Entry(epub, "OEBPS/content.opf");
            StringAssert.Contains(package, "ollama:translategemma:4b</dc:contributor>");
            StringAssert.Contains(package, "ollama:translategemma:12b</dc:contributor>");

            // A block with no server text at all keeps the book incomplete.
            await host.Fixture.RunSqlAsync("DELETE FROM \"ReadingResources\" WHERE \"Path\" = ':localtranslation/v4/translategemma:12b/de/00001'");
            Assert.AreEqual(0, (await ReadingTests.OpenAsync(host, first)).TranslatedLanguages.Length);
            using var incomplete = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + document.DocumentId + "/ebook?language=de");
            Assert.AreEqual(HttpStatusCode.NotFound, incomplete.StatusCode);
        }

        [TestMethod]
        public async Task Job_ASectionWithoutText_IsDoneOnceAndTheRoundsMoveOn()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434" },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama));
            var (first, _) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var document = await ReadingTests.OpenAsync(host, first);
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'en'");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" LIKE 'de%' ORDER BY \"Locale\" LIMIT 1)");

            // The first section becomes a page of one picture: no text block at all.
            var id = document.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingResources\" SET \"Bytes\" = convert_to('<p><img src=\"a.png\"/></p>', 'UTF8') WHERE \"ReadingDocumentId\" = " + id + " AND \"Path\" = ':section/00000'");
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Structure\" = jsonb_set(\"Structure\", '{Sections,0,Blocks}', '0') WHERE \"Id\" = " + id);

            var rounds = 0;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                while (await Translate(scope)) Assert.IsTrue(++rounds < 200, "The job keeps picking the section without text.");
            }
            var sections = Convert.ToInt64(await host.ScalarAsync("SELECT jsonb_array_length(\"Structure\" -> 'Sections') FROM \"ReadingDocuments\" WHERE \"Id\" = " + document.DocumentId), System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(sections, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/translategemma:4b/de/%' AND \"ReadingDocumentId\" = " + document.DocumentId));
        }

        [TestMethod]
        public async Task Progress_CountsFinishedSectionsPerLanguageAndBook_AndNamesTheSectionInWork()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434" },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama));
            var (first, second) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var firstDocument = await ReadingTests.OpenAsync(host, first);
            await ReadingTests.OpenAsync(host, second);
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'en'");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" LIKE 'de%' ORDER BY \"Locale\" LIMIT 1)");

            // Before the job's first round the books are still to be counted, and the page says so.
            Assert.IsTrue((await ReadProgressAsync(host)).Counting);
            using (var counting = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Translation?handler=Progress"))
                StringAssert.Contains(await counting.Content.ReadAsStringAsync(), "Being counted");

            // The first round counts them; without a machine for the default model it translates nothing: every section of both books waits, nothing is in work,
            // nothing can be estimated.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"TranslationModel\" = 'ollama:translategemma:27b' WHERE \"Id\" = 1");
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsFalse(await Translate(scope));
            }
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"TranslationModel\" = NULL WHERE \"Id\" = 1");
            var before = await ReadProgressAsync(host);
            Assert.IsFalse(before.Counting);
            Assert.AreEqual(1, before.Languages.Length);
            Assert.AreEqual("de", before.Languages[0].Language);
            Assert.AreEqual(0, before.Languages[0].Done);
            Assert.IsTrue(before.Languages[0].Total > 0);
            Assert.AreEqual(2, before.Books.Length);
            Assert.IsNull(before.Current);
            Assert.IsNull(before.SecondsRemaining);

            // The time left is the blocks still to do over the blocks per minute, not the sections: with 2 blocks a minute it is half the blocks in minutes.
            var tracker = host.Services.GetRequiredService<Jularr.Backend.Service.Core.BookTranslationProgress>();
            for (var block = 0; block < 20; block++) tracker.BlockDone();
            var totalBlocks = Convert.ToInt64(await host.ScalarAsync("SELECT sum((section.value ->> 'Blocks')::integer) FROM \"ReadingDocuments\" AS document, jsonb_array_elements(document.\"Structure\" -> 'Sections') AS section (value)"), System.Globalization.CultureInfo.InvariantCulture);
            var estimated = await ReadProgressAsync(host);
            Assert.AreEqual(2.0, estimated.BlocksPerMinute);
            Assert.AreEqual((long)Math.Round(totalBlocks / 2.0 * 60), estimated.SecondsRemaining);

            // One round: the section it picked is named, and once finished it counts before the next one is.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsTrue(await Translate(scope));
            }
            var during = await ReadProgressAsync(host);
            Assert.IsNotNull(during.Current);
            Assert.AreEqual(1, during.Languages[0].Done);
            Assert.IsTrue(during.Current.Title is "First Book" or "Second Book");
            Assert.IsTrue(during.Current.Section >= 1);
            Assert.AreEqual("de", during.Current.Language);
            // The time left counts the blocks of the sections still to do: a finished section's blocks are its own, not another's.
            var doneBlocks = Convert.ToInt64(await host.ScalarAsync("SELECT coalesce(sum((document.\"Structure\" -> 'Sections' -> substring(cache.\"Path\" FROM '[0-9]{5}$')::integer ->> 'Blocks')::integer), 0) "
                + "FROM \"ReadingResources\" AS cache JOIN \"ReadingDocuments\" AS document ON document.\"Id\" = cache.\"ReadingDocumentId\" WHERE cache.\"Path\" ~ '^:localtranslation/v4/[^/]+/de/[0-9]{5}$'"),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.IsTrue(doneBlocks > 0);
            Assert.AreEqual((long)Math.Round((totalBlocks - doneBlocks) / during.BlocksPerMinute * 60), during.SecondsRemaining);

            // A book that is one volume of a series is listed with its number.
            await host.Fixture.RunSqlAsync("INSERT INTO \"WorkUnits\" (\"WorkId\", \"WorkUnitTypeId\", \"Number\") SELECT asset.\"WorkId\", 3, 14 FROM \"MediaAssets\" AS asset WHERE asset.\"Id\" = " + first);
            await host.Fixture.RunSqlAsync("UPDATE \"MediaAssets\" SET \"WorkUnitId\" = (SELECT unit.\"Id\" FROM \"WorkUnits\" AS unit WHERE unit.\"WorkId\" = \"MediaAssets\".\"WorkId\" AND unit.\"Number\" = 14) WHERE \"Id\" = " + first);
            var labelled = await ReadProgressAsync(host);
            Assert.AreEqual("14", labelled.Books.Single(book => book.Title == "First Book").Volume);
            Assert.IsNull(labelled.Books.Single(book => book.Title == "Second Book").Volume);

            var rounds = 0;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                while (await Translate(scope)) Assert.IsTrue(++rounds < 200, "The job never finishes.");
            }
            var done = await ReadProgressAsync(host);
            Assert.AreEqual(done.Languages[0].Total, done.Languages[0].Done);
            Assert.IsTrue(done.Books.All(book => book.Done == book.Total && book.Total > 0));
            Assert.IsNull(done.Current);
            Assert.IsNull(done.SecondsRemaining);
            Assert.IsNull(done.IssueKey);
            Assert.IsTrue(firstDocument.DocumentId > 0);

            // The page shows the part, and reading only that part answers the fragment the page swaps in; the Jobs page loads.
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Translation");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "data-translation-progress");
            StringAssert.Contains(html, "First Book");
            using var fragment = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Translation?handler=Progress");
            var part = await fragment.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, fragment.StatusCode, part);
            Assert.IsFalse(part.Contains("Being counted", StringComparison.Ordinal));
            Assert.IsTrue(part.TrimStart().StartsWith("<div", StringComparison.Ordinal) && part.Contains("data-translation-progress", StringComparison.Ordinal), part);
            // The page has two tabs: the progress (first) and the setup, each only with its own part.
            Assert.IsFalse(html.Contains("data-translation-machine", StringComparison.Ordinal), "The progress tab holds no machine cards.");
            using var setup = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Translation?tab=setup");
            var setupHtml = await setup.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, setup.StatusCode, setupHtml);
            Assert.IsFalse(setupHtml.Contains("data-translation-progress", StringComparison.Ordinal), "The setup tab holds no progress.");
            StringAssert.Contains(setupHtml, "data-translation-machine");
            StringAssert.Contains(setupHtml, "?handler=Machines");
            Assert.IsFalse(setupHtml.Contains("LocalTranslation__", StringComparison.Ordinal), "No variable name in the text.");

            // The Jobs page still loads with the translation module on (its drawer needs the job row, which a started worker creates).
            using var jobs = await host.SendAsync(HttpMethod.Get, "/Admin/Jobs");
            Assert.AreEqual(HttpStatusCode.OK, jobs.StatusCode, await jobs.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task Progress_KeptByTheJobTheReaderANewVersionAndADeletion_EqualsAFullCount()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await CreateHostAsync(storage.Path, ollama);
            var (first, second) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var firstDocument = await ReadingTests.OpenAsync(host, first);
            var secondDocument = await ReadingTests.OpenAsync(host, second);
            await PrepareGermanReadersAsync(host);
            await ReadAsync(host, first, "s0/b0", "1 hour");

            // The job counts the books and translates a few sections; the Reader translates a block of the other book; a new model version translates a section.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                for (var round = 0; round < 3; round++) Assert.IsTrue(await Translate(scope));
            }
            using (var block = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/documents/" + secondDocument.DocumentId + "/sections/1/local-translations/de/ollama:translategemma:4b/blocks/0",
                new LocalReadingBlockDataV1("en")))
                Assert.AreEqual(HttpStatusCode.OK, block.StatusCode, await block.Content.ReadAsStringAsync());
            ollama.Digest = new string('b', 64);
            await LocalReadingTranslationIntegrationTests.CheckMachinesAsync(host);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsTrue(await Translate(scope));
            }
            var kept = await ProgressRowsAsync(host);
            var shown = JsonSerializer.Serialize(await ReadProgressAsync(host));
            StringAssert.Contains(kept, new string('b', 64));

            // Counting every book again from its caches gives the same figures.
            await RecountAllAsync(host);
            Assert.AreEqual(kept, await ProgressRowsAsync(host));
            Assert.AreEqual(shown, JsonSerializer.Serialize(await ReadProgressAsync(host)));

            // The progress, its page and the Jobs page read the kept counts (the median of a few warm reads).
            foreach (var path in new[] { "/api/v1/admin/translation/progress", "/Admin/Instance/Translation?handler=Progress", "/Admin/Instance/Translation", "/Admin/Jobs" })
            {
                using (var warm = await host.SendAsync(HttpMethod.Get, path)) Assert.AreEqual(HttpStatusCode.OK, warm.StatusCode, path + ": " + await warm.Content.ReadAsStringAsync());
                var times = new List<TimeSpan>();
                for (var read = 0; read < 9; read++)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    using var response = await host.SendAsync(HttpMethod.Get, path);
                    times.Add(watch.Elapsed);
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
                }
                var median = times.Order().ElementAt(times.Count / 2);
                Console.WriteLine(path + ": " + median.TotalMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms");
                Assert.IsTrue(median < TimeSpan.FromSeconds(1), path + " took " + median);
            }

            // Deleting a book's translation counts it again at once.
            using (var deleted = await host.SendAsync(HttpMethod.Delete, "/api/v1/admin/library/documents/" + firstDocument.DocumentId + "/translations/de", new DeleteTranslationDataV1(true)))
                Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT coalesce(sum(\"DoneSections\"), 0) FROM \"BookTranslationProgress\" WHERE \"ReadingDocumentId\" = " + firstDocument.DocumentId));
            Assert.IsTrue(await host.ScalarAsync("SELECT \"Sections\" FROM \"BookTranslationProgress\" WHERE \"ModelKey\" = '' AND \"ReadingDocumentId\" = " + firstDocument.DocumentId) is > 0);
        }

        [TestMethod]
        public async Task Progress_ABookLeavingTheCacheOrClearedWaitsForTheJobsCount_AndIsCountedAgainNotDropped()
        {
            using var storage = new TemporaryDirectory();
            var ollama = new FakeOllama();
            await using var host = await CreateHostAsync(storage.Path, ollama);
            var (first, second) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var book = (await ReadingTests.OpenAsync(host, first)).DocumentId;
            await PrepareGermanReadersAsync(host);
            await ReadAsync(host, first, "s0/b0", "1 hour");
            var doneSql = "SELECT coalesce(sum(\"DoneSections\"), 0) FROM \"BookTranslationProgress\" WHERE \"ModelKey\" = '' AND \"ReadingDocumentId\" = " + book;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                // The job counts the book and translates it until a section of it is finished.
                for (var round = 0; await host.ScalarAsync(doneSql) is not > 0L; round++)
                {
                    Assert.IsTrue(round < 20 && await Translate(scope), "The job finished no section of the book.");
                }
            }

            // The first book leaves the cache when the second is prepared (its file changed, the budget is spent) while the job's count holds the first book's
            // lock and rows: the preparation waits for that lock, never for the rows in a circle.
            await host.Fixture.RunSqlAsync($"""
                UPDATE "ReadingDocuments" SET "LastReadAt" = now() - interval '30 days' WHERE "Id" = {book};
                UPDATE "StoredFiles" SET "SizeBytes" = "SizeBytes" + 1 WHERE "Id" = (SELECT "StoredFileId" FROM "ReadingDocuments" WHERE "Id" = {book});
                UPDATE "InstanceSettings" SET "ReadingCacheBytes" = 0;
                """);
            await using (var count = await HoldCountAsync(host, book))
            {
                var preparing = ReadingTests.OpenAsync(host, second);
                await LocalReadingTranslationIntegrationTests.UntilWaitingAsync(host);
                Assert.IsFalse(preparing.IsCompleted);
                await count.CommitAsync();
                await preparing.WaitAsync(TimeSpan.FromSeconds(30));
            }

            // It is counted again with nothing translated, not dropped: what a full count gives.
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"ReadingDocumentId\" = " + book));
            Assert.AreEqual(0L, await host.ScalarAsync(doneSql));
            var evicted = await ProgressRowsAsync(host);
            StringAssert.Contains(evicted, book + "|de|||");
            await RecountAllAsync(host);
            Assert.AreEqual(evicted, await ProgressRowsAsync(host));

            // Clearing the book, as a preparation starting over does, waits for the count's lock the same way and counts it again.
            await using (var count = await HoldCountAsync(host, book))
            {
                var clearing = Task.Run(async () =>
                {
                    await using var sql = new SqlContext(host.Fixture.Database);
                    await sql.BeginAsync(SqlAccessMode.ReadWrite);
                    var ctx = new LogicContext(sql);
                    await ctx.ExecuteAsyncForAffectedRows(new SqlStatement { Statement = "SET LOCAL lock_timeout = '10s';" });
                    await ctx.ClearReadingResourcesAsync(new ReadingDocumentIdData(book), default);
                    await sql.CommitAsync();
                });
                await LocalReadingTranslationIntegrationTests.UntilWaitingAsync(host);
                Assert.IsFalse(clearing.IsCompleted);
                await count.CommitAsync();
                await clearing.WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.AreEqual(evicted, await ProgressRowsAsync(host));
        }

        // The job's count of a book while it runs: the book's count lock and every kept row of the book, held in an open transaction.
        private static async Task<SqlContext> HoldCountAsync(AuthenticationTests.Host host, long documentId)
        {
            var sql = new SqlContext(host.Fixture.Database);
            await sql.BeginAsync(SqlAccessMode.ReadWrite);
            await new LogicContext(sql).ExecuteAsyncForAffectedRows(new SqlStatement
            {
                Statement = $"""
                    SELECT pg_advisory_xact_lock(hashtextextended('book-translation-progress:' || {documentId}::text, 0));
                    UPDATE "BookTranslationProgress" SET "UpdatedAt" = now() WHERE "ReadingDocumentId" = {documentId};
                    """
            });
            return sql;
        }

        // The job's full count of every book, at once.
        private static async Task RecountAllAsync(AuthenticationTests.Host host)
        {
            await using var sql = new SqlContext(host.Fixture.Database);
            await sql.BeginAsync(SqlAccessMode.ReadWrite);
            Assert.IsNull(await new LogicContext(sql).RecountBookTranslationBatchAsync(new BookTranslationBatchData(1, 1000), default));
            await sql.CommitAsync();
        }

        // Every kept count, in a fixed order.
        private static async Task<string> ProgressRowsAsync(AuthenticationTests.Host host) =>
            Convert.ToString(await host.ScalarAsync("""
                SELECT string_agg(concat_ws('|', "ReadingDocumentId", "Language", "ModelKey", "Digest", "Sections", "DoneSections", "Blocks", "DoneBlocks"), ';'
                    ORDER BY "ReadingDocumentId", "Language", "ModelKey", "Digest")
                FROM "BookTranslationProgress"
                """), System.Globalization.CultureInfo.InvariantCulture)!;

        [TestMethod]
        public void Progress_TracksTheSectionPaceAndWhyNothingHappens()
        {
            var clock = new Clock();
            var progress = new Jularr.Backend.Service.Core.BookTranslationProgress(clock);
            Assert.IsNull(progress.Snapshot().Current);

            progress.Picked(7, 3, "de");
            clock.Now += TimeSpan.FromSeconds(90);
            var waiting = progress.Snapshot();
            Assert.AreEqual(3, waiting.Current!.Section);
            Assert.AreEqual(90, waiting.SecondsWithoutProgress);

            // A block resets the time without progress; blocks count towards the pace of the last ten minutes.
            progress.BlockDone();
            for (var block = 0; block < 9; block++) progress.BlockDone();
            var moving = progress.Snapshot();
            Assert.AreEqual(0, moving.SecondsWithoutProgress);
            Assert.AreEqual(1.0, moving.BlocksPerMinute);

            clock.Now += TimeSpan.FromMinutes(11);
            Assert.AreEqual(0.0, progress.Snapshot().BlocksPerMinute);

            progress.Idle("noMachine");
            var idle = progress.Snapshot();
            Assert.IsNull(idle.Current);
            Assert.AreEqual("noMachine", idle.IssueKey);
        }

        private static Task<AuthenticationTests.Host> CreateHostAsync(string storage, FakeOllama ollama) =>
            LibraryTests.CreateHostAsync(storage, razor: true,
                settings: new Dictionary<string, string> { ["LocalTranslation:Endpoint"] = "http://ollama:11434" },
                services: services => services.AddHttpClient<OllamaLocalModels>().ConfigurePrimaryHttpMessageHandler(() => ollama));

        // The books are English, Translation is on and every profile reads German.
        private static async Task PrepareGermanReadersAsync(AuthenticationTests.Host host)
        {
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"Language\" = 'en'");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Translation'");
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" LIKE 'de%' ORDER BY \"Locale\" LIMIT 1)");
            await LocalReadingTranslationIntegrationTests.CheckMachinesAsync(host);
        }

        // Every profile is reading the book at the locator, last read that long ago.
        private static Task ReadAsync(AuthenticationTests.Host host, long asset, string locator, string ago) =>
            host.Fixture.RunSqlAsync($"""
                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "ProgressPositionTypeId", "Locator", "Progression", "IsCompleted", "UpdatedAt")
                SELECT profile."Id", asset."WorkId", 2, '{locator}', 0.1, FALSE, now() - interval '{ago}'
                FROM "Profiles" AS profile, "MediaAssets" AS asset WHERE asset."Id" = {asset}
                """);

        private static async Task<long> TranslatedSectionCountAsync(AuthenticationTests.Host host, long documentId) =>
            Convert.ToInt64(await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"Path\" LIKE ':localtranslation/v4/translategemma:4b/de/%' AND \"ReadingDocumentId\" = " + documentId), System.Globalization.CultureInfo.InvariantCulture);

        private static async Task<LocalReadingTranslationV1> ReadSectionAsync(AuthenticationTests.Host host, long documentId, int section)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + documentId + "/sections/" + section + "/local-translations/de/ollama:translategemma:4b?sourceLanguage=en");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<LocalReadingTranslationV1>(JsonSerializerOptions.Web))!;
        }

        private static async Task<string> DigestAsync(AuthenticationTests.Host host, long documentId, string section) =>
            Convert.ToString(await host.ScalarAsync("SELECT convert_from(\"Bytes\", 'UTF8')::jsonb ->> 'ModelDigest' FROM \"ReadingResources\" WHERE \"ReadingDocumentId\" = " + documentId
                + " AND \"Path\" = ':localtranslation/v4/translategemma:4b/de/" + section + "'"), System.Globalization.CultureInfo.InvariantCulture)!;

        private static async Task<System.IO.Compression.ZipArchive> DownloadEpubAsync(AuthenticationTests.Host host, long documentId)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + documentId + "/ebook?language=de");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return new System.IO.Compression.ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        }

        private static async Task<PageResult<TranslatedBookV1>> ReadBooksAsync(AuthenticationTests.Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/translation/books");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<PageResult<TranslatedBookV1>>(JsonSerializerOptions.Web))!;
        }

        private static async Task<BookTranslationProgressV1> ReadProgressAsync(AuthenticationTests.Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/translation/progress");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<BookTranslationProgressV1>(JsonSerializerOptions.Web))!;
        }

        private sealed class Clock : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Now;
        }

        private static async Task<bool> Translate(AsyncServiceScope scope) =>
            (await scope.ServiceProvider.GetRequiredService<TranslateReadingBooksV1>().ExecuteAsync<UpdateResult>(new BookTranslationParametersV1(), default, CancellationToken.None)).Success;

        private sealed class FakeOllama : HttpMessageHandler
        {
            public int GenerationCount { get; private set; }

            // The version of the model; a new one makes every translation of the earlier one stale.
            public string Digest { get; set; } = new('a', 64);

            // Answers with a text no translation check accepts.
            public bool Unverifiable { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body;
                if (request.RequestUri!.AbsolutePath == "/api/tags")
                    body = JsonSerializer.Serialize(new { models = new[] { new { name = "translategemma:4b", digest = Digest } } });
                else if (request.RequestUri.AbsolutePath == "/api/ps")
                    body = """{"models":[]}""";
                else if (request.RequestUri.AbsolutePath == "/api/generate")
                {
                    using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    var prompt = input.RootElement.GetProperty("prompt").GetString()!;
                    GenerationCount++;
                    var language = prompt.Contains("into French", StringComparison.Ordinal) ? "FR" : "DE";
                    var answer = Unverifiable ? new string('x', 19_000) : language + " " + prompt[(prompt.LastIndexOf('\n') + 1)..];
                    body = JsonSerializer.Serialize(new { response = answer, eval_count = 2, eval_duration = 200000000 });
                }
                else throw new InvalidOperationException("Unexpected Ollama API: " + request.RequestUri.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
        }
    }
}
