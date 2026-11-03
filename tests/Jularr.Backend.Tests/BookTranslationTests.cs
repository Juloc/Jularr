using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Data.User.Translation.V1;
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
            var blocks = JsonSerializer.Deserialize<string[]>((await translated.Content.ReadFromJsonAsync<ReadingTranslationV1>())!.Blocks)!;
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

            // Before the first round: every section of both books waits, nothing is in work, nothing can be estimated.
            var before = await ReadProgressAsync(host);
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

            // One round: the section it picked is named.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsTrue(await Translate(scope));
            }
            var during = await ReadProgressAsync(host);
            Assert.IsNotNull(during.Current);
            Assert.IsTrue(during.Current.Title is "First Book" or "Second Book");
            Assert.IsTrue(during.Current.Section >= 1);
            Assert.AreEqual("de", during.Current.Language);

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
