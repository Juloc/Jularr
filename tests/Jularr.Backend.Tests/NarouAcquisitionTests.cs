using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Integrations.Logic.AniList;
using Jularr.Integrations.Logic.Narou;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class NarouAcquisitionTests
    {
        private const string Ncode = "n1234ab";
        private const string Title = "転生したら図書館だった件";

        [TestMethod]
        public async Task WebNovel_FoundAndRequested_StaysApartFromTheEdition_AndIsDownloadedImportedAndRead()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var narou = new NarouClientTests.FakeNarou();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, narou);

            // A published light novel of the same name is already here.
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle") VALUES (900, 3, 3, '{Title}');
                INSERT INTO "WorkTitles" ("WorkId", "Title", "IsManual") VALUES (900, '{Title}', TRUE);
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (900, 'anilist', '4242');
                """);

            // Discover finds the web novel by its title and by its ncode, as a web novel of Narō kept as one catalog Work of its own.
            var found = new List<long>();
            foreach (var query in new[] { "転生したら", "N1234AB" })
            {
                using var search = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/search", new SearchDiscoverCatalogDataV1(MediaType.Book, query, MediaSubtype.LightNovel));
                Assert.AreEqual(HttpStatusCode.OK, search.StatusCode, await search.Content.ReadAsStringAsync());
                var item = (await search.Content.ReadFromJsonAsync<BaseDataList<DiscoverCatalogItemV1>>())!.Single(result => result.Namespace == "syosetu");
                Assert.AreEqual((Ncode, Title, 2019), (item.ExternalId, item.Title, item.ReleaseYear), query);
                found.Add(item.WorkId);
            }

            var workId = (long)(await host.ScalarAsync($"SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'syosetu' AND \"Value\" = '{Ncode}'"))!;
            CollectionAssert.AreEqual(new[] { workId, workId }, found);
            Assert.AreNotEqual(900L, workId);

            // The page lists it as a web novel that opens its Work; the Work's page takes Narō's description.
            using var page = await host.SendAsync(HttpMethod.Get, "/?type=lightnovels&q=" + Uri.EscapeDataString("転生したら"));
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "/Details/" + workId);
            StringAssert.Contains(html, "Web novel");
            using var details = await host.SendAsync(HttpMethod.Get, "/Details/" + workId);
            var detailsHtml = await details.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, details.StatusCode, detailsHtml);
            StringAssert.Contains(WebUtility.HtmlDecode(detailsHtml), "司書が異世界で本を集める。");

            // The request makes the web novel its own light novel Work under its ncode; the edition keeps its own.
            using var requested = await host.SendAsync(HttpMethod.Post, $"/api/v1/works/{workId}/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            Assert.AreEqual("3:ja:山田太郎", await host.ScalarAsync($"""
                SELECT work."MediaSubtypeId" || ':' || work."OriginalLanguage" || ':' || person."Name"
                FROM "Works" AS work JOIN "WorkPeople" AS credit ON credit."WorkId" = work."Id" JOIN "People" AS person ON person."Id" = credit."PersonId" WHERE work."Id" = {workId}
                """));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WorkExternalIdentities\" WHERE \"WorkId\" = 900"));

            // The Wanted search offers Narō for the web novel only, and the automatic grab downloads it in passes.
            var wantedId = (long)(await host.ScalarAsync($"SELECT \"Id\" FROM \"WantedItems\" WHERE \"WorkId\" = {workId}"))!;
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode, await searched.Content.ReadAsStringAsync());
            var offer = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.Single();
            Assert.AreEqual((ReleaseDecisionType.Eligible, "EPUB"), (offer.ReleaseDecisionTypeId, offer.QualityKey));
            await host.Fixture.RunSqlAsync("INSERT INTO \"WantedItems\" (\"Id\", \"WorkId\") VALUES (901, 900)");
            using var edition = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/901/search", new SearchWantedDataV1());
            Assert.AreEqual(0, (await edition.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.Count);

            using var grabbed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(offer.CandidateId));
            Assert.AreEqual(HttpStatusCode.OK, grabbed.StatusCode, await grabbed.Content.ReadAsStringAsync());
            await using (var scope = host.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<MonitorDownloadsV1>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default);
            }

            Assert.AreEqual((short)3, await host.ScalarAsync("SELECT \"OperationStatusTypeId\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
            Assert.IsTrue(narou.Requests.All(request => request.StartsWith(NarouClient.Api, StringComparison.Ordinal) || request.StartsWith(NarouClient.Site, StringComparison.Ordinal)));
            Assert.AreEqual(0, Directory.EnumerateFiles(downloads.Path, "*", SearchOption.AllDirectories).Count());

            // The imported EPUB opens in the reader: three chapters in Japanese.
            var assetId = (long)(await host.ScalarAsync($"SELECT asset.\"Id\" FROM \"MediaAssets\" AS asset JOIN \"StoredFiles\" AS file ON file.\"MediaAssetId\" = asset.\"Id\" WHERE asset.\"WorkId\" = {workId}"))!;
            var document = await ReadingTests.OpenAsync(host, assetId);
            Assert.AreEqual((Title, 3), (document.Title, document.SectionCount));
        }

        [TestMethod]
        public async Task WebNovel_FoundByItsEditionsEnglishName_TakesThatNameOnlyWhenTitleAndWriterAgree()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var narou = new NarouClientTests.FakeNarou { Word = Title };
            var aniList = new FakeAniList();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, narou, aniList);

            // The English name finds the published edition on AniList and, through its Japanese title, the web novel right after it under the same name.
            using var found = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/search", new SearchDiscoverCatalogDataV1(MediaType.Book, "Library Reincarnation", MediaSubtype.LightNovel));
            Assert.AreEqual(HttpStatusCode.OK, found.StatusCode, await found.Content.ReadAsStringAsync());
            var items = (await found.Content.ReadFromJsonAsync<BaseDataList<DiscoverCatalogItemV1>>())!.ToList();
            CollectionAssert.AreEqual(new[] { "anilist:4242:Library Reincarnation", "syosetu:" + Ncode + ":Library Reincarnation" },
                items.Select(item => item.Namespace + ":" + item.ExternalId + ":" + item.Title).ToArray());
            Assert.AreEqual(2, narou.Requests.Count);

            // Requested, the web novel is its own Work under its ncode and is called by the edition's name; the edition stays a Work of its own.
            var novelId = await RequestAsync(host, "syosetu", Ncode);
            var editionId = await RequestAsync(host, "anilist", "4242");
            Assert.AreNotEqual(novelId, editionId);
            Assert.AreEqual($"{editionId}:4242", await host.ScalarAsync("""SELECT string_agg("WorkId" || ':' || "Value", ',') FROM "WorkExternalIdentities" WHERE "Namespace" = 'anilist'"""));
            Assert.AreEqual((short)1, await host.ScalarAsync($"""SELECT "IdentityMatchStatusTypeId" FROM "WorkIdentityMatches" WHERE "WorkId" = {novelId}"""));
            Assert.AreEqual("Library Reincarnation", await host.ScalarAsync($"""SELECT "Title" FROM "ProfileWorks" WHERE "WorkId" = {novelId} LIMIT 1"""));
            Assert.AreEqual(Title, await host.ScalarAsync($"""SELECT "CanonicalTitle" FROM "Works" WHERE "Id" = {novelId}"""));

            // Another writer's novel of the same title is only proposed; a person confirms it on the matches page and it takes the name, not the edition.
            narou.Api = NarouClientTests.FakeNarou.Novel.Replace("N1234AB", "N5678CD", StringComparison.Ordinal).Replace("山田太郎", "別人", StringComparison.Ordinal);
            var otherId = await RequestAsync(host, "syosetu", "n5678cd");
            using var listed = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/works/matches");
            var review = (await listed.Content.ReadFromJsonAsync<PageResult<IdentityReviewV1>>())!.Items.Single();
            Assert.AreEqual((otherId, true, 4242), (review.WorkId, review.IsWebNovel, review.CandidateIds.Single()));
            StringAssert.Contains(review.CandidateLabels.Single(), "Library Reincarnation · " + Title + " · 山田太郎");
            Assert.AreEqual(Title, await host.ScalarAsync($"""SELECT "Title" FROM "ProfileWorks" WHERE "WorkId" = {otherId} LIMIT 1"""));
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Works/Matches");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "Web novel");
            StringAssert.Contains(html, "Library Reincarnation");
            using var confirmed = await host.SendAsync(HttpMethod.Post, $"/api/v1/admin/works/{otherId}/matches", new ResolveIdentityReviewDataV1(4242));
            Assert.AreEqual(HttpStatusCode.OK, confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync());
            Assert.AreEqual("Library Reincarnation", await host.ScalarAsync($"""SELECT "Title" FROM "ProfileWorks" WHERE "WorkId" = {otherId} LIMIT 1"""));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "WorkExternalIdentities" WHERE "Namespace" = 'anilist'"""));
        }

        // A title Discover has not shown yet is found first, so that it is a catalog Work; the request names that Work.
        private static async Task<long> RequestAsync(Host host, string source, string id)
        {
            var identity = $"""SELECT "WorkId" FROM "WorkExternalIdentities" WHERE "Namespace" = '{source}' AND "Value" = '{id}'""";
            if (await host.ScalarAsync(identity) is null)
            {
                using var found = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/search", new SearchDiscoverCatalogDataV1(MediaType.Book, id, MediaSubtype.LightNovel));
                Assert.AreEqual(HttpStatusCode.OK, found.StatusCode, await found.Content.ReadAsStringAsync());
            }

            var workId = (long)(await host.ScalarAsync(identity))!;
            using var requested = await host.SendAsync(HttpMethod.Post, $"/api/v1/works/{workId}/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            return workId;
        }

        private static async Task<Host> CreateHostAsync(string storage, string downloads, NarouClientTests.FakeNarou narou, FakeAniList? aniList = null)
        {
            var host = await LibraryTests.CreateHostAsync(storage, razor: true, settings: new Dictionary<string, string> { ["Storage:LocalPaths:2"] = downloads }, services: services =>
            {
                services.AddHttpClient<NarouClient>().ConfigurePrimaryHttpMessageHandler(() => narou);
                if (aniList is not null) services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => aniList);
            });

            // Narō's catalog is off until the Owner switches it on.
            using (var off = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/search", new SearchDiscoverCatalogDataV1(MediaType.Book, "転生したら", MediaSubtype.LightNovel)))
            {
                Assert.AreEqual(0, (await off.Content.ReadFromJsonAsync<BaseDataList<DiscoverCatalogItemV1>>())!.Count(item => item.Namespace == "syosetu"));
                Assert.AreEqual(0, narou.Requests.Count);
            }

            await host.Fixture.RunSqlAsync("UPDATE \"Providers\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'syosetu'");

            // A writable light novel library and Narō as the one source, with its download staging.
            var mountId = await LibraryTests.CreateMountAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"IsReadOnly\" = FALSE");
            Directory.CreateDirectory(Path.Combine(storage, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Light Novels"));
            await LibraryTests.CreateRootAsync(host, mountId, "Light Novels", MediaType.Book, MediaSubtype.LightNovel);
            await DownloadStorageTests.ConfigureAsync(host, downloads, DownloadStorageArea.Direct);
            using var elsewhere = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/acquisition/connections",
                new SaveAcquisitionConnectionDataV1("Mirror", AcquisitionConnectionType.Narou, "https://mirror.example.test", null, true, 50, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.BadRequest, elsewhere.StatusCode);
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/acquisition/connections",
                new SaveAcquisitionConnectionDataV1("Narō", AcquisitionConnectionType.Narou, "https://ncode.syosetu.com", null, true, 50, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            return host;
        }

        // AniList's published edition of the web novel, found by its English or its Japanese title.
        private sealed class FakeAniList : HttpMessageHandler
        {
            private const string Edition = """
                {"id":4242,"type":"MANGA","format":"NOVEL","countryOfOrigin":"JP","title":{"romaji":"Tensei shitara Toshokan datta Ken","english":"Library Reincarnation","native":"転生したら図書館だった件"},
                "synonyms":[],"startDate":{"year":2020},"volumes":2,"staff":{"edges":[{"role":"Story","node":{"id":77,"name":{"full":"Taro Yamada","native":"山田太郎"}}}]},"relations":{"edges":[]}}
                """;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var variables = body.RootElement.GetProperty("variables");
                var json = !variables.TryGetProperty("search", out var search) ? "{\"data\":{\"Media\":" + Edition + "}}"
                    : search.GetString() is "Library Reincarnation" or Title ? "{\"data\":{\"Page\":{\"media\":[" + Edition + "]}}}"
                    : "{\"data\":{\"Page\":{\"media\":[]}}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
        }
    }
}
