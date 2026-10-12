using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Integrations.Data.Newznab;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Sabnzbd;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class AcquisitionTests
    {
        private const string Connections = "/api/v1/admin/acquisition/connections";
        private const string Feature = "Example.Movie.2020.1080p.WEB-DL.H264-GRP";
        private const string Lower = "Example.Movie.2020.720p.WEB-DL.H264-GRP";

        [TestMethod]
        public async Task Movie_ManualSearchGrabMonitorAndImport_CompletesTheRequest()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var usenet = new FakeUsenet();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, usenet);
            var wantedId = await CreateWantedMovieAsync(host);

            // Keys are write-only and the test stores capabilities.
            using var list = await host.SendAsync(HttpMethod.Get, Connections);
            Assert.IsFalse((await list.Content.ReadAsStringAsync()).Contains(FakeUsenet.IndexerKey, StringComparison.Ordinal));
            using var tested = await host.SendAsync(HttpMethod.Post, Connections + "/1/test");
            var test = (await tested.Content.ReadFromJsonAsync<AcquisitionConnectionTestV1>())!;
            Assert.IsTrue(test.IsAvailable);
            CollectionAssert.Contains(test.SearchModes, "Movie");

            // Every candidate stays visible with its decision.
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search");
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode, await searched.Content.ReadAsStringAsync());
            var candidates = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.ToArray();
            Assert.AreEqual(Feature, candidates[0].Title);
            Assert.AreEqual(ReleaseDecisionType.Eligible, candidates[0].ReleaseDecisionTypeId);
            Assert.AreEqual("HigherQuality", candidates[0].ReasonCode);
            var wrong = candidates.Single(candidate => candidate.Title.StartsWith("Other.Movie", StringComparison.Ordinal));
            Assert.AreEqual(ReleaseDecisionType.Rejected, wrong.ReleaseDecisionTypeId);
            Assert.AreEqual("WrongTitle", wrong.ReasonCode);
            Assert.AreEqual("WrongYear", candidates.Single(candidate => candidate.Title.Contains("1999", StringComparison.Ordinal)).ReasonCode);

            using var grabbed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(candidates[0].CandidateId));
            Assert.AreEqual(HttpStatusCode.OK, grabbed.StatusCode, await grabbed.Content.ReadAsStringAsync());
            Assert.AreEqual("https://indexer.example.test/getnzb/feature", usenet.Grabbed.Single());
            Assert.AreEqual((short)4, await host.ScalarAsync("SELECT \"AcquisitionRequestStatusTypeId\" FROM \"AcquisitionRequests\""));
            using var repeated = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(candidates[1].CandidateId));
            Assert.AreEqual(HttpStatusCode.BadRequest, repeated.StatusCode);

            // Progress comes from the client queue.
            usenet.Queue = """{"queue":{"paused":false,"slots":[{"nzo_id":"SABnzbd_nzo_1","filename":"Example","percentage":"40"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);
            using var wantedRead = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/wanted/" + wantedId);
            var wanted = (await wantedRead.Content.ReadFromJsonAsync<WantedDetailsV1>())!;
            Assert.IsTrue(wanted.IsDownloading);
            Assert.AreEqual(40, wanted.ProgressPercent);

            // The completed job is mapped, linked into the library and assigned.
            var completed = Path.Combine(downloads.Path, Feature);
            Directory.CreateDirectory(completed);
            File.WriteAllText(Path.Combine(completed, Feature + ".mkv"), new string('x', 4096));
            File.WriteAllText(Path.Combine(completed, Feature + ".en.srt"), "subtitle");
            File.WriteAllText(Path.Combine(completed, "sample.mkv"), new string('x', 8192));
            usenet.Queue = FakeUsenet.EmptyQueue;
            usenet.History = $$$"""{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_1","name":"Example","status":"Completed","storage":"/downloads/{{{Feature}}}"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);

            var library = Path.Combine(storage.Path, "1", "Movies", "Example Movie (2020)");
            Assert.IsTrue(File.Exists(Path.Combine(library, "Example Movie (2020).mkv")));
            Assert.IsTrue(File.Exists(Path.Combine(library, "Example Movie (2020).en.srt")));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"MediaAssetId\" IS NOT NULL"));
            Assert.AreEqual((short)2, await host.ScalarAsync("SELECT \"AcquisitionReleaseStatusTypeId\" FROM \"AcquisitionReleases\""));
            Assert.AreEqual((short)6, await host.ScalarAsync("SELECT \"AcquisitionRequestStatusTypeId\" FROM \"AcquisitionRequests\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 1 AND \"OperationStatusTypeId\" = 1"));
            using var history = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/wanted/" + wantedId + "/releases");
            var release = (await history.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseV1>>())!.Single();
            Assert.AreEqual(AcquisitionReleaseStatusType.Imported, release.AcquisitionReleaseStatusTypeId);
            Assert.AreEqual(100, release.ProgressPercent);
        }

        [TestMethod]
        public async Task AutomaticSearch_GrabsTheBestRelease_AndSkipsItAfterAReleaseFault()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var usenet = new FakeUsenet();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, usenet);
            await CreateWantedMovieAsync(host);

            await RunAsync<SearchDueWantedV1>(host);
            Assert.AreEqual("https://indexer.example.test/getnzb/feature", usenet.Grabbed.Single());
            Assert.AreEqual("grabbed", await host.ScalarAsync("SELECT \"LastOutcomeKey\" FROM \"WantedItems\""));

            // A broken release is failed, never retried, and the next best one is taken.
            usenet.History = """{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_1","name":"Example","status":"Failed","fail_message":"Not enough articles"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.AreEqual("download_failed", await host.ScalarAsync("SELECT \"ReasonKey\" FROM \"AcquisitionReleases\""));
            await RunAsync<SearchDueWantedV1>(host);
            CollectionAssert.AreEqual(new[] { "https://indexer.example.test/getnzb/feature", "https://indexer.example.test/getnzb/lower" }, usenet.Grabbed);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionReleases\""));

            // Nothing new is searched while a download runs.
            await RunAsync<SearchDueWantedV1>(host);
            Assert.AreEqual(2, usenet.Grabbed.Count);
        }

        [TestMethod]
        public async Task Razor_WantedSearchAndConnections_RenderRealResults()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var usenet = new FakeUsenet();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, usenet, razor: true);
            var wantedId = await CreateWantedMovieAsync(host);

            using var connections = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition?AcquisitionConnectionId=2");
            var html = await connections.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, connections.StatusCode, html);
            StringAssert.Contains(html, "Path reported by the client");
            Assert.IsFalse(html.Contains(FakeUsenet.ClientKey, StringComparison.Ordinal));

            // The search form renders every candidate with its decision.
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Wanted?WantedItemId=" + wantedId + "&Tab=search");
            html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            using var searched = await host.SendAsync(HttpMethod.Post, "/Admin/Wanted?PageNumber=1&WantedItemId=" + wantedId + "&Tab=search&handler=Search",
                new Dictionary<string, string> { ["__RequestVerificationToken"] = LibraryTests.Token(html), ["WantedItemId"] = wantedId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            html = await searched.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode, html);
            StringAssert.Contains(html, Feature);
            StringAssert.Contains(html, "Different title");
            StringAssert.Contains(html, "Select and download");
        }

        [TestMethod]
        public async Task Connections_ValidateInputAndStayOwnerOnly()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);

            using var missingKey = await host.SendAsync(HttpMethod.Post, Connections,
                new SaveAcquisitionConnectionDataV1("Indexer", AcquisitionConnectionType.Newznab, "https://indexer.example.test", null, true, 10, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.BadRequest, missingKey.StatusCode);
            using var badUrl = await host.SendAsync(HttpMethod.Post, Connections,
                new SaveAcquisitionConnectionDataV1("Indexer", AcquisitionConnectionType.Newznab, "ftp://indexer", "key", true, 10, [], null, null, null));
            StringAssert.Contains(await badUrl.Content.ReadAsStringAsync(), "invalid_url");
            using var clientFields = await host.SendAsync(HttpMethod.Post, Connections,
                new SaveAcquisitionConnectionDataV1("Indexer", AcquisitionConnectionType.Newznab, "https://indexer.example.test", "key", true, 10, [], "movies", null, null));
            Assert.AreEqual(HttpStatusCode.BadRequest, clientFields.StatusCode);
            using var halfMapping = await host.SendAsync(HttpMethod.Post, Connections,
                new SaveAcquisitionConnectionDataV1("SAB", AcquisitionConnectionType.Sabnzbd, "https://sab.example.test", "key", true, 10, [], null, "/downloads", null));
            Assert.AreEqual(HttpStatusCode.BadRequest, halfMapping.StatusCode);

            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var denied = await host.SendAsync(HttpMethod.Get, Connections);
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        [TestMethod]
        public void Selection_NeverLetsAPreferenceRepairAWrongIdentity()
        {
            var profile = new QualityProfile("1", "Test", ["WEB-1080p", "WEB-720p"], ["WEB-1080p", "WEB-720p"], true, "WEB-1080p", null, null, [], [], [], [],
                [new ReleaseScoreRule("Group", ReleaseRuleField.ReleaseGroup, ReleaseRuleMatch.Equals, "GRP", 500)]);
            var offers = new[]
            {
                Offer("Other.Movie.2020.1080p.WEB-DL.H264-GRP", "wrong"),
                Offer("Example.Movie.2020.720p.WEB-DL.H264-OTHER", "right")
            };
            var result = ReleaseSelectionEngine.Select(profile, offers.Select(offer =>
            {
                var judgement = VideoReleaseJudge.Judge(AcquisitionKind.Movie, "Example Movie", 2020, null, VideoUnitScope.Empty, offer);
                return new SelectionCandidate(offer.Identity, judgement.Parsed, offer.SizeBytes, offer.ConnectionName, offer.ConnectionPriority, offer.PublishedAt, judgement.Evidence,
                    judgement.Coverage);
            }).ToArray());

            Assert.AreEqual("1:right", result.Winner!.Candidate.Id);
            Assert.AreEqual(SelectionOutcome.Usable, result.Outcome);
        }

        [TestMethod]
        public void Newznab_ParsesCapabilitiesAndKeepsUsenetLinksOnly()
        {
            var caps = NewznabClient.ParseCapabilities(XDocument.Parse(FakeUsenet.Capabilities), DateTimeOffset.UnixEpoch);
            Assert.IsTrue(caps.Supports(NewznabSearchMode.Movie, "imdbid"));
            Assert.IsTrue(caps.Offers(2040));
            Assert.IsFalse(caps.Offers(5000));

            var items = NewznabClient.ParseItems("https://indexer.example.test", XDocument.Parse("""
                <rss><channel>
                  <item><title>A.Release</title><guid>a</guid><enclosure url="/getnzb/a" length="10" /></item>
                  <item><title>B.Release</title><guid>b</guid><link>magnet:?xt=urn:btih:abc</link></item>
                </channel></rss>
                """));
            Assert.AreEqual("https://indexer.example.test/getnzb/a", items[0].DownloadUri!.ToString());
            Assert.IsNull(items[1].DownloadUri);
        }

        [TestMethod]
        public void Sabnzbd_FailuresOnlyBlameTheReleaseForReleaseFaults()
        {
            var history = SabnzbdClient.ParseHistoryResponse("""
                {"history":{"slots":[
                  {"nzo_id":"a","status":"Failed","fail_message":"Not enough articles"},
                  {"nzo_id":"b","status":"Failed","fail_message":"Disk full"},
                  {"nzo_id":"c","status":"Extracting"},
                  {"nzo_id":"d","status":"Completed","storage":"/downloads/d"}]}}
                """);
            Assert.IsTrue(SabnzbdFailureKinds.IsReleaseFault(history.Jobs[0].FailureKind));
            Assert.IsTrue(history.Jobs[1].IsFailed);
            Assert.IsFalse(SabnzbdFailureKinds.IsReleaseFault(history.Jobs[1].FailureKind));
            Assert.IsFalse(history.Jobs[2].IsFailed || history.Jobs[2].IsCompleted);
            Assert.AreEqual("/downloads/d", history.Jobs[3].StoragePath);
        }

        private static ReleaseOffer Offer(string title, string guid) =>
            new(title, 1, "Indexer", 25, guid, new Uri("https://indexer.example.test/" + guid), null, 2_000_000_000, DateTimeOffset.UtcNow, ["title"]);

        private static async Task<Host> CreateHostAsync(string storage, string downloads, FakeUsenet usenet, bool razor = false)
        {
            var host = await LibraryTests.CreateHostAsync(storage, razor, services: services =>
            {
                services.AddHttpClient<NewznabClient>().ConfigurePrimaryHttpMessageHandler(() => usenet);
                services.AddHttpClient<SabnzbdClient>().ConfigurePrimaryHttpMessageHandler(() => usenet);
            });

            // A writable movie library and one indexer plus one client.
            var mountId = await LibraryTests.CreateMountAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"IsReadOnly\" = FALSE");
            Directory.CreateDirectory(Path.Combine(storage, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Movies"));
            await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            foreach (var data in new[]
            {
                new SaveAcquisitionConnectionDataV1("Indexer", AcquisitionConnectionType.Newznab, "https://indexer.example.test/", FakeUsenet.IndexerKey, true, 10, [], null, null, null),
                new SaveAcquisitionConnectionDataV1("SAB", AcquisitionConnectionType.Sabnzbd, "https://sab.example.test", FakeUsenet.ClientKey, true, 10, [], "movies", "/downloads", downloads)
            })
            {
                using var created = await host.SendAsync(HttpMethod.Post, Connections, data);
                Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            }

            return host;
        }

        private static async Task<long> CreateWantedMovieAsync(Host host)
        {
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1(MediaType.Movie, "Example Movie", 2020));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var work = (await created.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            Assert.AreEqual(2020, work.ReleaseYear);
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "UiLocaleId", "AcquisitionRequestStatusTypeId", "DecidedByAccountId", "DecidedAt")
                VALUES (500, {work.Id}, 202, 1, 2, 41, now());
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (600, {work.Id});
                INSERT INTO "RequestTargets" ("AcquisitionRequestId", "WantedItemId") VALUES (500, 600);
                """);
            return 600;
        }

        private static async Task RunAsync<TService>(Host host) where TService : Service.Core.SystemService<AcquisitionCycleParametersV1, NoData>
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TService>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default);
        }

        private sealed class FakeUsenet : HttpMessageHandler
        {
            public const string IndexerKey = "indexer-secret-key";
            public const string ClientKey = "client-secret-key";
            public const string EmptyQueue = """{"queue":{"paused":false,"slots":[]}}""";
            public const string Capabilities = """
                <caps>
                  <server title="Fake" version="1.0" />
                  <limits max="100" default="100" />
                  <searching>
                    <search available="yes" supportedParams="q" />
                    <movie-search available="yes" supportedParams="q,imdbid" />
                  </searching>
                  <categories><category id="2000" name="Movies"><subcat id="2040" name="HD" /></category></categories>
                </caps>
                """;

            public List<string> Grabbed { get; } = [];
            public string Queue { get; set; } = EmptyQueue;
            public string History { get; set; } = """{"history":{"slots":[]}}""";

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri!.Host == "indexer.example.test")
                {
                    var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                    if (query["apikey"] != IndexerKey) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    return Xml(query["t"] == "caps" ? Capabilities : Results);
                }

                // SABnzbd receives form posts.
                var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (form["apikey"] != ClientKey) return Json("""{"status":false,"error":"API Key Incorrect"}""");
                switch (form["mode"])
                {
                    case "addurl":
                        Grabbed.Add(form["name"]!);
                        return Json($$"""{"status":true,"nzo_ids":["SABnzbd_nzo_{{Grabbed.Count}}"]}""");
                    case "queue":
                        return Json(Queue);
                    case "history":
                        return Json(History);
                    default:
                        return Json("""{"version":"4.5.0"}""");
                }
            }

            private static string Results => $"""
                <rss><channel>
                  {Item(Lower, "lower", 1_000_000_000)}
                  {Item(Feature, "feature", 2_000_000_000)}
                  {Item("Other.Movie.2020.1080p.WEB-DL.H264-GRP", "other", 2_000_000_000)}
                  {Item("Example.Movie.1999.1080p.WEB-DL.H264-GRP", "old", 2_000_000_000)}
                </channel></rss>
                """;

            private static string Item(string title, string guid, long size) =>
                $"""<item><title>{title}</title><guid>{guid}</guid><pubDate>Mon, 05 Oct 2026 10:00:00 +0000</pubDate><enclosure url="/getnzb/{guid}" length="{size}" /></item>""";

            private static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

            private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
