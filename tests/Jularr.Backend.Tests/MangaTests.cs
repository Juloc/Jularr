using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Integrations.Logic.AniList;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Sabnzbd;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class MangaTests
    {
        private const string Connections = "/api/v1/admin/acquisition/connections";

        [TestMethod]
        public async Task Manga_AniListVolumesPacksAndSingles_CompleteOnlyWhenEveryVolumeExists()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeMangaSources();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);

            // AniList names the series, both titles and its volumes.
            using var found = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/metadata/search", new SearchMetadataDataV1(MediaType.Book, "Example Manga", mediaSubtypeId: MediaSubtype.Manga));
            Assert.AreEqual(HttpStatusCode.OK, found.StatusCode, await found.Content.ReadAsStringAsync());
            var results = (await found.Content.ReadFromJsonAsync<BaseDataList<MetadataSearchResultV1>>())!;
            CollectionAssert.AreEquivalent(new[] { "openlibrary", "anilist" }, results.Select(item => item.Namespace).ToArray());
            var result = results.Single(item => item.Namespace == "anilist");
            Assert.AreEqual("101", result.ExternalId);
            Assert.AreEqual("Mika Example", result.Author);
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Book, result.ExternalId, MediaSubtype.Manga, "anilist"));
            Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
            var work = (await imported.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            Assert.AreEqual("Example Manga", work.CanonicalTitle);
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT count(*) FROM \"WorkUnits\" WHERE \"WorkUnitTypeId\" = 3 AND \"ParentWorkUnitId\" IS NULL"));
            Assert.AreEqual("Reidai Manga", await host.ScalarAsync("SELECT \"Title\" FROM \"WorkTitles\" WHERE \"Title\" <> 'Example Manga'"));

            // The same series from Open Library joins the Work instead of becoming a second one.
            using var joined = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Book, "OL9W", MediaSubtype.Manga, "openlibrary"));
            Assert.AreEqual(HttpStatusCode.OK, joined.StatusCode, await joined.Content.ReadAsStringAsync());
            Assert.AreEqual(work.Id, (await joined.Content.ReadFromJsonAsync<WorkDetailsV1>())!.Id);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkExternalIdentities\""));
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId", "AcquisitionRequestStatusTypeId", "DecidedByAccountId", "DecidedAt")
                VALUES (700, {work.Id}, 202, 42, 1, 2, 41, now());
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (800, {work.Id});
                INSERT INTO "RequestTargets" ("AcquisitionRequestId", "WantedItemId") VALUES (700, 800);
                """);

            // Both libraries accept Manga; a Light Novel profile does not fit, a Book profile does.
            using var listed = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/wanted/800/libraries");
            var libraries = (await listed.Content.ReadFromJsonAsync<BaseDataList<WantedLibraryV1>>())!;
            CollectionAssert.AreEqual(new[] { "Books", "Manga" }, libraries.Select(library => library.Name).ToArray());
            var novels = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"AcquisitionProfiles\" WHERE \"Name\" = 'Light Novels'"))!;
            var books = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"AcquisitionProfiles\" WHERE \"Name\" = 'Books'"))!;
            using var wrong = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/800", new UpdateWantedDataV1(false, 1, novels));
            Assert.AreEqual(HttpStatusCode.BadRequest, wrong.StatusCode);
            using var chosen = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/800", new UpdateWantedDataV1(false, 1, books, libraries.Single(library => library.Name == "Manga").Id));
            Assert.AreEqual(HttpStatusCode.OK, chosen.StatusCode, await chosen.Content.ReadAsStringAsync());
            using var reset = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/800", new UpdateWantedDataV1(false, 2, null, libraries.Single(library => library.Name == "Manga").Id));
            Assert.AreEqual(HttpStatusCode.OK, reset.StatusCode);

            // A switched-off type is not searched.
            using var off = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/media", new UpdateInstanceMediaDataV1("Book", false, 1));
            Assert.AreEqual(HttpStatusCode.OK, off.StatusCode, await off.Content.ReadAsStringAsync());
            await RunAsync<SearchDueWantedV1>(host);
            Assert.AreEqual(0, sources.Grabbed.Count);
            using var on = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/media", new UpdateInstanceMediaDataV1("Book", true, 2));
            Assert.AreEqual(HttpStatusCode.OK, on.StatusCode);

            // The first missing volumes come as a pack, the rest alone; claimed volumes are not searched again.
            await RunAsync<SearchDueWantedV1>(host);
            CollectionAssert.AreEqual(new[] { "https://indexer.example.test/getnzb/v12", "https://indexer.example.test/getnzb/v3" }, sources.Grabbed);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionReleases\" WHERE \"ParentUnitNumber\" IS NULL AND \"UnitStart\" = 1 AND \"UnitEnd\" = 2 AND \"QualityKey\" = 'COMIC'"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionReleases\" WHERE \"UnitStart\" = 3 AND \"UnitEnd\" = 3 AND \"QualityKey\" = 'CBR'"));
            await host.Fixture.RunSqlAsync("UPDATE \"WantedItems\" SET \"NextSearchAt\" = NULL");
            await RunAsync<SearchDueWantedV1>(host);
            Assert.AreEqual(2, sources.Grabbed.Count);

            // Each volume file is stored by its number; archives must be what their names say.
            var pack = Directory.CreateDirectory(Path.Combine(downloads.Path, "Example Manga v01-02")).FullName;
            File.WriteAllBytes(Path.Combine(pack, "Example Manga v01 (2019) (Digital).cbz"), Archive("PK\u0003\u0004", 2000));
            File.WriteAllBytes(Path.Combine(pack, "Example Manga v02 (2019) (Digital).cbz"), Archive("PK\u0003\u0004", 2100));
            File.WriteAllBytes(Path.Combine(pack, "Example Manga v02 (2019) (Digital) small.cbz"), Archive("PK\u0003\u0004", 100));
            sources.History = """{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_1","name":"Pack","status":"Completed","storage":"/downloads/Example Manga v01-02"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);
            var library = Path.Combine(storage.Path, "1", "Manga", "Example Manga");
            Assert.IsTrue(File.Exists(Path.Combine(library, "Example Manga v01.cbz")));
            Assert.AreEqual(2100L, new FileInfo(Path.Combine(library, "Example Manga v02.cbz")).Length);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"MediaAssets\" WHERE \"WorkUnitId\" IS NOT NULL AND \"MediaAssetTypeId\" = 4"));
            Assert.AreNotEqual((short)6, await host.ScalarAsync("SELECT \"AcquisitionRequestStatusTypeId\" FROM \"AcquisitionRequests\""));

            var single = Directory.CreateDirectory(Path.Combine(downloads.Path, "Example Manga v03")).FullName;
            File.WriteAllBytes(Path.Combine(single, "Example Manga v03 (2021) (Digital).cbr"), Archive("Rar!\u001A\u0007", 3000));
            sources.History = """{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_2","name":"Single","status":"Completed","storage":"/downloads/Example Manga v03"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.IsTrue(File.Exists(Path.Combine(library, "Example Manga v03.cbr")));
            Assert.AreEqual((short)6, await host.ScalarAsync("SELECT \"AcquisitionRequestStatusTypeId\" FROM \"AcquisitionRequests\""));

            // A manual search can target one volume; other series and chapter releases do not fill it.
            using var manual = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/800/search", new SearchWantedDataV1(null, 3));
            Assert.AreEqual(HttpStatusCode.OK, manual.StatusCode, await manual.Content.ReadAsStringAsync());
            var candidates = (await manual.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.ToArray();
            Assert.AreEqual(ReleaseDecisionType.Eligible, candidates.Single(candidate => candidate.Title.StartsWith("Example Manga v03", StringComparison.Ordinal)).ReleaseDecisionTypeId);
            Assert.AreEqual("WrongVolume", candidates.Single(candidate => candidate.Title.StartsWith("Example Manga v01-02", StringComparison.Ordinal)).ReasonCode);
            Assert.AreEqual("ChaptersOnly", candidates.Single(candidate => candidate.Title.StartsWith("Example Manga c010", StringComparison.Ordinal)).ReasonCode);
            Assert.AreEqual(ReleaseDecisionType.Rejected, candidates.Single(candidate => candidate.Title.StartsWith("Other Manga", StringComparison.Ordinal)).ReleaseDecisionTypeId);
            Assert.AreNotEqual(ReleaseDecisionType.Eligible, candidates.Single(candidate => candidate.Title.StartsWith("Example Manga Side Story", StringComparison.Ordinal)).ReleaseDecisionTypeId);
        }

        [TestMethod]
        [DataRow("Example Manga v01-05 (2019) (Digital) (Group)", "Example Manga", 1, 5, "COMIC")]
        [DataRow("Example.Manga.Vol.3.cbr", "Example Manga", 3, null, "CBR")]
        [DataRow("Example Manga Volume 12 [EPUB]", "Example Manga", 12, null, "EPUB")]
        [DataRow("Example Manga (Complete) (Digital) cbz", "Example Manga", null, null, "CBZ")]
        [DataRow("Example Manga v01 - 2019 (Digital)", "Example Manga", 1, null, "COMIC")]
        [DataRow("[Group] (Digital) Example Manga v02", "Example Manga", 2, null, "COMIC")]
        [DataRow("Example Manga (c2c) (Digital)", "Example Manga", null, null, "COMIC")]
        public void Manga_ReleaseNames_NameSeriesVolumesAndFormat(string title, string series, int? start, int? end, string format)
        {
            var parsed = MangaReleaseJudge.Parse(title);
            Assert.AreEqual(series, parsed.SeriesTitle);
            Assert.AreEqual(start, parsed.VolumeStart);
            Assert.AreEqual(end, parsed.VolumeEnd);
            Assert.AreEqual(format, Jularr.Backend.Logic.Acquisition.ReleaseQuality.GetKey(parsed));
        }

        [TestMethod]
        public void Manga_WithoutKnownVolumes_OnlyUnnumberedReleasesFillTheSeries()
        {
            // Chapters never fill volumes; a numbered volume cannot complete a series of unknown length.
            Assert.AreEqual("ChaptersOnly", Judge("Example Manga c001-010 (Digital)").Evidence.Code);
            var numbered = Judge("Example Manga v03 (Digital)");
            Assert.AreEqual(MangaIdentityMatch.AmbiguousTitle, numbered.Match);
            Assert.AreEqual("VolumesUnknown", numbered.Evidence.Code);
            Assert.AreEqual(MangaIdentityMatch.Matches, Judge("Example Manga (c2c) (Digital)").Match);
            CollectionAssert.AreEqual(new[] { "FR" }, Judge("Example Manga (French) (Digital)").Languages!.ToArray());

            // A file holding several volumes is no single volume.
            Assert.AreEqual(0, ImportPlacementLogic.Plan(MediaType.Book, MediaSubtype.Manga, "Example Manga", null, null, [new("Example Manga v01-03.cbz", 1000, DateTime.UtcNow)]).Count);
            Assert.AreEqual(1, ImportPlacementLogic.Plan(MediaType.Book, MediaSubtype.Manga, "Example Manga", null, null, [new("Example Manga v01-03.cbz", 1000, DateTime.UtcNow),
                new("Example Manga v04.cbz", 1000, DateTime.UtcNow)]).Count);

            static ReleaseJudgement<MangaIdentityMatch> Judge(string title) => MangaReleaseJudge.Judge(["Example Manga"], null,
                new ReleaseOffer(title, 1, "Indexer", 25, "guid", new Uri("https://indexer.example.test/guid"), null, 1_000_000, DateTimeOffset.UtcNow, ["title"]));
        }

        [TestMethod]
        public async Task AniList_NullFields_ReadAsMissing()
        {
            var sources = new FakeMangaSources { Manga = """{"id":102,"title":{"romaji":"Reidai","english":null,"native":null},"startDate":null,"volumes":null,"description":null,"coverImage":null,"staff":null}""" };
            var manga = await new AniListClient(new HttpClient(sources)).ReadAsync(Jularr.Integrations.Data.AniList.AniListMediaType.Manga, 102, CancellationToken.None);
            Assert.AreEqual("Reidai", manga.RomajiTitle);
            Assert.IsNull(manga.Volumes);
            Assert.AreEqual(0, manga.Authors.Count);
            sources.Manga = "null";
            await Assert.ThrowsExactlyAsync<Jularr.Integrations.Data.AniList.AniListException>(() => new AniListClient(new HttpClient(sources)).ReadAsync(Jularr.Integrations.Data.AniList.AniListMediaType.Manga, 102, CancellationToken.None));
        }

        private static byte[] Archive(string header, int length) => [.. Encoding.Latin1.GetBytes(header), .. new byte[length - header.Length]];

        private static async Task<Host> CreateHostAsync(string storage, string downloads, FakeMangaSources sources)
        {
            var host = await LibraryTests.CreateHostAsync(storage, services: services =>
            {
                services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
                services.AddHttpClient<Jularr.Integrations.Logic.OpenLibrary.OpenLibraryClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
                services.AddHttpClient<NewznabClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
                services.AddHttpClient<SabnzbdClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
            });

            // Writable libraries, one indexer and one client.
            var mountId = await LibraryTests.CreateMountAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"IsReadOnly\" = FALSE");
            // The Books library comes first; Manga still lands in its own.
            Directory.CreateDirectory(Path.Combine(storage, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Books"));
            Directory.CreateDirectory(Path.Combine(storage, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Manga"));
            await LibraryTests.CreateRootAsync(host, mountId, "Books", MediaType.Book);
            await LibraryTests.CreateRootAsync(host, mountId, "Manga", MediaType.Book, MediaSubtype.Manga);
            foreach (var data in new[]
            {
                new SaveAcquisitionConnectionDataV1("Indexer", AcquisitionConnectionType.Newznab, "https://indexer.example.test/", "indexer-key", true, 10, [7030], null, null, null),
                new SaveAcquisitionConnectionDataV1("SAB", AcquisitionConnectionType.Sabnzbd, "https://sab.example.test", "client-key", true, 10, [], "manga", "/downloads", downloads)
            })
            {
                using var created = await host.SendAsync(HttpMethod.Post, Connections, data);
                Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            }

            return host;
        }

        private static async Task RunAsync<TService>(Host host) where TService : Service.Core.SystemService<AcquisitionCycleParametersV1, NoData>
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TService>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default);
        }

        private sealed class FakeMangaSources : HttpMessageHandler
        {
            public string Manga { get; set; } = """
                {"id":101,"type":"MANGA","format":"MANGA","title":{"romaji":"Reidai Manga","english":"Example Manga","native":"例題漫画"},"synonyms":[],"startDate":{"year":2019},"volumes":3,"status":"FINISHED",
                 "description":"A <i>test</i> series.","coverImage":{"large":"https://img.example.test/101.jpg"},
                 "staff":{"edges":[{"role":"Story & Art","node":{"name":{"full":"Mika Example"}}},{"role":"Translator","node":{"name":{"full":"Somebody Else"}}}]}}
                """;


            public List<string> Grabbed { get; } = [];
            public string History { get; set; } = """{"history":{"slots":[]}}""";

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                switch (uri.Host)
                {
                    case "openlibrary.org":
                        return uri.AbsolutePath switch
                        {
                            "/search.json" => Json("""{"docs":[{"key":"/works/OL9W","title":"Example Manga","author_name":["Mika Example"],"first_publish_year":2019}]}"""),
                            "/works/OL9W.json" => Json("""{"title":"Example Manga","first_publish_date":"2019","authors":[{"author":{"key":"/authors/OL1A"}}]}"""),
                            "/authors/OL1A.json" => Json("""{"name":"Mika Example"}"""),
                            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                        };
                    case "graphql.anilist.co":
                        var query = await request.Content!.ReadAsStringAsync(cancellationToken);
                        return Json(query.Contains("Page(", StringComparison.Ordinal) ? "{\"data\":{\"Page\":{\"media\":[" + Manga + "]}}}" : "{\"data\":{\"Media\":" + Manga + "}}");
                    case "indexer.example.test" when uri.Query.Contains("t=caps", StringComparison.Ordinal):
                        return Xml("""
                            <caps><limits max="100" default="100" /><searching><search available="yes" supportedParams="q" /></searching>
                              <categories><category id="7000" name="Books"><subcat id="7030" name="Comics" /></category></categories></caps>
                            """);
                    case "indexer.example.test":
                        return Xml("""
                            <rss><channel>
                              <item><title>Example Manga v01-02 (2019) (Digital) (Group)</title><guid>v12</guid><link>https://indexer.example.test/getnzb/v12</link><size>400000000</size></item>
                              <item><title>Example Manga v03 (2021) (Digital) cbr</title><guid>v3</guid><link>https://indexer.example.test/getnzb/v3</link><size>200000000</size></item>
                              <item><title>Example Manga c010 (Digital)</title><guid>c10</guid><link>https://indexer.example.test/getnzb/c10</link><size>20000000</size></item>
                              <item><title>Example Manga Side Story v03 (Digital)</title><guid>side</guid><link>https://indexer.example.test/getnzb/side</link><size>200000000</size></item>
                              <item><title>Other Manga v03 (Digital)</title><guid>other</guid><link>https://indexer.example.test/getnzb/other</link><size>200000000</size></item>
                            </channel></rss>
                            """);
                    default:
                        var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                        switch (form["mode"])
                        {
                            case "addurl":
                                Grabbed.Add(form["name"]!);
                                return Json($$"""{"status":true,"nzo_ids":["SABnzbd_nzo_{{Grabbed.Count}}"]}""");
                            case "queue":
                                return Json("""{"queue":{"paused":false,"slots":[]}}""");
                            case "history":
                                return Json(History);
                            default:
                                return Json("""{"version":"4.5.0"}""");
                        }
                }
            }

            private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

            private static HttpResponseMessage Xml(string xml) => new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/rss+xml") };
        }
    }
}
