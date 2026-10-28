using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Service.System.Acquisition.V1;
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

            // Discover finds the web novel by its title and by its ncode, as a web novel of Narō.
            foreach (var query in new[] { "転生したら", "N1234AB" })
            {
                using var found = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/search", new SearchDiscoverCatalogDataV1(MediaType.Book, query, MediaSubtype.LightNovel));
                Assert.AreEqual(HttpStatusCode.OK, found.StatusCode, await found.Content.ReadAsStringAsync());
                var item = (await found.Content.ReadFromJsonAsync<BaseDataList<DiscoverCatalogItemV1>>())!.Single(result => result.Namespace == "syosetu");
                Assert.AreEqual((Ncode, Title, 2019, (long?)null), (item.ExternalId, item.Title, item.ReleaseYear, item.WorkId), query);
            }

            // The page lists it as a web novel and its request page names the ncode.
            using var page = await host.SendAsync(HttpMethod.Get, "/?type=lightnovels&q=" + Uri.EscapeDataString("転生したら"));
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "/Discover/lightnovels/syosetu/" + Ncode);
            StringAssert.Contains(html, "Web novel");
            using var requestPage = await host.SendAsync(HttpMethod.Get, "/Discover/lightnovels/syosetu/" + Ncode);
            StringAssert.Contains(await requestPage.Content.ReadAsStringAsync(), "Web novel " + Ncode);

            using var entry = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/entry", new ReadDiscoverCatalogEntryDataV1(MediaType.Book, "syosetu", Ncode, MediaSubtype.LightNovel));
            Assert.AreEqual("司書が異世界で本を集める。", (await entry.Content.ReadFromJsonAsync<DiscoverCatalogEntryV1>())!.Overview);

            // The request makes the web novel its own light novel Work under its ncode; the edition keeps its own.
            using var requested = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/requests", new RequestDiscoverCatalogDataV1(MediaType.Book, "syosetu", Ncode, 1, MediaSubtype.LightNovel));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            var workId = (long)(await host.ScalarAsync($"SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'syosetu' AND \"Value\" = '{Ncode}'"))!;
            Assert.AreNotEqual(900L, workId);
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

        private static async Task<Host> CreateHostAsync(string storage, string downloads, NarouClientTests.FakeNarou narou)
        {
            var host = await LibraryTests.CreateHostAsync(storage, razor: true, settings: new Dictionary<string, string> { ["Storage:LocalPaths:2"] = downloads },
                services: services => services.AddHttpClient<NarouClient>().ConfigurePrimaryHttpMessageHandler(() => narou));

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
    }
}
