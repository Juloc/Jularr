using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.AniList;
using Jularr.Integrations.Logic.OpenLibrary;
using Jularr.Integrations.Logic.Tmdb;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class MediaImportTests
    {
        private const string Preview = "/api/v1/admin/media-import/preview";
        private const string Run = "/api/v1/admin/media-import";
        private const string Token = "eyJ-import-token";

        // A list is one object of version and items; whatever else it holds, or however it is too big, rejects it as a whole.
        [TestMethod]
        [DataRow("{", "import_invalid_json")]
        [DataRow("""{"version":1,"items":[{"type":"movie","title":"A"},]}""", "import_invalid_json")]
        [DataRow("""{"version":1,/* x */"items":[{"type":"movie","title":"A"}]}""", "import_invalid_json")]
        [DataRow("[]", "import_invalid_structure")]
        [DataRow("""{"version":1,"items":{}}""", "import_invalid_structure")]
        [DataRow("""{"version":1}""", "import_invalid_structure")]
        [DataRow("""{"items":[{"type":"movie","title":"A"}]}""", "import_unsupported_version")]
        [DataRow("""{"version":2,"items":[{"type":"movie","title":"A"}]}""", "import_unsupported_version")]
        [DataRow("""{"version":"1","items":[{"type":"movie","title":"A"}]}""", "import_unsupported_version")]
        [DataRow("""{"version":1,"items":[{"type":"movie","title":"A"}],"extra":true}""", "import_unknown_key")]
        [DataRow("""{"version":1,"version":1,"items":[{"type":"movie","title":"A"}]}""", "import_unknown_key")]
        [DataRow("""{"version":1,"items":[]}""", "import_empty")]
        public void Parse_FaultyDocument_IsRejectedAsAWhole(string json, string code)
        {
            var failure = Assert.ThrowsExactly<DataValidationException>(() => MediaImportParser.Parse(json));
            Assert.AreEqual(code, failure.Code);
        }

        [TestMethod]
        public void Parse_TooManyItemsOrTooManyBytes_IsRejectedWithoutReadingFurther()
        {
            var many = "{\"version\":1,\"items\":[" + string.Join(',', Enumerable.Repeat("{\"type\":\"movie\",\"title\":\"A\"}", 2001)) + "]}";
            Assert.AreEqual("import_too_many_items", Assert.ThrowsExactly<DataValidationException>(() => MediaImportParser.Parse(many)).Code);
            Assert.AreEqual(2000, MediaImportParser.Parse("{\"version\":1,\"items\":[" + string.Join(',', Enumerable.Repeat("{\"type\":\"movie\",\"title\":\"A\"}", 2000)) + "]}").Count);

            var large = "{\"version\":1,\"items\":[{\"type\":\"movie\",\"title\":\"" + new string('a', 1_048_576) + "\"}]}";
            Assert.AreEqual("import_too_large", Assert.ThrowsExactly<DataValidationException>(() => MediaImportParser.Parse(large)).Code);

            // Bytes count, not characters: a quarter of a million four-byte characters fill a mebibyte.
            var wide = "{\"version\":1,\"items\":[{\"type\":\"movie\",\"title\":\"" + string.Concat(Enumerable.Repeat("\U0001F600", 262_144)) + "\"}]}";
            Assert.AreEqual("import_too_large", Assert.ThrowsExactly<DataValidationException>(() => MediaImportParser.Parse(wide)).Code);
        }

        // An entry is normalized the same way every time, or marked invalid on its own without touching its neighbours.
        [TestMethod]
        [DataRow("""{"type":"movie","bogus":1,"title":"Bad"}""", "unknown_key", "bogus", "Bad")]
        [DataRow("""{"type":"film","title":"A"}""", "unknown_type", "film", "A")]
        [DataRow("""{"title":"A"}""", "missing_type", null, "A")]
        [DataRow("""{"type":"movie"}""", "no_identity", null, null)]
        [DataRow("""{"type":"movie","ids":{"tmdb":"abc"}}""", "invalid_id", "tmdb", null)]
        [DataRow("""{"type":"movie","ids":{"tmdb":"0"}}""", "invalid_id", "tmdb", null)]
        [DataRow("""{"type":"movie","ids":{"imdb":"tt12"}}""", "invalid_id", "imdb", null)]
        [DataRow("""{"type":"movie","ids":{"tvdb":"81189"}}""", "id_not_for_type", "tvdb", null)]
        [DataRow("""{"type":"book","ids":{"tmdb":"603"}}""", "id_not_for_type", "tmdb", null)]
        [DataRow("""{"type":"book","ids":{"anilist":"30002"}}""", "id_not_for_type", "anilist", null)]
        [DataRow("""{"type":"book","ids":{"isbn":"9780141439519"}}""", "invalid_id", "isbn", null)]
        [DataRow("""{"type":"book","ids":{"openlibrary":"OL66554"}}""", "invalid_id", "openlibrary", null)]
        [DataRow("""{"type":"music","ids":{"musicbrainz":"not-a-guid"}}""", "invalid_id", "musicbrainz", null)]
        [DataRow("""{"type":"game","ids":{"steam":"1"}}""", "unknown_id_key", "steam", null)]
        [DataRow("""{"type":"movie","ids":[],"title":"A"}""", "invalid_ids", null, "A")]
        [DataRow("""{"type":"movie","type":"series","title":"A"}""", "duplicate_key", "type", "A")]
        [DataRow("""{"type":"movie","year":1700,"title":"A"}""", "invalid_year", null, "A")]
        [DataRow("""{"type":"movie","year":"1999","title":"A"}""", "invalid_year", null, "A")]
        [DataRow("""{"type":"movie","title":5}""", "invalid_text", "title", null)]
        [DataRow("""{"type":"movie","title":"a\u0001b"}""", "invalid_text", "title", null)]
        [DataRow("""{"type":"movie","title":"a\ud800b"}""", "invalid_text", null, null)]
        [DataRow("""{"type":"movie","title":"   "}""", "invalid_text", "title", null)]
        [DataRow("""{"type":"movie","title":"<b>x</b>","author":"a","artist":"b"}""", "duplicate_key", "author", "<b>x</b>")]
        [DataRow("5", "not_object", null, null)]
        public void Parse_FaultyEntry_IsInvalidOnItsOwn(string entry, string problem, string? detail, string? title)
        {
            var items = MediaImportParser.Parse("{\"version\":1,\"items\":[{\"type\":\"series\",\"title\":\"Good\"}," + entry + ",{\"type\":\"series\",\"title\":\"Also good\"}]}");
            Assert.IsNull(items[0].Problem);
            Assert.IsNull(items[2].Problem);
            Assert.AreEqual((problem, detail, title), (items[1].Problem, items[1].ProblemDetail, items[1].Title));
        }

        [TestMethod]
        public void Parse_ValidEntries_AreNormalizedDeterministically()
        {
            var items = MediaImportParser.Parse("""
                {"version":1,"items":[
                  {"type":" Movie ","ids":{"tmdb":603,"imdb":"TT0133093"},"title":"  The   Matrix\t","year":1999},
                  {"type":"series","ids":{"tmdb":"0001399","tvdb":"81189"},"title":"X"},
                  {"type":"book","ids":{"isbn":"0-14-143951-3","openlibrary":"ol66554w"},"author":"  Jane   Austen "},
                  {"type":"manga","ids":{"anilist":"30002","isbn":"978-0-14-143951-8"}},
                  {"type":"music","ids":{"musicbrainz":"F5093C06-23E3-404F-AEAA-40F72885EE3A"},"artist":"Pink Floyd"},
                  {"type":"anime","ids":{"anilist":"16498"},"title":"<script>alert(1)</script>"}
                ]}
                """);
            Assert.IsTrue(items.All(item => item.Problem is null), string.Join(',', items.Select(item => item.Problem)));
            Assert.AreEqual(("movie", MediaType.Movie, (MediaSubtype?)null, "The Matrix", (int?)1999), (items[0].TypeKey, items[0].Type!.Value, items[0].Subtype, items[0].Title, items[0].Year));
            CollectionAssert.AreEqual(new[] { "tmdb_movie:603", "imdb:tt0133093" }, items[0].Ids.Select(id => id.Namespace + ":" + id.Value).ToArray());
            CollectionAssert.AreEqual(new[] { "tmdb_tv:1399", "tvdb:81189" }, items[1].Ids.Select(id => id.Namespace + ":" + id.Value).ToArray());
            CollectionAssert.AreEqual(new[] { "isbn:9780141439518", "openlibrary:OL66554W" }, items[2].Ids.Select(id => id.Namespace + ":" + id.Value).ToArray());
            Assert.AreEqual("Jane Austen", items[2].Creator);
            Assert.AreEqual(MediaSubtype.Manga, items[3].Subtype);
            CollectionAssert.AreEqual(new[] { "anilist:30002", "isbn:9780141439518" }, items[3].Ids.Select(id => id.Namespace + ":" + id.Value).ToArray());
            Assert.AreEqual("f5093c06-23e3-404f-aeaa-40f72885ee3a", items[4].Ids.Single().Value);
            Assert.AreEqual("Pink Floyd", items[4].Creator);

            // Markup is only text; it is never interpreted or removed.
            Assert.AreEqual("<script>alert(1)</script>", items[5].Title);
            Assert.AreEqual(MediaSubtype.Anime, items[5].Subtype);
            CollectionAssert.AreEqual(MediaImportParser.Parse(MediaImportParser.Write(items)).Select(item => item.Title).ToArray(), items.Select(item => item.Title).ToArray());
        }

        [TestMethod]
        public void Example_IsAValidListOfEveryKind()
        {
            var items = MediaImportParser.Parse(MediaImportExample.Json);
            Assert.AreEqual(8, items.Count);
            Assert.IsTrue(items.All(item => item.Problem is null), string.Join(',', items.Select(item => item.Problem + ":" + item.ProblemDetail)));
            CollectionAssert.AreEquivalent(MediaImportParser.TypeKeys.ToArray(), items.Select(item => item.TypeKey).ToArray());
        }

        [TestMethod]
        public async Task Import_Isbn_ResolvesUnimportedEditionToCanonicalWork_AndRejectsWrongTitleOrAuthor()
        {
            var providers = new FakeProviders();
            await using var host = await CreateHostAsync(providers);

            const string isbnList = """
                {"version":1,"items":[
                  {"type":"book","ids":{"isbn":"9780141439518"},"title":"Pride and Prejudice","author":"Jane Austen"},
                  {"type":"book","ids":{"isbn":"9781601424440"},"title":"For Women Only, Revised and Updated Edition","author":"Shaunti Feldhahn"}
                ]}
                """;
            var preview = await PreviewAsync(host, isbnList);
            CollectionAssert.AreEqual(new[] { MediaImportStatusType.New, MediaImportStatusType.New }, preview.Rows.Select(row => row.StatusTypeId).ToArray());
            CollectionAssert.AreEqual(new[] { "openlibrary:OL66554W", "openlibrary:OL7777W" }, preview.Rows.Select(row => row.Choice).ToArray());
            Assert.IsTrue(preview.Rows.All(row => row.IsIncluded));
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));

            var result = await RunAsync(host, isbnList, MediaImportActionType.AddOnly);
            Assert.AreEqual(2, result.Rows.Count(row => row.OutcomeTypeId == MediaImportOutcomeType.Added));
            Assert.AreEqual(2L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));

            // The edition's ISBN now identifies the existing Work; a repeated import never creates a second copy.
            var repeat = await PreviewAsync(host, isbnList);
            Assert.IsTrue(repeat.Rows.All(row => row.StatusTypeId == MediaImportStatusType.InLibrary));
            Assert.AreEqual(2, (await RunAsync(host, isbnList, MediaImportActionType.AddOnly)).Rows.Count(row => row.OutcomeTypeId == MediaImportOutcomeType.Skipped));

            const string mismatched = """
                {"version":1,"items":[
                  {"type":"book","ids":{"isbn":"9780141439518"},"title":"Summary of Pride and Prejudice","author":"Jane Austen"},
                  {"type":"book","ids":{"isbn":"9781601424440"},"title":"For Young Women Only","author":"Shaunti Feldhahn"}
                ]}
                """;
            // Local edition IDs are authoritative for an existing Work; title checks apply before a new Work is imported.
            var empty = await CreateHostAsync(new FakeProviders());
            await using (empty)
            {
                var unmatched = await PreviewAsync(empty, mismatched);
                Assert.IsTrue(unmatched.Rows.All(row => row.StatusTypeId == MediaImportStatusType.Open));
                Assert.AreEqual(0L, await empty.ScalarAsync("""SELECT count(*) FROM "Works" """));
            }
        }

        [TestMethod]
        public async Task Import_ListWithIds_PreviewsWithoutChangesThenAddsMonitorsAndRepeatsWithoutDuplicates()
        {
            var providers = new FakeProviders();
            await using var host = await CreateHostAsync(providers);
            var list = """
                {"version":1,"items":[
                  {"type":"movie","ids":{"tmdb":"603","imdb":"tt0133093"},"title":"The Matrix"},
                  {"type":"movie","ids":{"imdb":"tt0234215"},"title":"The Matrix Reloaded"},
                  {"type":"series","ids":{"tmdb":"1399"}},
                  {"type":"book","ids":{"openlibrary":"OL66554W"}},
                  {"type":"movie","ids":{"tmdb":"603"},"title":"The Matrix again"},
                  {"type":"movie","bogus":1,"title":"Bad"},
                  {"type":"book","title":"Pride and Prejudice","author":"Jane Austen"},
                  {"type":"movie","title":"Twin Title"},
                  {"type":"movie","ids":{"imdb":"tt9999999"},"title":"Unknown Film"},
                  {"type":"film","title":"Wrong type"}
                ]}
                """;

            // The preview matches and proposes; it creates nothing.
            var preview = await PreviewAsync(host, list);
            CollectionAssert.AreEqual(new[]
            {
                MediaImportStatusType.New, MediaImportStatusType.New, MediaImportStatusType.New, MediaImportStatusType.New, MediaImportStatusType.Duplicate,
                MediaImportStatusType.Invalid, MediaImportStatusType.Open, MediaImportStatusType.Open, MediaImportStatusType.Open, MediaImportStatusType.Invalid
            }, preview.Rows.Select(row => row.StatusTypeId).ToArray());
            Assert.AreEqual(("unknown_type", "film", ""), (preview.Rows[9].Problem, preview.Rows[9].ProblemDetail, preview.Rows[9].TypeKey));
            Assert.AreEqual("tmdb_movie:603", preview.Rows[0].Choice);
            Assert.AreEqual("tmdb_movie:604", preview.Rows[1].Choice, "the IMDb id finds its TMDB entry");
            Assert.AreEqual(0, preview.Rows[4].DuplicateOf);
            Assert.AreEqual(("unknown_key", "bogus"), (preview.Rows[5].Problem, preview.Rows[5].ProblemDetail));
            CollectionAssert.AreEqual(new[] { true, true, true, true, false, false, false, false, false, false }, preview.Rows.Select(row => row.IsIncluded).ToArray());
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WantedItems\""));

            // A title alone proposes; one candidate carrying title and author is suggested, two equal ones are not, none is taken.
            var book = preview.Rows[6];
            Assert.AreEqual(("openlibrary:OL66554W", true), (book.Candidates.Single().Choice, book.Candidates.Single().IsSuggested));
            Assert.IsNull(book.Choice);
            Assert.AreEqual(2, preview.Rows[7].Candidates.Count);
            Assert.IsFalse(preview.Rows[7].Candidates.Any(candidate => candidate.IsSuggested));
            Assert.AreEqual(("no_match", 0), (preview.Rows[8].Problem, preview.Rows[8].Candidates.Count));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));

            // The run adds what the preview showed, monitors it and wakes the search once.
            var signal = host.Services.GetRequiredService<AcquisitionSearchSignal>();
            var result = await RunAsync(host, list, MediaImportActionType.MonitorAndSearch);
            CollectionAssert.AreEqual(new[]
            {
                MediaImportOutcomeType.AddedAndMonitored, MediaImportOutcomeType.AddedAndMonitored, MediaImportOutcomeType.AddedAndMonitored, MediaImportOutcomeType.AddedAndMonitored,
                MediaImportOutcomeType.Skipped, MediaImportOutcomeType.Skipped, MediaImportOutcomeType.Skipped, MediaImportOutcomeType.Skipped, MediaImportOutcomeType.Skipped, MediaImportOutcomeType.Skipped
            }, result.Rows.Select(row => row.OutcomeTypeId).ToArray());
            Assert.IsTrue(result.Rows.Take(4).All(row => row.Searches && row.WorkId is not null));
            Assert.IsNull(result.RemainingJson);
            Assert.AreEqual(4L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
            Assert.AreEqual(4L, await host.ScalarAsync("SELECT count(*) FROM \"WantedItems\" WHERE NOT \"IsPaused\""));
            Assert.AreEqual("tmdb_tv:1399,tmdb_movie:603,tmdb_movie:604", await host.ScalarAsync("SELECT string_agg(\"Namespace\" || ':' || \"Value\", ',' ORDER BY \"Value\") FROM \"WorkExternalIdentities\" WHERE \"Namespace\" LIKE 'tmdb%'"));
            Assert.IsTrue(signal.TakeRequest());

            // Repeating the list changes nothing; every title is known now, and a book is also found by an ISBN of its edition.
            var again = await PreviewAsync(host, list.Replace("""{"type":"book","ids":{"openlibrary":"OL66554W"}}""", """{"type":"book","ids":{"isbn":"0-14-143951-3"}}""", StringComparison.Ordinal));
            CollectionAssert.AreEqual(new[] { MediaImportStatusType.InLibrary, MediaImportStatusType.InLibrary, MediaImportStatusType.InLibrary, MediaImportStatusType.InLibrary, MediaImportStatusType.Duplicate },
                again.Rows.Take(5).Select(row => row.StatusTypeId).ToArray());
            Assert.IsTrue(again.Rows.Take(4).All(row => row.HasTarget && !row.IsIncluded && row.WorkId is not null));
            Assert.AreEqual("The Matrix", again.Rows[0].MatchTitle);
            var repeated = await RunAsync(host, list, MediaImportActionType.MonitorAndSearch);
            Assert.IsTrue(repeated.Rows.All(row => row.OutcomeTypeId == MediaImportOutcomeType.Skipped));
            Assert.AreEqual(4L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
            Assert.AreEqual(4L, await host.ScalarAsync("SELECT count(*) FROM \"WantedItems\""));
            Assert.IsFalse(signal.TakeRequest());
        }

        [TestMethod]
        public async Task Import_AddOnlyThenMonitor_KeepsOneWorkAndGivesItATargetLater()
        {
            await using var host = await CreateHostAsync(new FakeProviders());
            await host.Fixture.RunSqlAsync("""INSERT INTO "AcquisitionProfiles" ("Name", "MediaTypeId", "Definition") SELECT 'Movies 4K', 1, "Definition" FROM "AcquisitionProfiles" WHERE "MediaTypeId" = 1 AND "IsDefault";""");
            const string list = """{"version":1,"items":[{"type":"movie","ids":{"tmdb":"603"}},{"type":"book","ids":{"openlibrary":"OL66554W"}}]}""";
            var signal = host.Services.GetRequiredService<AcquisitionSearchSignal>();

            // Only adding leaves the titles without a target and searches nothing.
            var added = await RunAsync(host, list, MediaImportActionType.AddOnly);
            Assert.IsTrue(added.Rows.All(row => row.OutcomeTypeId == MediaImportOutcomeType.Added && !row.Searches));
            Assert.AreEqual((2L, 0L), (await host.ScalarAsync("SELECT count(*) FROM \"Works\""), await host.ScalarAsync("SELECT count(*) FROM \"WantedItems\"")));
            Assert.IsFalse(signal.TakeRequest());

            // The same list with monitoring now finds both in the library and monitors them, without a second Work.
            var preview = await PreviewAsync(host, list, MediaImportActionType.Monitor);
            Assert.IsTrue(preview.Rows.All(row => row.StatusTypeId == MediaImportStatusType.InLibrary && !row.HasTarget && row.IsIncluded && row.Action == MediaImportActionType.Monitor));
            var monitored = await RunAsync(host, list, MediaImportActionType.Monitor);
            Assert.IsTrue(monitored.Rows.All(row => row.OutcomeTypeId == MediaImportOutcomeType.Monitored && !row.Searches));
            Assert.AreEqual((2L, 2L), (await host.ScalarAsync("SELECT count(*) FROM \"Works\""), await host.ScalarAsync("SELECT count(*) FROM \"WantedItems\" WHERE NOT \"IsPaused\"")));
            Assert.IsFalse(signal.TakeRequest(), "monitoring alone leaves the search to the next regular pass");
        }

        [TestMethod]
        public async Task Import_PartialFailures_AreReportedPerRowAndLeaveTheRestForTheNextRun()
        {
            var providers = new FakeProviders();
            await using var host = await CreateHostAsync(providers);
            const string list = """{"version":1,"items":[{"type":"movie","ids":{"tmdb":"603"}},{"type":"movie","ids":{"tmdb":"5000"}},{"type":"movie","ids":{"tmdb":"604"}}]}""";

            // A title the provider does not know fails alone.
            var first = await RunAsync(host, list, MediaImportActionType.AddOnly);
            CollectionAssert.AreEqual(new[] { MediaImportOutcomeType.Added, MediaImportOutcomeType.NotFound, MediaImportOutcomeType.Added }, first.Rows.Select(row => row.OutcomeTypeId).ToArray());
            Assert.IsNull(first.RemainingJson);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));

            // A provider that does not answer fails the rows that need it, and they come back as a list of their own.
            providers.IsUnavailable = true;
            var down = await RunAsync(host, """{"version":1,"items":[{"type":"movie","ids":{"tmdb":"603"}},{"type":"movie","ids":{"tmdb":"700"},"title":"Seven"},{"type":"movie","ids":{"tmdb":"701"}}]}""", MediaImportActionType.AddOnly);
            CollectionAssert.AreEqual(new[] { MediaImportOutcomeType.Skipped, MediaImportOutcomeType.Unavailable, MediaImportOutcomeType.Deferred }, down.Rows.Select(row => row.OutcomeTypeId).ToArray());
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
            var remaining = MediaImportParser.Parse(down.RemainingJson!);
            CollectionAssert.AreEqual(new[] { "tmdb_movie:700", "tmdb_movie:701" }, remaining.Select(item => item.Ids.Single().Namespace + ":" + item.Ids.Single().Value).ToArray());
            Assert.AreEqual("Seven", remaining[0].Title);

            // The next run takes them.
            providers.IsUnavailable = false;
            var next = await RunAsync(host, down.RemainingJson!, MediaImportActionType.AddOnly);
            Assert.IsTrue(next.Rows.All(row => row.OutcomeTypeId == MediaImportOutcomeType.Added));
            Assert.AreEqual(4L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
        }

        [TestMethod]
        public async Task Import_MoreNewTitlesThanOneRunTakes_DefersTheRestAndStaysIdempotent()
        {
            await using var host = await CreateHostAsync(new FakeProviders());
            var list = "{\"version\":1,\"items\":[" + string.Join(',', Enumerable.Range(1000, 45).Select(id => "{\"type\":\"movie\",\"ids\":{\"tmdb\":\"" + id + "\"}}")) + "]}";

            var first = await RunAsync(host, list, MediaImportActionType.AddOnly);
            Assert.AreEqual((40, 5), (first.Rows.Count(row => row.OutcomeTypeId == MediaImportOutcomeType.Added), first.Rows.Count(row => row.OutcomeTypeId == MediaImportOutcomeType.Deferred)));
            Assert.AreEqual(40L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));

            // The remainder alone, or the whole list again, ends with each title once.
            var second = await RunAsync(host, list, MediaImportActionType.AddOnly);
            Assert.AreEqual((5, 40), (second.Rows.Count(row => row.OutcomeTypeId == MediaImportOutcomeType.Added), second.Rows.Count(row => row.OutcomeTypeId == MediaImportOutcomeType.Skipped)));
            Assert.AreEqual(45L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
            Assert.AreEqual(45L, await host.ScalarAsync("SELECT count(DISTINCT \"Value\") FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'tmdb_movie'"));
        }

        [TestMethod]
        public async Task Import_LargeListOfIds_PreviewsWithoutAskingAProvider()
        {
            var providers = new FakeProviders();
            await using var host = await CreateHostAsync(providers);
            var list = "{\"version\":1,\"items\":[" + string.Join(',', Enumerable.Range(1, 2000).Select(id => "{\"type\":\"movie\",\"ids\":{\"tmdb\":\"" + id + "\"},\"title\":\"Film " + id + "\"}")) + "]}";
            providers.Requests.Clear();

            var preview = await PreviewAsync(host, list);
            Assert.AreEqual(2000, preview.Rows.Count);
            Assert.IsTrue(preview.Rows.All(row => row.StatusTypeId == MediaImportStatusType.New && row.IsIncluded));
            Assert.AreEqual(0, providers.Requests.Count);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
        }

        [TestMethod]
        public async Task Import_DeletedWithExclusion_IsNotAddedAgain()
        {
            await using var host = await CreateHostAsync(new FakeProviders());
            const string list = """{"version":1,"items":[{"type":"movie","ids":{"tmdb":"603"},"title":"The Matrix"}]}""";
            await RunAsync(host, list, MediaImportActionType.AddOnly);
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!;
            using var deleted = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/" + workId + "/deletion", new DeleteWorkDataV1(false, true));
            Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());

            var preview = await PreviewAsync(host, list);
            Assert.AreEqual((MediaImportStatusType.Invalid, "excluded"), (preview.Rows[0].StatusTypeId, preview.Rows[0].Problem));
            var result = await RunAsync(host, list, MediaImportActionType.MonitorAndSearch);
            Assert.AreEqual(MediaImportOutcomeType.Excluded, result.Rows[0].OutcomeTypeId);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
        }

        [TestMethod]
        public async Task Import_OnlyAdministrators_MayPreviewOrRun()
        {
            await using var host = await CreateHostAsync(new FakeProviders());
            const string list = """{"version":1,"items":[{"type":"movie","ids":{"tmdb":"603"}}]}""";
            await host.Fixture.RunSqlAsync("""UPDATE "Accounts" SET "AccountRoleTypeId" = 2 WHERE "Id" = 41;""");
            foreach (var path in new[] { Preview, Run })
            {
                using var refused = await host.SendAsync(HttpMethod.Post, path, Form(list, MediaImportActionType.MonitorAndSearch));
                Assert.IsTrue(refused.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, refused.StatusCode.ToString());
            }

            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));

            // A media manager adds titles by hand and so may import.
            await host.Fixture.RunSqlAsync("""UPDATE "Accounts" SET "AccountRoleTypeId" = 3 WHERE "Id" = 41;""");
            Assert.AreEqual(MediaImportOutcomeType.AddedAndMonitored, (await RunAsync(host, list, MediaImportActionType.Monitor)).Rows.Single().OutcomeTypeId);
        }

        [TestMethod]
        public async Task Import_FileUpload_ReadsStrictUtf8WithinItsLimits()
        {
            await using var host = await CreateHostAsync(new FakeProviders());
            MultipartFormDataContent File(string name, byte[] bytes) => new() { { new ByteArrayContent(bytes), "File", name }, { new StringContent("2"), "Action" } };
            var json = Encoding.UTF8.GetBytes("""{"version":1,"items":[{"type":"movie","ids":{"tmdb":"603"},"title":"Café"}]}""");

            using var accepted = await host.SendAsync(HttpMethod.Post, Preview, File("list.json", [0xEF, 0xBB, 0xBF, .. json]));
            Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode, await accepted.Content.ReadAsStringAsync());
            Assert.AreEqual("Café", (await accepted.Content.ReadFromJsonAsync<MediaImportPreviewV1>())!.Rows.Single().Title);
            using var badBytes = await host.SendAsync(HttpMethod.Post, Preview, File("list.json", [.. json, 0xFF, 0xFE]));
            Assert.AreEqual(HttpStatusCode.BadRequest, badBytes.StatusCode);
            using var wrongType = await host.SendAsync(HttpMethod.Post, Preview, File("list.html", json));
            StringAssert.Contains(await wrongType.Content.ReadAsStringAsync(), "invalid_file_type");
            using var tooLarge = await host.SendAsync(HttpMethod.Post, Preview, File("list.json", new byte[1_048_577]));
            StringAssert.Contains(await tooLarge.Content.ReadAsStringAsync(), "out_of_range");
        }

        [TestMethod]
        public async Task Import_FaultyList_IsRejectedWithItsReasonAndChangesNothing()
        {
            await using var host = await CreateHostAsync(new FakeProviders());
            using var unsupported = await host.SendAsync(HttpMethod.Post, Run, Form("""{"version":3,"items":[{"type":"movie","ids":{"tmdb":"603"}}]}""", MediaImportActionType.Monitor));
            Assert.AreEqual(HttpStatusCode.BadRequest, unsupported.StatusCode);
            StringAssert.Contains(await unsupported.Content.ReadAsStringAsync(), "import_unsupported_version");
            using var unknown = await host.SendAsync(HttpMethod.Post, Preview, Form("""{"version":1,"items":[{"type":"movie","ids":{"tmdb":"603"}}],"note":"x"}""", MediaImportActionType.Monitor));
            StringAssert.Contains(await unknown.Content.ReadAsStringAsync(), "import_unknown_key");
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
        }

        [TestMethod]
        public async Task ImportPage_PreviewShowsEveryRowAndTheRunHonoursTheChoices()
        {
            await using var host = await CreateHostAsync(new FakeProviders());
            await host.Fixture.RunSqlAsync("""INSERT INTO "AcquisitionProfiles" ("Name", "MediaTypeId", "Definition") SELECT 'Movies 4K', 1, "Definition" FROM "AcquisitionProfiles" WHERE "MediaTypeId" = 1 AND "IsDefault";""");
            var profile = (long)(await host.ScalarAsync("""SELECT "Id" FROM "AcquisitionProfiles" WHERE "Name" = 'Movies 4K'"""))!;
            const string list = """
                {"version":1,"items":[
                  {"type":"movie","ids":{"tmdb":"603"},"title":"The Matrix"},
                  {"type":"movie","ids":{"tmdb":"604"}},
                  {"type":"movie","ids":{"tmdb":"701"}},
                  {"type":"book","title":"Pride and Prejudice","author":"Jane Austen"},
                  {"type":"movie","bogus":1,"title":"<script>alert(1)</script>"}
                ]}
                """;

            // The page offers the file, the paste field, the way of adding and the example.
            using var form = await host.SendAsync(HttpMethod.Get, "/Admin/Import");
            var html = await form.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, form.StatusCode, html);
            foreach (var part in new[] { "name=\"File\"", "name=\"Json\"", "name=\"Action\"", "handler=Example" }) StringAssert.Contains(html, part);
            var token = LibraryTests.Token(html);
            using var example = await host.SendAsync(HttpMethod.Get, "/Admin/Import?handler=Example");
            Assert.AreEqual(("application/json", true), (example.Content.Headers.ContentType!.MediaType, example.Content.Headers.ContentDisposition!.FileName!.Contains("jularr-media-list", StringComparison.Ordinal)));
            Assert.AreEqual(8, MediaImportParser.Parse(await example.Content.ReadAsStringAsync()).Count);

            // The preview names each row's status and what is wrong with the invalid one; markup of the file stays text.
            using var preview = await host.SendAsync(HttpMethod.Post, "/Admin/Import?handler=Preview", new FormUrlEncodedContent([KeyValuePair.Create("__RequestVerificationToken", token),
                KeyValuePair.Create("Json", list), KeyValuePair.Create("Action", "3")]));
            html = await preview.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode, html);
            foreach (var part in new[] { "Unknown key: bogus", "&lt;script&gt;alert(1)&lt;/script&gt;", "value=\"openlibrary:OL66554W\" selected", "name=\"Choices[3].Choice\"", "name=\"Choices[0].Include\"", "Movies 4K" }) StringAssert.Contains(html, part);
            Assert.IsFalse(html.Contains("<script>alert(1)", StringComparison.Ordinal));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
            token = LibraryTests.Token(html);

            // Row 0 only adds, row 1 stays out, row 2 follows the list with the 4K profile of the movies, the book is the candidate that was chosen.
            var choices = new List<KeyValuePair<string, string>> { KeyValuePair.Create("__RequestVerificationToken", token), KeyValuePair.Create("Json", list), KeyValuePair.Create("Action", "3"),
                KeyValuePair.Create("Defaults[0].Key", "movie"), KeyValuePair.Create("Defaults[0].AcquisitionProfileId", profile.ToString(System.Globalization.CultureInfo.InvariantCulture)), KeyValuePair.Create("Defaults[0].LibraryRootId", "") };
            void Row(int index, bool include, string? action = null, string? choice = null)
            {
                choices.Add(KeyValuePair.Create($"Choices[{index}].Index", index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                if (include) choices.Add(KeyValuePair.Create($"Choices[{index}].Include", "true"));
                choices.Add(KeyValuePair.Create($"Choices[{index}].Include", "false"));
                choices.Add(KeyValuePair.Create($"Choices[{index}].Action", action ?? ""));
                if (choice is not null) choices.Add(KeyValuePair.Create($"Choices[{index}].Choice", choice));
            }

            Row(0, true, "1");
            Row(1, false);
            Row(2, true);
            Row(3, true, choice: "openlibrary:OL66554W");
            choices.Add(KeyValuePair.Create("Choices[4].Index", "4"));
            using var applied = await host.SendAsync(HttpMethod.Post, "/Admin/Import?handler=Apply", new FormUrlEncodedContent(choices));
            html = await applied.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, applied.StatusCode, html);
            StringAssert.Contains(html, "Added, monitored");
            StringAssert.Contains(html, "Searching");
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WantedItems\" AS target JOIN \"WorkExternalIdentities\" AS identity ON identity.\"WorkId\" = target.\"WorkId\" WHERE identity.\"Value\" = '603'"));
            Assert.AreEqual(profile, await host.ScalarAsync("SELECT target.\"AcquisitionProfileId\" FROM \"WantedItems\" AS target JOIN \"WorkExternalIdentities\" AS identity ON identity.\"WorkId\" = target.\"WorkId\" WHERE identity.\"Value\" = '701'"));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WantedItems\" WHERE NOT \"IsPaused\""));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WorkExternalIdentities\" WHERE \"Value\" = '604'"));
        }

        [TestMethod]
        public async Task ImportPage_ANormalUser_IsRefused()
        {
            await using var host = await CreateHostAsync(new FakeProviders());
            await host.Fixture.RunSqlAsync("""UPDATE "Accounts" SET "AccountRoleTypeId" = 2 WHERE "Id" = 41;""");
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Import");
            Assert.IsTrue(page.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.Redirect or HttpStatusCode.Found, page.StatusCode.ToString());
            using var example = await host.SendAsync(HttpMethod.Get, "/Admin/Import?handler=Example");
            Assert.AreNotEqual(HttpStatusCode.OK, example.StatusCode);
        }

        private static MultipartFormDataContent Form(string json, MediaImportActionType action) =>
            new() { { new StringContent(json), "Json" }, { new StringContent(((byte)action).ToString(System.Globalization.CultureInfo.InvariantCulture)), "Action" } };

        private static async Task<MediaImportPreviewV1> PreviewAsync(Host host, string json, MediaImportActionType action = MediaImportActionType.MonitorAndSearch)
        {
            using var response = await host.SendAsync(HttpMethod.Post, Preview, Form(json, action));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<MediaImportPreviewV1>())!;
        }

        private static async Task<MediaImportResultV1> RunAsync(Host host, string json, MediaImportActionType action)
        {
            using var response = await host.SendAsync(HttpMethod.Post, Run, Form(json, action));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<MediaImportResultV1>())!;
        }

        internal static async Task<Host> CreateHostAsync(FakeProviders providers)
        {
            var host = await Host.CreateAsync(razor: true, library: true, services: services =>
            {
                services.AddHttpClient<TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => providers);
                services.AddHttpClient<OpenLibraryClient>().ConfigurePrimaryHttpMessageHandler(() => providers);
                services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => providers);
            });
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            var provider = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'tmdb'"))!;
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + provider + "/secret", new SaveProviderSecretDataV1(Token));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            return host;
        }

        // TMDB with a few films and a series, Open Library with one book; an id from 1000 to 1999 is a film.
        internal sealed class FakeProviders : HttpMessageHandler
        {
            public List<string> Requests { get; } = [];
            public bool IsUnavailable { get; set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                Requests.Add(uri.Host + uri.PathAndQuery);
                if (IsUnavailable) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                if (uri.Host == "openlibrary.org")
                {
                    return Task.FromResult(uri.AbsolutePath switch
                    {
                        "/search.json" => Json("""{"docs":[{"key":"/works/OL66554W","title":"Pride and Prejudice","author_name":["Jane Austen"],"first_publish_year":1813,"cover_i":14348537,"isbn":["9780141439518"]}]}"""),
                        "/isbn/9780141439518.json" => Json("""{"works":[{"key":"/works/OL66554W"}]}"""),
                        "/isbn/9781601424440.json" => Json("""{"works":[{"key":"/works/OL7777W"}]}"""),
                        "/works/OL7777W.json" => Json("""{"title":"For Women Only","first_publish_date":"2004","authors":[{"author":{"key":"/authors/OL7777A"}}]}"""),
                        "/works/OL7777W/editions.json" => Json("""{"entries":[{"key":"/books/OL7777M","title":"For Women Only, Revised and Updated Edition","languages":[{"key":"/languages/eng"}],"isbn_13":["9781601424440"],"publish_date":"2013"}]}"""),
                        "/authors/OL7777A.json" => Json("""{"name":"Shaunti Christine Feldhahn"}"""),
                        "/works/OL66554W.json" => Json("""{"title":"Pride and Prejudice","first_publish_date":"1813","description":{"value":"A novel of manners."},"covers":[14348537],"authors":[{"author":{"key":"/authors/OL21594A"}}]}"""),
                        "/authors/OL21594A.json" => Json("""{"name":"Jane Austen"}"""),
                        "/works/OL66554W/editions.json" => Json("""{"entries":[{"key":"/books/OL1M","title":"Pride and Prejudice","languages":[{"key":"/languages/eng"}],"physical_format":"Paperback","isbn_13":["9780141439518"],"publishers":["Penguin"],"publish_date":"2003"}]}"""),
                        _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                    });
                }

                if (request.Headers.Authorization?.Parameter != Token) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                var query = QueryHelpers.ParseQuery(uri.Query);
                var path = uri.AbsolutePath;
                var body = path switch
                {
                    "/3/search/movie" => query.GetValueOrDefault("query").ToString() switch
                    {
                        "Twin Title" => "{\"results\":[" + Film(701, "Twin Title", 2020) + "," + Film(702, "Twin Title", 2020) + "]}",
                        _ => "{\"results\":[]}"
                    },
                    "/3/find/tt0234215" => "{\"movie_results\":[{\"id\":604}],\"tv_results\":[]}",
                    _ when path.StartsWith("/3/find/", StringComparison.Ordinal) => "{\"movie_results\":[],\"tv_results\":[]}",
                    "/3/movie/603" => Film(603, "The Matrix", 1999, "tt0133093"),
                    "/3/movie/604" => Film(604, "The Matrix Reloaded", 2003, "tt0234215"),
                    "/3/movie/701" => Film(701, "Twin Title", 2020),
                    "/3/movie/702" => Film(702, "Twin Title", 2020),
                    "/3/tv/1399" => """{"id":1399,"name":"Fake Show","original_name":"Fake Show","first_air_date":"2020-01-01","overview":"A show.","external_ids":{"tvdb_id":81189},"seasons":[]}""",
                    _ when path.StartsWith("/3/movie/", StringComparison.Ordinal) && int.TryParse(path["/3/movie/".Length..], out var id) && id is >= 700 and < 2000 => Film(id, "Film " + id, 2000),
                    _ => null
                };
                return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(body));
            }

            private static string Film(int id, string title, int year, string? imdb = null) =>
                "{\"id\":" + id + ",\"title\":\"" + title + "\",\"original_title\":\"" + title + "\",\"release_date\":\"" + year + "-03-31\",\"overview\":\"A film.\",\"poster_path\":\"/p" + id
                + ".jpg\",\"external_ids\":{\"imdb_id\":" + (imdb is null ? "null" : "\"" + imdb + "\"") + "},\"genres\":[],\"credits\":{\"cast\":[],\"crew\":[]},\"videos\":{\"results\":[]},\"translations\":{\"translations\":[]}}";

            private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
