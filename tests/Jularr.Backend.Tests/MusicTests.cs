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
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Integrations.Logic.MusicBrainz;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Sabnzbd;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class MusicTests
    {
        private const string Connections = "/api/v1/admin/acquisition/connections";
        private const string Album = "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b";

        [TestMethod]
        public async Task Album_PlaysItsPresentTracksInOrderAndLeadsToTheArtist()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (10, 6, 'Album'), (20, 6, 'Other Album');
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") VALUES (10, 'Artist', 'Artist A & Artist B', TRUE), (20, 'Artist', 'artist a', TRUE);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number", "Title") VALUES (101, 10, 5, 1, 'One'), (102, 10, 5, 2, 'Two'), (103, 10, 5, 3, 'Three');
                INSERT INTO "WorkEditions" ("Id", "WorkId") VALUES (1, 10);
                INSERT INTO "WorkVersions" ("Id", "WorkId", "WorkEditionId") VALUES (1, 10, 1);
                INSERT INTO "MediaAssets" ("Id", "WorkId", "WorkVersionId", "WorkUnitId", "MediaAssetTypeId") VALUES (1, 10, 1, 101, 2), (2, 10, 1, 102, 2), (3, 10, 1, 103, 2);
                """);
            var mountId = await LibraryTests.CreateMountAsync(host);
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Music", MediaType.Music);
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId", "MissingSince")
                VALUES ({rootId}, 'Album/01.flac', 1, now(), now(), 1, NULL), ({rootId}, 'Album/02.flac', 1, now(), now(), 2, now()), ({rootId}, 'Album/03.flac', 1, now(), now(), 3, NULL);
                """);

            // A missing track is skipped; the first and last have one neighbour.
            using var first = await host.SendAsync(HttpMethod.Get, "/api/v1/playback/assets/1");
            var opening = (await first.Content.ReadFromJsonAsync<Jularr.Backend.Data.User.Playback.V1.PlaybackItemV1>())!;
            Assert.AreEqual("One", opening.UnitTitle);
            Assert.IsNull(opening.PreviousMediaAssetId);
            Assert.AreEqual(3L, opening.NextMediaAssetId);
            using var last = await host.SendAsync(HttpMethod.Get, "/api/v1/playback/assets/3");
            var closing = (await last.Content.ReadFromJsonAsync<Jularr.Backend.Data.User.Playback.V1.PlaybackItemV1>())!;
            Assert.AreEqual(1L, closing.PreviousMediaAssetId);
            Assert.IsNull(closing.NextMediaAssetId);
            using var player = await host.SendAsync(HttpMethod.Get, "/Player/1");
            var html = await player.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, player.StatusCode, html);
            StringAssert.Contains(html, "data-href=\"/Player/3\"");
            StringAssert.Contains(html, "Track 1 &#xB7; One");

            // Each artist leads to everything of theirs the profile may see.
            using var details = await host.SendAsync(HttpMethod.Get, "/Details/10");
            var page = await details.Content.ReadAsStringAsync();
            StringAssert.Contains(page, "href=\"/Creator?name=Artist%20B\"");
            using var creator = await host.SendAsync(HttpMethod.Get, "/api/v1/creators/works?name=ARTIST%20A");
            CollectionAssert.AreEquivalent(new[] { 10L, 20L }, (await creator.Content.ReadFromJsonAsync<PageResult<Jularr.Backend.Data.User.Media.V1.WorkCardV1>>())!.Items.Select(item => item.Id).ToArray());
            using var creatorPage = await host.SendAsync(HttpMethod.Get, "/Creator?name=Artist%20A");
            Assert.AreEqual(HttpStatusCode.OK, creatorPage.StatusCode);
            StringAssert.Contains(await creatorPage.Content.ReadAsStringAsync(), "Other Album");
        }

        [TestMethod]
        public async Task Music_MusicBrainzAlbumSearchAndImport_PlacesTracksOnTheirUnits()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeMusicSources();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);

            // MusicBrainz names the album, its artist and the tracks of its first edition.
            using var found = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/metadata/search", new SearchMetadataDataV1(MediaType.Music, "Example Album"));
            Assert.AreEqual(HttpStatusCode.OK, found.StatusCode, await found.Content.ReadAsStringAsync());
            var result = (await found.Content.ReadFromJsonAsync<BaseDataList<MetadataSearchResultV1>>())!.Single();
            Assert.AreEqual("Example Artist", result.Author);
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Music, result.ExternalId));
            Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
            var work = (await imported.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT count(*) FROM \"WorkUnits\" WHERE \"WorkUnitTypeId\" = 5"));
            Assert.AreEqual("Example Artist", await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Artist'"));
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId", "AcquisitionRequestStatusTypeId", "DecidedByAccountId", "DecidedAt")
                VALUES (700, {work.Id}, 202, 42, 1, 2, 41, now());
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (800, {work.Id});
                INSERT INTO "RequestTargets" ("AcquisitionRequestId", "WantedItemId") VALUES (700, 800);
                """);

            // Artist and album decide; another edition waits for a person, the best format wins.
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/800/search", new SearchWantedDataV1());
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode, await searched.Content.ReadAsStringAsync());
            var candidates = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.ToArray();
            Assert.AreEqual("FLAC", candidates.Single(candidate => candidate.Title == "Example Artist - Example Album (2019) [FLAC]").QualityKey);
            Assert.AreEqual("OtherEdition", candidates.Single(candidate => candidate.Title.Contains("Live", StringComparison.Ordinal)).ReasonCode);
            Assert.AreEqual(ReleaseDecisionType.Rejected, candidates.Single(candidate => candidate.Title.StartsWith("Other Artist", StringComparison.Ordinal)).ReleaseDecisionTypeId);
            await RunAsync<SearchDueWantedV1>(host);
            CollectionAssert.AreEqual(new[] { "https://indexer.example.test/getnzb/flac" }, sources.Grabbed);

            // Tracks keep the release's order and land on their units.
            var job = Directory.CreateDirectory(Path.Combine(downloads.Path, "Example Artist - Example Album")).FullName;
            foreach (var (name, size) in new[] { ("01 - Opening.flac", 3000), ("02. Middle Song.flac", 3100), ("03_Closing.flac", 3200), ("cover.jpg", 100) })
            {
                File.WriteAllBytes(Path.Combine(job, name), new byte[size]);
            }

            sources.History = """{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_1","name":"Album","status":"Completed","storage":"/downloads/Example Artist - Example Album"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);
            var folder = Path.Combine(storage.Path, "1", "Music", "Example Artist", "Example Album (2019)");
            CollectionAssert.AreEquivalent(new[] { "01 - Opening.flac", "02 - Middle Song.flac", "03 - Closing.flac" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT count(*) FROM \"MediaAssets\" WHERE \"WorkUnitId\" IS NOT NULL AND \"MediaAssetTypeId\" = 2"));
            Assert.AreEqual((short)6, await host.ScalarAsync("SELECT \"AcquisitionRequestStatusTypeId\" FROM \"AcquisitionRequests\""));
        }

        [TestMethod]
        [DataRow("Metallica", "Metallica", "Metallica - Metallica (1991) [FLAC]", "Matches")]
        [DataRow("Metallica", "Metallica", "Metallica - Load (1996) [FLAC]", "WrongTitle")]
        [DataRow("Led Zeppelin", "Led Zeppelin", "Led_Zeppelin-Led_Zeppelin_III-REMASTERED-WEB-FLAC-2014-GRP", "AmbiguousTitle")]
        [DataRow("Now Hits", "Various Artists", "VA - Now Hits (2020) [MP3 320]", "Matches")]
        [DataRow("Don't Stop", "Beyoncé", "Beyonce-Dont_Stop-WEB-2019-GRP", "Matches")]
        [DataRow("Back in Black", "AC/DC", "ACDC - Back in Black (1980) [FLAC]", "Matches")]
        [DataRow("The Wall", "Pink Floyd", "Pink Floyd - The Wall (1979) [FLAC]", "Matches")]
        [DataRow("Alive", "Live", "Live - Alive (1999) [FLAC]", "Matches")]
        public void Music_ArtistAndAlbum_AreSeparateRuns(string album, string artist, string release, string expected) =>
            Assert.AreEqual(expected, MusicReleaseJudge.Judge([album], artist,
                new ReleaseOffer(release, 1, "Indexer", 25, "guid", new Uri("https://indexer.example.test/guid"), null, 1_000_000, DateTimeOffset.UtcNow, ["title"])).Match.ToString());

        [TestMethod]
        public void Music_Placement_KeepsNaturalOrderAndTiesTracksOnlyWhenCountsAgree()
        {
            var files = new[] { "10 - Ten.flac", "2 - Two.flac", "1 - One.flac" }.Select(name => new Jularr.Integrations.Data.Storage.StorageFile(name, 1000, DateTime.UtcNow)).ToArray();
            var tied = ImportPlacementLogic.Plan(MediaType.Music, null, "Album", 2019, "FLAC", files, "Artist", 3);
            CollectionAssert.AreEqual(new[] { "Artist/Album (2019)/01 - One", "Artist/Album (2019)/02 - Two", "Artist/Album (2019)/03 - Ten" }, tied.Select(placement => placement.TargetStem).ToArray());
            Assert.IsTrue(tied.All(placement => placement.WorkUnitTypeId == WorkUnitType.Track));
            Assert.IsTrue(ImportPlacementLogic.Plan(MediaType.Music, null, "Album", 2019, "FLAC", files, "Artist", 4).All(placement => placement.WorkUnitTypeId is null));
        }

        [TestMethod]
        [DataRow("Example Artist - Example Album (2019) [FLAC 24bit]", "FLAC-24")]
        [DataRow("Example Artist - Example Album (2019) [FLAC 24-96]", "FLAC-24")]
        [DataRow("Example Artist - Example Album (2019) [Hi-Res FLAC]", "FLAC-24")]
        [DataRow("Example Artist - Example Album (2019) MP3 320kbps", "MP3-320")]
        [DataRow("Example_Artist-Example_Album-WEB-2019-GRP", "AUDIO")]
        [DataRow("Example Artist - Example Album (2019) MP3 320", "MP3-320")]
        [DataRow("Example Artist - Example Album (2019) [MP3 V0]", "MP3-V0")]
        public void Music_ReleaseNames_NameTheirFormat(string title, string quality) =>
            Assert.AreEqual(quality, ReleaseQuality.GetKey(MusicReleaseJudge.Parse(title)));

        private static async Task<Host> CreateHostAsync(string storage, string downloads, FakeMusicSources sources)
        {
            var host = await LibraryTests.CreateHostAsync(storage, services: services =>
            {
                services.AddHttpClient<MusicBrainzClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
                services.AddHttpClient<NewznabClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
                services.AddHttpClient<SabnzbdClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
            });

            // A writable music library, one indexer and one client.
            var mountId = await LibraryTests.CreateMountAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"IsReadOnly\" = FALSE");
            Directory.CreateDirectory(Path.Combine(storage, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Music"));
            await LibraryTests.CreateRootAsync(host, mountId, "Music", MediaType.Music);
            foreach (var data in new[]
            {
                new SaveAcquisitionConnectionDataV1("Indexer", AcquisitionConnectionType.Newznab, "https://indexer.example.test/", "indexer-key", true, 10, [3040], null, null, null),
                new SaveAcquisitionConnectionDataV1("SAB", AcquisitionConnectionType.Sabnzbd, "https://sab.example.test", "client-key", true, 10, [], "music", "/downloads", downloads)
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

        private sealed class FakeMusicSources : HttpMessageHandler
        {
            private const string Group = $$"""{"id":"{{Album}}","title":"Example Album","primary-type":"Album","first-release-date":"2019-05-03","artist-credit":[{"name":"Example Artist","joinphrase":""}]}""";

            public List<string> Grabbed { get; } = [];
            public string History { get; set; } = """{"history":{"slots":[]}}""";

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                switch (uri.Host)
                {
                    case "musicbrainz.org" when uri.AbsolutePath == "/ws/2/release-group":
                        return Json("{\"release-groups\":[" + Group + "]}");
                    case "musicbrainz.org" when uri.AbsolutePath == "/ws/2/release-group/" + Album:
                        return Json(Group);
                    case "musicbrainz.org" when uri.AbsolutePath == "/ws/2/release":
                        return Json("""{"releases":[{"id":"bbbbbbbb-0000-0000-0000-000000000002","date":"2020-01-01"},{"id":"aaaaaaaa-0000-0000-0000-000000000001","date":"2019-05-03"}]}""");
                    case "musicbrainz.org" when uri.AbsolutePath == "/ws/2/release/aaaaaaaa-0000-0000-0000-000000000001":
                        return Json("""{"media":[{"position":2,"tracks":[{"title":"Closing"}]},{"position":1,"tracks":[{"title":"Opening"},{"title":"Middle Song"}]}]}""");
                    case "indexer.example.test" when uri.Query.Contains("t=caps", StringComparison.Ordinal):
                        return Xml("""
                            <caps><limits max="100" default="100" /><searching><search available="yes" supportedParams="q" /></searching>
                              <categories><category id="3000" name="Audio"><subcat id="3040" name="Lossless" /></category></categories></caps>
                            """);
                    case "indexer.example.test":
                        return Xml("""
                            <rss><channel>
                              <item><title>Example Artist - Example Album (2019) [FLAC]</title><guid>flac</guid><link>https://indexer.example.test/getnzb/flac</link><size>300000000</size></item>
                              <item><title>Example Artist - Example Album (2019) MP3 320</title><guid>mp3</guid><link>https://indexer.example.test/getnzb/mp3</link><size>100000000</size></item>
                              <item><title>Example Artist - Example Album Live (2020) [FLAC]</title><guid>live</guid><link>https://indexer.example.test/getnzb/live</link><size>300000000</size></item>
                              <item><title>Other Artist - Example Album (2019) [FLAC]</title><guid>other</guid><link>https://indexer.example.test/getnzb/other</link><size>300000000</size></item>
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
