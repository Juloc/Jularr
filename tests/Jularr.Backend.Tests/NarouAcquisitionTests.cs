using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.System.Acquisition.V1;
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
        [TestMethod]
        public async Task LightNovel_AutomaticallyFindsVerifiedNarouAndImportsThroughWanted()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var source = new FakeNarou();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, source);
            var (workId, wantedId) = await CreateWantedAsync(host);
            using var searched = await host.SendAsync(HttpMethod.Post,
                "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode, await searched.Content.ReadAsStringAsync());
            var candidates = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!;
            var candidate = candidates.Single(item => item.ConnectionName == "Narou");
            Assert.AreEqual(ReleaseDecisionType.Eligible, candidate.ReleaseDecisionTypeId);

            using var grabbed = await host.SendAsync(HttpMethod.Post,
                "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(candidate.CandidateId));
            Assert.AreEqual(HttpStatusCode.OK, grabbed.StatusCode, await grabbed.Content.ReadAsStringAsync());

            await using (var scope = host.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<MonitorDownloadsV1>()
                    .ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default);
            }
            Assert.AreEqual(1L, await host.ScalarAsync(
                "SELECT count(*) FROM \"StoredFiles\" AS file JOIN \"MediaAssets\" AS asset ON file.\"MediaAssetId\" = asset.\"Id\" WHERE asset.\"WorkId\" = "
                + workId + " AND file.\"MissingSince\" IS NULL"));
            Assert.IsTrue(source.Urls.Any(url => url.StartsWith("api.syosetu.com/novelapi/api/", StringComparison.Ordinal)));
            Assert.IsTrue(source.Urls.Any(url => url == "ncode.syosetu.com/n123/1/"));
        }

        [TestMethod]
        public async Task LightNovel_RejectsUnrelatedNarouAuthor()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var source = new FakeNarou { WrongAuthor = true };
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, source);
            var (_, wantedId) = await CreateWantedAsync(host);
            using var searched = await host.SendAsync(HttpMethod.Post,
                "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            if (searched.IsSuccessStatusCode)
            {
                var candidates = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!;
                Assert.IsFalse(candidates.Any(candidate => candidate.ConnectionName == "Narou"));
            }
            else
            {
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, searched.StatusCode);
            }
        }

        private static async Task<Host> CreateHostAsync(string storage, string downloads, FakeNarou source)
        {
            var host = await LibraryTests.CreateHostAsync(storage, razor: true,
                settings: new Dictionary<string, string> { ["Storage:LocalPaths:2"] = downloads },
                services: services => services.AddHttpClient<NarouClient>().ConfigurePrimaryHttpMessageHandler(() => source));
            var mount = await LibraryTests.CreateMountAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"IsReadOnly\" = FALSE");
            Directory.CreateDirectory(Path.Combine(storage, mount.ToString(System.Globalization.CultureInfo.InvariantCulture), "Books"));
            await LibraryTests.CreateRootAsync(host, mount, "Books", MediaType.Book);
            await DownloadStorageTests.ConfigureAsync(host, downloads, Jularr.Backend.Data.Enums.Storage.DownloadStorageArea.Direct);
            using var connection = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/acquisition/connections",
                new SaveAcquisitionConnectionDataV1("Narou", AcquisitionConnectionType.Narou,
                    "https://ncode.syosetu.com", null, true, 30, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, connection.StatusCode, await connection.Content.ReadAsStringAsync());
            return host;
        }

        private static async Task<(long WorkId, long WantedId)> CreateWantedAsync(Host host)
        {
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works",
                new CreateWorkDataV1(MediaType.Book, "テスト作品", null, MediaSubtype.LightNovel));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var work = (await created.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "People" ("Id", "Name") VALUES (9007, '著者') ON CONFLICT DO NOTHING;
                INSERT INTO "WorkPeople" ("WorkId", "PersonId", "PersonRoleId", "Position")
                    VALUES ({work.Id}, 9007, 7, 1);
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId", "AcquisitionRequestStatusTypeId", "DecidedByAccountId", "DecidedAt")
                    VALUES (99101, {work.Id}, 202, 42, 1, 2, 41, now());
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (99201, {work.Id});
                INSERT INTO "RequestTargets" ("AcquisitionRequestId", "WantedItemId") VALUES (99101, 99201);
                """);
            return (work.Id, 99201);
        }

        private sealed class FakeNarou : HttpMessageHandler
        {
            public bool WrongAuthor { get; init; }
            public List<string> Urls { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                Urls.Add(uri.Host + uri.AbsolutePath);
                var result = uri.Host == "api.syosetu.com"
                    ? "[{\"allcount\":1},{\"ncode\":\"n123\",\"title\":\"テスト作品\",\"writer\":\"" +
                      (WrongAuthor ? "別人" : "著者") + "\"}]"
                    : uri.AbsolutePath == "/n123/"
                    ? "<h1 class=\"p-novel__title\">テスト作品</h1><div class=\"p-novel__author\">著者</div><a href=\"/n123/1/\">第1話</a>"
                    : uri.AbsolutePath == "/n123/1/"
                    ? "<div id=\"novel_honbun\"><p>小説本文をここに保存します。</p></div>"
                    : "<html><body>OK</body></html>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(result, Encoding.UTF8,
                        uri.Host == "api.syosetu.com" ? "application/json" : "text/html")
                });
            }
        }
    }
}
