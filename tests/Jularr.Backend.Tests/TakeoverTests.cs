using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Media.V1;
using Jularr.Integrations.Logic.Servarr;
using Jularr.Integrations.Logic.Tmdb;
using Jularr.Integrations.Logic.Tvdb;
using Jularr.Integrations.Logic.OpenLibrary;
using Jularr.Integrations.Logic.MusicBrainz;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class TakeoverTests
    {
        private const string Takeover = "/api/v1/admin/acquisition/takeover";
        private const string RadarrKey = "radarr-secret-key";
        private const string TmdbToken = "eyJ-takeover-token";

        [TestMethod]
        public async Task Takeover_RadarrPreviewThenApply_ImportsInventoryIntoTheLibraryWithRadarrsMonitoring()
        {
            var radarr = new FakeRadarr();
            await using var host = await Host.CreateAsync(razor: true, library: true, services: services =>
            {
                services.AddHttpClient<ServarrApiClient>().ConfigurePrimaryHttpMessageHandler(() => radarr);
                services.AddHttpClient<TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => radarr);
            });
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            var tmdb = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'tmdb'"))!;
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + tmdb + "/secret", new SaveProviderSecretDataV1(TmdbToken));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());

            // A wrong key is refused without touching anything.
            using var refused = await host.SendAsync(HttpMethod.Post, Takeover, new RunTakeoverDataV1(TakeoverSourceType.Radarr, "http://radarr.example.test:7878", "wrong", false));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

            // The preview shows what would happen and changes nothing.
            var preview = await RunAsync(host, false);
            Assert.AreEqual("takeover_will_add", preview.Single(item => item.Name == "Readable").OutcomeKey);
            Assert.AreEqual("takeover_needs_key", preview.Single(item => item.Name == "Hidden").OutcomeKey);
            Assert.AreEqual("takeover_will_add", preview.Single(item => item.Name == "Open").OutcomeKey);
            Assert.AreEqual("https://hidden.example.test", preview.Single(item => item.Name == "Hidden").SuggestedUrl);
            Assert.AreEqual("takeover_needs_key", preview.Single(item => item.TakeoverItemTypeId == TakeoverItemType.Client).OutcomeKey);
            Assert.AreEqual("takeover_will_want", preview.Single(item => item.Name == "The Matrix (1999)").OutcomeKey);
            Assert.AreEqual("takeover_no_id", preview.Single(item => item.Name == "Unknown (2001)").OutcomeKey);
            Assert.AreEqual("takeover_will_add", preview.Single(item => item.Name == "Owned (2000)").OutcomeKey);
            Assert.AreEqual("takeover_will_add", preview.Single(item => item.Name == "Ignored (2000)").OutcomeKey);
            Assert.IsFalse(preview.Any(item => item.Name == "Tracker"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionConnections\""));

            // Taking over adds the readable and the open indexer and wants the missing movie; a second run only repeats nothing.
            var applied = await RunAsync(host, true);
            Assert.AreEqual("takeover_added", applied.Single(item => item.Name == "Readable").OutcomeKey);
            Assert.AreEqual("takeover_added", applied.Single(item => item.Name == "Open").OutcomeKey);
            Assert.AreEqual("takeover_wanted", applied.Single(item => item.Name == "The Matrix (1999)").OutcomeKey);
            Assert.AreEqual("takeover_added", applied.Single(item => item.Name == "Owned (2000)").OutcomeKey);
            Assert.AreEqual("takeover_added", applied.Single(item => item.Name == "Ignored (2000)").OutcomeKey);
            Assert.AreEqual(3L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));
            Assert.AreEqual("https://readable.example.test", await host.ScalarAsync("SELECT \"BaseUrl\" FROM \"AcquisitionConnections\" WHERE \"Name\" = 'Readable'"));
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"ProtectedApiKey\" IS NULL FROM \"AcquisitionConnections\" WHERE \"Name\" = 'Open'"));
            // Every title taken over is in the library at once, monitored as Radarr monitored it.
            Assert.AreEqual("Ignored:true,Owned:false,The Matrix:false", await host.ScalarAsync("""
                SELECT string_agg(work."CanonicalTitle" || ':' || wanted."IsPaused", ',' ORDER BY work."CanonicalTitle")
                FROM "WantedItems" AS wanted JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                """));
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "Works" AS work WHERE NOT EXISTS (SELECT 1 FROM "WorkLibraryRelations" AS tie WHERE tie."WorkId" = work."Id")"""));
            var again = await RunAsync(host, true);
            Assert.AreEqual("takeover_exists", again.Single(item => item.Name == "Readable").OutcomeKey);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionConnections\""));
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT count(*) FROM \"WantedItems\""));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionReleases\""));
            var signal = host.Services.GetRequiredService<AcquisitionSearchSignal>();
            Assert.IsFalse(signal.TakeRequest(), "Taking over inventory leaves the search to its next cycle.");

            // Releasing an unmonitored title uses the existing target editor; a repeated takeover must preserve that choice.
            var wantedId = (long)(await host.ScalarAsync("""SELECT wanted."Id" FROM "WantedItems" AS wanted JOIN "Works" AS work ON work."Id" = wanted."WorkId" WHERE work."CanonicalTitle" = 'Ignored'"""))!;
            await host.Fixture.RunSqlAsync("UPDATE \"WantedItems\" SET \"NextSearchAt\" = now() + interval '1 day' WHERE \"Id\" = " + wantedId);
            using var released = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/" + wantedId, new UpdateWantedDataV1(false, 1));
            Assert.AreEqual(HttpStatusCode.OK, released.StatusCode, await released.Content.ReadAsStringAsync());
            Assert.IsTrue(signal.TakeRequest(), "Resuming a paused takeover must wake automatic search after commit.");
            Assert.IsFalse(signal.TakeRequest(), "A resume must only enqueue one wake-up.");
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"NextSearchAt\" FROM \"WantedItems\" WHERE \"Id\" = " + wantedId),
                "Resuming must clear the previous search backoff.");
            using var unchanged = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/" + wantedId, new UpdateWantedDataV1(false, 2));
            Assert.AreEqual(HttpStatusCode.OK, unchanged.StatusCode);
            Assert.IsFalse(signal.TakeRequest(), "Editing an already resumed target must not wake automatic search.");
            using var stale = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/" + wantedId, new UpdateWantedDataV1(false, 2));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            Assert.IsFalse(signal.TakeRequest(), "A stale update must not wake automatic search.");
            await RunAsync(host, true);
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsPaused\" FROM \"WantedItems\" WHERE \"Id\" = " + wantedId));

            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Takeover");
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, await page.Content.ReadAsStringAsync());

            // Hidden source keys are completed inline and protected at rest; they never return in the result.
            using var keyed = await host.SendAsync(HttpMethod.Post, Takeover, new RunTakeoverDataV1(TakeoverSourceType.Radarr, "http://radarr.example.test:7878", RadarrKey, true,
                connectionKeys: new([
                    new TakeoverConnectionKeyV1(TakeoverItemType.Indexer, "https://hidden.example.test", "inline-indexer-secret"),
                    new TakeoverConnectionKeyV1(TakeoverItemType.Client, "http://sab.example.test:8080", "inline-client-secret")
                ])));
            Assert.AreEqual(HttpStatusCode.OK, keyed.StatusCode, await keyed.Content.ReadAsStringAsync());
            Assert.IsFalse((await keyed.Content.ReadAsStringAsync()).Contains("inline-", StringComparison.Ordinal));
            Assert.AreEqual(4L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionConnections\""));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionConnections\" WHERE \"Name\" IN ('Hidden', 'SAB') AND \"ProtectedApiKey\" IS NOT NULL"));
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"AcquisitionConnections\" WHERE \"Name\" = 'SAB'"),
                "A taken-over SABnzbd client has no verified import mapping and must require explicit activation.");
        }

        [TestMethod]
        public async Task Takeover_ProwlarrSonarrLidarrAndReadarr_TakeConnectionsAndWantTheirMissingTitles()
        {
            var arr = new FakeArrs();
            var music = new MusicTests.FakeMusicSources();
            var books = new BookTests.FakeBookSources();
            await using var host = await Host.CreateAsync(library: true, services: services =>
            {
                services.AddHttpClient<ServarrApiClient>().ConfigurePrimaryHttpMessageHandler(() => arr);
                services.AddHttpClient<TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => arr);
                services.AddHttpClient<TvdbClient>().ConfigurePrimaryHttpMessageHandler(() => arr);
                services.AddHttpClient<MusicBrainzClient>().ConfigurePrimaryHttpMessageHandler(() => music);
                services.AddHttpClient<OpenLibraryClient>().ConfigurePrimaryHttpMessageHandler(() => books);
            });
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            await SaveSeriesKeysAsync(host);

            // Prowlarr hands over its indexers only.
            var prowlarr = await RunAsync(host, true, TakeoverSourceType.Prowlarr, "http://prowlarr.example.test:9696");
            Assert.AreEqual("takeover_added", prowlarr.Single().OutcomeKey);
            Assert.AreEqual(TakeoverItemType.Indexer, prowlarr.Single().TakeoverItemTypeId);

            // Sonarr: its client keeps the TV category, the missing series becomes wanted.
            var sonarr = await RunAsync(host, true, TakeoverSourceType.Sonarr, "http://sonarr.example.test:8989");
            Assert.AreEqual("takeover_added", sonarr.Single(item => item.TakeoverItemTypeId == TakeoverItemType.Client).OutcomeKey);
            Assert.AreEqual("tv", await host.ScalarAsync("SELECT \"DownloadCategory\" FROM \"AcquisitionConnections\" WHERE \"AcquisitionConnectionTypeId\" = 2"));
            Assert.AreEqual("takeover_wanted", sonarr.Single(item => item.Name == "Example Show (2020)").OutcomeKey);

            // Its profile keeps Sonarr's qualities by their names, best first, stops at the best of the cutoff group and gets its custom formats as rules; the title uses it.
            Assert.AreEqual("takeover_added", sonarr.Single(item => item.TakeoverItemTypeId == TakeoverItemType.Profile).OutcomeKey);
            var foundFormats = sonarr.Where(item => item.TakeoverItemTypeId == TakeoverItemType.Format).ToArray();
            Assert.IsTrue(foundFormats.Length > 0 && foundFormats.All(item => item.OutcomeKey == "takeover_found"));
            var profile = (await (await host.SendAsync(HttpMethod.Get, "/api/v1/admin/acquisition/profiles")).Content.ReadFromJsonAsync<BaseDataList<AcquisitionProfileDetailsV1>>())!
                .Single(item => item.Name == "HD-1080p");
            CollectionAssert.AreEqual(new[] { "DVD", "Bluray-1080p", "group:1002", "HDTV-1080p" }, profile.AllowedQualities);
            Assert.AreEqual(("group:1002", MediaType.Series), (profile.UpgradeCutoffQuality, profile.MediaTypeId));
            Assert.AreEqual("WEB 1080p", profile.QualityGroups.Single().Name);
            using var formatsResponse = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/acquisition/custom-formats?mediaTypeId=2");
            var formats = (await formatsResponse.Content.ReadFromJsonAsync<PageResult<CustomFormatDetailsV1>>())!.Items;
            var x265 = formats.Single(item => item.Name == "x265");
            Assert.AreEqual("[xh]\\.?265", x265.Conditions.Single().Value);
            Assert.AreEqual(-10000, profile.FormatScores.Single(item => item.FormatId == x265.Id).Score);
            Assert.AreEqual(1750, profile.FormatScores.Single(item => item.FormatId == formats.Single(format => format.Name == "Good groups").Id).Score);
            Assert.AreEqual(2, formats.Single(item => item.Name == "Mixed").Conditions.Count);
            Assert.AreEqual(profile.Id, await host.ScalarAsync("SELECT wanted.\"AcquisitionProfileId\" FROM \"WantedItems\" AS wanted JOIN \"Works\" AS work ON work.\"Id\" = wanted.\"WorkId\" WHERE work.\"MediaTypeId\" = 2"));

            // A second run finds the profile by its name.
            var again = await RunAsync(host, false, TakeoverSourceType.Sonarr, "http://sonarr.example.test:8989");
            Assert.AreEqual("takeover_exists", again.Single(item => item.TakeoverItemTypeId == TakeoverItemType.Profile).OutcomeKey);

            // Lidarr's root folders are offered every music library; the one named like the folder is suggested.
            var mountId = await host.ScalarAsync("""INSERT INTO "StorageMounts" ("Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly") VALUES ('NAS', 3, '/srv/media', FALSE) RETURNING "Id" """);
            await host.Fixture.RunSqlAsync("INSERT INTO \"LibraryRoots\" (\"Name\", \"MediaTypeId\", \"StorageMountId\", \"RelativePath\") VALUES ('Music', 6, " + mountId + ", 'music'), ('Classical', 6, "
                + mountId + ", 'classical')");
            var musicLibrary = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"LibraryRoots\" WHERE \"Name\" = 'Music'"))!;
            var folders = (await RunAsync(host, false, TakeoverSourceType.Lidarr, "http://lidarr.example.test:8686")).Where(item => item.TakeoverItemTypeId == TakeoverItemType.RootFolder).ToArray();
            Assert.AreEqual(4, folders.Length);
            CollectionAssert.AreEquivalent(new[] { "/data/music/ Music", "/data/classical Classical" },
                folders.Where(item => item.OutcomeKey == "takeover_suggested").Select(item => item.Name + " " + item.LibraryName).ToArray());

            // Lidarr: an album with its MusicBrainz release group is wanted in the chosen library, one without has no id, a single its metadata profile leaves out is skipped.
            var lidarr = await RunAsync(host, true, TakeoverSourceType.Lidarr, "http://lidarr.example.test:8686", [musicLibrary + ":/data/music/"]);
            Assert.AreEqual("takeover_wanted", lidarr.Single(item => item.Name == "Example Album (2019)").OutcomeKey);
            Assert.AreEqual("takeover_no_id", lidarr.Single(item => item.Name == "Bootleg (2020)").OutcomeKey);
            Assert.AreEqual("takeover_not_monitored", lidarr.Single(item => item.Name == "Some Single (2021)").OutcomeKey);
            Assert.AreEqual("takeover_assigned", lidarr.Single(item => item.Name == "/data/music/" && item.LibraryRootId == musicLibrary).OutcomeKey);
            Assert.AreEqual(musicLibrary, await host.ScalarAsync("SELECT wanted.\"LibraryRootId\" FROM \"WantedItems\" AS wanted JOIN \"Works\" AS work ON work.\"Id\" = wanted.\"WorkId\" WHERE work.\"MediaTypeId\" = 6"));

            // Its profile keeps the lossless qualities by name and the metadata profile its artists follow as monitoring; the album uses it.
            var lossless = (await (await host.SendAsync(HttpMethod.Get, "/api/v1/admin/acquisition/profiles")).Content.ReadFromJsonAsync<BaseDataList<AcquisitionProfileDetailsV1>>())!
                .Single(item => item.Name == "Lossless");
            CollectionAssert.AreEqual(new[] { "FLAC-24", "FLAC" }, lossless.AllowedQualities);
            Assert.AreEqual(("FLAC", MediaType.Music), (lossless.UpgradeCutoffQuality, lossless.MediaTypeId));
            using var losslessDetails = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/acquisition/profiles/" + lossless.Id);
            var monitoring = (await losslessDetails.Content.ReadFromJsonAsync<AcquisitionProfileDetailsV1>())!;
            CollectionAssert.AreEqual(new[] { "Album", "Studio", "Official" }, monitoring.MonitoredTypes);
            Assert.AreEqual(lossless.Id, await host.ScalarAsync("SELECT wanted.\"AcquisitionProfileId\" FROM \"WantedItems\" AS wanted JOIN \"Works\" AS work ON work.\"Id\" = wanted.\"WorkId\" WHERE work.\"MediaTypeId\" = 6"));

            // Readarr: the book found once in Open Library by title, author and year is wanted; an unclear one waits for a person.
            var readarr = await RunAsync(host, true, TakeoverSourceType.Readarr, "http://readarr.example.test:8787");
            Assert.AreEqual("takeover_wanted", readarr.Single(item => item.Name == "Pride and Prejudice (1813)").OutcomeKey);
            Assert.AreEqual("takeover_no_match", readarr.Single(item => item.Name == "Unknown Book (2001)").OutcomeKey);
            Assert.AreEqual("takeover_no_library", readarr.Single(item => item.TakeoverItemTypeId == TakeoverItemType.RootFolder).OutcomeKey);
            using var ebookDetails = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/acquisition/profiles/" + await host.ScalarAsync("SELECT \"Id\" FROM \"AcquisitionProfiles\" WHERE \"Name\" = 'eBook'"));
            var ebook = (await ebookDetails.Content.ReadFromJsonAsync<AcquisitionProfileDetailsV1>())!;
            CollectionAssert.AreEqual(new[] { "MOBI", "EPUB" }, ebook.AllowedQualities);
            Assert.AreEqual(("en de", 100), (string.Join(" ", ebook.MonitoredLanguages), ebook.MinimumPages));
            Assert.AreEqual("movie:0 series:1 music:1 book:1", await host.ScalarAsync("""
                SELECT 'movie:' || count(*) FILTER (WHERE work."MediaTypeId" = 1) || ' series:' || count(*) FILTER (WHERE work."MediaTypeId" = 2)
                    || ' music:' || count(*) FILTER (WHERE work."MediaTypeId" = 6) || ' book:' || count(*) FILTER (WHERE work."MediaTypeId" = 3)
                FROM "WantedItems" AS wanted JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                """));
        }

        private static async Task<IReadOnlyList<TakeoverItemV1>> RunAsync(Host host, bool apply, TakeoverSourceType source = TakeoverSourceType.Radarr, string address = "http://radarr.example.test:7878",
            string[]? rootFolders = null)
        {
            using var response = await host.SendAsync(HttpMethod.Post, Takeover, new RunTakeoverDataV1(source, address, RadarrKey, apply, rootFolders));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<BaseDataList<TakeoverItemV1>>())!;
        }

        [TestMethod]
        public async Task Sonarr_WithoutTheTvdbKey_TakesTheSeriesWithItsIds_AndItsDetailsFollowOnceTheKeyIsThere()
        {
            var arr = new FakeArrs();
            await using var host = await Host.CreateAsync(library: true, services: services =>
            {
                services.AddHttpClient<ServarrApiClient>().ConfigurePrimaryHttpMessageHandler(() => arr);
                services.AddHttpClient<TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => arr);
                services.AddHttpClient<TvdbClient>().ConfigurePrimaryHttpMessageHandler(() => arr);
            });
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            await SaveSeriesKeysAsync(host, "tmdb");

            // Without TheTVDB's key the missing series is still wanted, as TheTVDB's series with Sonarr's TheTVDB, TMDB and IMDb ids and no details yet.
            var sonarr = await RunAsync(host, true, TakeoverSourceType.Sonarr, "http://sonarr.example.test:8989");
            Assert.AreEqual("takeover_wanted", sonarr.Single(item => item.Name == "Example Show (2020)").OutcomeKey);
            var workId = (long)(await host.ScalarAsync("SELECT \"WorkId\" FROM \"WantedItems\""))!;
            Assert.AreEqual("imdb:tt0081189 tmdb_tv:1399 tvdb:81189", await host.ScalarAsync($"""
                SELECT string_agg(identity."Namespace" || ':' || identity."Value", ' ' ORDER BY identity."Namespace") FROM "WorkExternalIdentities" AS identity WHERE identity."WorkId" = {workId}
                """));
            Assert.AreEqual("Example Show 2020", await host.ScalarAsync($"SELECT \"CanonicalTitle\" || ' ' || \"ReleaseYear\" FROM \"Works\" WHERE \"Id\" = {workId} AND \"DetailedAt\" IS NULL"));

            // A second run finds it by its ids.
            var again = await RunAsync(host, true, TakeoverSourceType.Sonarr, "http://sonarr.example.test:8989");
            Assert.AreEqual("takeover_exists", again.Single(item => item.Name == "Example Show (2020)").OutcomeKey);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\" WHERE \"MediaTypeId\" = 2"));

            // Once the key is there the metadata job brings TheTVDB's details into the same Work.
            await SaveSeriesKeysAsync(host, "tvdb");
            await using (var scope = host.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<FillWorkMetadataV1>().ExecuteAsync<UpdateResult>(new CoverParametersV1(), default);
            }
            Assert.AreEqual(true, await host.ScalarAsync($"SELECT \"DetailedAt\" IS NOT NULL FROM \"Works\" WHERE \"Id\" = {workId}"));
            Assert.AreEqual(1L, await host.ScalarAsync($"SELECT count(*) FROM \"WorkUnits\" WHERE \"WorkId\" = {workId} AND \"WorkUnitTypeId\" = 2"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\" WHERE \"MediaTypeId\" = 2"));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task SeriesWithoutId_UseTitleAndYear_AndUnsupportedProfilesBlockTitles(bool unsupported)
        {
            var arr = new FakeArrs { WithoutSeriesId = true, UnsupportedProfile = unsupported, WithRootFolder = true };
            await using var host = await Host.CreateAsync(library: true, services: services =>
            {
                services.AddHttpClient<ServarrApiClient>().ConfigurePrimaryHttpMessageHandler(() => arr);
                services.AddHttpClient<TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => arr);
                services.AddHttpClient<TvdbClient>().ConfigurePrimaryHttpMessageHandler(() => arr);
            });
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            await SaveSeriesKeysAsync(host);
            var mount = (long)(await host.ScalarAsync("""INSERT INTO "StorageMounts" ("Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly") VALUES ('New import', 3, '/srv/new', FALSE) RETURNING "Id" """))!;
            var libraries = new BaseDataList<TakeoverLibraryV1>([new("/new", "Imported series", mount, "series")]);
            using var preview = await host.SendAsync(HttpMethod.Post, Takeover, new RunTakeoverDataV1(TakeoverSourceType.Sonarr, "http://sonarr.example.test:8989", RadarrKey, false, newLibraries: libraries));
            Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode, await preview.Content.ReadAsStringAsync());
            var previewItems = (await preview.Content.ReadFromJsonAsync<BaseDataList<TakeoverItemV1>>())!;
            Assert.AreEqual(unsupported ? "takeover_profile_unsupported" : "takeover_will_want", previewItems.Single(item => item.TakeoverItemTypeId == TakeoverItemType.Series).OutcomeKey);
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "LibraryRoots" """));
            using var applied = await host.SendAsync(HttpMethod.Post, Takeover, new RunTakeoverDataV1(TakeoverSourceType.Sonarr, "http://sonarr.example.test:8989", RadarrKey, true, newLibraries: libraries));
            Assert.AreEqual(HttpStatusCode.OK, applied.StatusCode, await applied.Content.ReadAsStringAsync());
            Assert.AreEqual(unsupported ? 0L : 1L, await host.ScalarAsync("""SELECT count(*) FROM "WantedItems" """));
            if (!unsupported)
            {
                Assert.AreEqual(true, await host.ScalarAsync("""
                    SELECT wanted."LibraryRootId" = root."Id" AND wanted."AcquisitionProfileId" IS NOT NULL
                    FROM "WantedItems" AS wanted CROSS JOIN "LibraryRoots" AS root WHERE root."Name" = 'Imported series'
                    """));
            }
            using var repeated = await host.SendAsync(HttpMethod.Post, Takeover, new RunTakeoverDataV1(TakeoverSourceType.Sonarr, "http://sonarr.example.test:8989", RadarrKey, true, newLibraries: libraries));
            Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode, await repeated.Content.ReadAsStringAsync());
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "LibraryRoots" """));
        }

        [TestMethod]
        public async Task LidarrMetadataProfiles_SharingOneQualityProfile_RemainSeparateAcrossImports()
        {
            var arr = new FakeArrs { MultipleMetadataProfiles = true };
            await using var host = await Host.CreateAsync(library: true, services: services =>
                services.AddHttpClient<ServarrApiClient>().ConfigurePrimaryHttpMessageHandler(() => arr));
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            var first = await RunAsync(host, true, TakeoverSourceType.Lidarr, "http://lidarr.example.test:8686");
            Assert.AreEqual(2, first.Count(item => item.TakeoverItemTypeId == TakeoverItemType.Profile && item.OutcomeKey == "takeover_added"));
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/acquisition/profiles?mediaTypeId=6");
            var imported = (await read.Content.ReadFromJsonAsync<BaseDataList<AcquisitionProfileDetailsV1>>())!.Where(item => !item.IsDefault).ToArray();
            Assert.AreEqual(2, imported.Length);
            CollectionAssert.AreEquivalent(new[] { "Album", "Single" }, imported.Select(item => item.MonitoredTypes.Single()).ToArray());
            var repeated = await RunAsync(host, true, TakeoverSourceType.Lidarr, "http://lidarr.example.test:8686");
            Assert.AreEqual(2, repeated.Count(item => item.TakeoverItemTypeId == TakeoverItemType.Profile && item.OutcomeKey == "takeover_exists"));
            Assert.AreEqual(2L, await host.ScalarAsync("""SELECT count(*) FROM "AcquisitionProfileImports" """));
        }

        private sealed class FakeRadarr : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                if (uri.Host == "api.themoviedb.org")
                {
                    return Task.FromResult(request.Headers.Authorization?.Parameter != TmdbToken ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : uri.AbsolutePath switch
                    {
                        "/3/movie/603" => Json("""{"id":603,"title":"The Matrix","original_title":"The Matrix","release_date":"1999-03-31","overview":"A hacker learns the truth.","external_ids":{"imdb_id":"tt0133093"}}"""),
                        "/3/movie/604" => Json("""{"id":604,"title":"Owned","release_date":"2000-01-01"}"""),
                        "/3/movie/605" => Json("""{"id":605,"title":"Ignored","release_date":"2000-01-01"}"""),
                        _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                    });
                }

                if (!request.Headers.TryGetValues("X-Api-Key", out var keys) || keys.Single() != RadarrKey)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                }

                return Task.FromResult(uri.AbsolutePath switch
                {
                    "/api/v3/indexer" => Json("""
                        [
                          {"name":"Readable","protocol":"usenet","implementation":"Newznab","enable":true,"priority":10,
                           "fields":[{"name":"baseUrl","value":"https://readable.example.test"},{"name":"apiPath","value":"/api"},{"name":"apiKey","value":"readable-key"},{"name":"categories","value":[2000,2040]}]},
                          {"name":"Hidden","protocol":"usenet","implementation":"Newznab","enable":true,"priority":20,
                           "fields":[{"name":"baseUrl","value":"https://hidden.example.test"},{"name":"apiPath","value":"/api"},{"name":"apiKey","value":"********"}]},
                          {"name":"Open","protocol":"usenet","implementation":"Newznab","enable":true,"priority":30,
                           "fields":[{"name":"baseUrl","value":"https://open.example.test"},{"name":"apiPath","value":"/api"},{"name":"apiKey","value":""}]},
                          {"name":"Tracker","protocol":"torrent","implementation":"Torznab","enable":true,"priority":5,"fields":[]}
                        ]
                        """),
                    "/api/v3/downloadclient" => Json("""
                        [{"name":"SAB","implementation":"Sabnzbd","enable":true,"priority":1,
                          "fields":[{"name":"host","value":"sab.example.test"},{"name":"port","value":8080},{"name":"useSsl","value":false},{"name":"apiKey","value":"********"},{"name":"movieCategory","value":"movies"}]}]
                        """),
                    "/api/v3/qualityprofile" or "/api/v3/customformat" or "/api/v3/rootfolder" or "/api/v3/language" or "/api/v3/indexerflag" or "/api/v3/customformat/schema" => Json("[]"),
                    "/api/v3/movie" => Json("""
                        [
                          {"title":"The Matrix","year":1999,"tmdbId":603,"monitored":true,"hasFile":false},
                          {"title":"Owned","year":2000,"tmdbId":604,"monitored":true,"hasFile":true},
                          {"title":"Ignored","year":2000,"tmdbId":605,"monitored":false,"hasFile":false},
                          {"title":"Unknown","year":2001,"monitored":true,"hasFile":false}
                        ]
                        """),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                });
            }

            private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        // Prowlarr, Sonarr, Lidarr and Readarr by their hosts, with the series Sonarr names by its TheTVDB, TMDB and IMDb ids.
        // Series come from TheTVDB; TMDB gives their rating.
        private static async Task SaveSeriesKeysAsync(Host host, params string[] keys)
        {
            foreach (var key in keys.Length == 0 ? ["tmdb", "tvdb"] : keys)
            {
                var provider = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = '" + key + "'"))!;
                using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + provider + "/secret", new SaveProviderSecretDataV1(TmdbToken));
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            }
        }

        private sealed class FakeArrs : HttpMessageHandler
        {
            public bool WithoutSeriesId { get; init; }
            public bool UnsupportedProfile { get; init; }
            public bool WithRootFolder { get; init; }
            public bool MultipleMetadataProfiles { get; init; }
            private const string Indexer = """[{"name":"NAME","protocol":"usenet","implementation":"Newznab","enable":true,"priority":10,"fields":[{"name":"baseUrl","value":"https://HOST.example.test"},{"name":"apiKey","value":"key"}]}]""";

            private static string Client(string category) => """[{"name":"SAB","implementation":"Sabnzbd","enable":true,"priority":1,"fields":[{"name":"host","value":"sab.example.test"},"""
                + """{"name":"port","value":8080},{"name":"apiKey","value":"sab-key"},""" + "{\"name\":\"" + category + "\",\"value\":\"" + category[..^8] + "\"}]}]";

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                if (uri.Host == "lidarr.example.test" && MultipleMetadataProfiles)
                {
                    if (uri.AbsolutePath == "/api/v1/metadataprofile")
                        return Task.FromResult(Json("""[{"id":2,"name":"Albums","primaryAlbumTypes":[{"albumType":{"id":0,"name":"Album"},"allowed":true}]},{"id":3,"name":"Singles","primaryAlbumTypes":[{"albumType":{"id":2,"name":"Single"},"allowed":true}]}]"""));
                    if (uri.AbsolutePath == "/api/v1/album")
                        return Task.FromResult(Json("""[{"title":"Owned album","monitored":true,"artist":{"qualityProfileId":1,"metadataProfileId":2},"statistics":{"trackCount":1,"trackFileCount":1}},{"title":"Owned single","monitored":true,"artist":{"qualityProfileId":1,"metadataProfileId":3},"statistics":{"trackCount":1,"trackFileCount":1}}]"""));
                }
                if (uri.Host == "sonarr.example.test" && uri.AbsolutePath == "/api/v3/series" && WithoutSeriesId)
                    return Task.FromResult(Json("""[{"title":"Example Show","year":2020,"monitored":true,"qualityProfileId":4,"path":"/new/Example Show","statistics":{"episodeFileCount":0,"episodeCount":1}}]"""));
                if (uri.Host == "sonarr.example.test" && uri.AbsolutePath == "/api/v3/customformat" && UnsupportedProfile)
                    return Task.FromResult(Json("""[{"id":1,"name":"Unsupported","specifications":[{"implementation":"FutureSpecification","fields":{"value":1}}]}]"""));
                if (uri.Host == "sonarr.example.test" && uri.AbsolutePath == "/api/v3/rootfolder" && WithRootFolder)
                    return Task.FromResult(Json("""[{"path":"/new","defaultQualityProfileId":4}]"""));
                if (uri.Host == "api.themoviedb.org")
                {
                    return Task.FromResult(uri.AbsolutePath switch
                    {
                        "/3/tv/1399" => Json("""{"id":1399,"name":"Example Show","first_air_date":"2020-04-01","external_ids":{"tvdb_id":81189},"seasons":[{"season_number":1,"name":"Season 1","air_date":"2020-04-01","episode_count":1}]}"""),
                        "/3/search/tv" => Json("""{"page":1,"total_pages":1,"results":[{"id":1399,"name":"Example Show","first_air_date":"2020-04-01"}]}"""),
                        _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                    });
                }

                // TheTVDB describes the series Sonarr hands over.
                if (uri.Host == "api4.thetvdb.com")
                {
                    return Task.FromResult(uri.AbsolutePath switch
                    {
                        "/v4/login" => Json("""{"data":{"token":"tvdb-test-token"}}"""),
                        "/v4/search" => Json("""{"data":[{"tvdb_id":"81189","name":"Example Show","year":"2020","type":"series","primary_language":"eng","translations":{"eng":"Example Show"}}]}"""),
                        "/v4/series/81189/extended" => Json("""{"data":{"id":81189,"name":"Example Show","firstAired":"2020-04-01","remoteIds":[{"sourceName":"TheMovieDB.com","id":"1399"}]}}"""),
                        "/v4/series/81189/episodes/default" => Json("""{"data":{"episodes":[{"id":1,"seasonNumber":1,"number":1,"name":"One","aired":"2020-04-01"}]},"links":{"next":null}}"""),
                        _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                    });
                }

                return Task.FromResult((uri.Host, uri.AbsolutePath) switch
                {
                    ("prowlarr.example.test", "/api/v1/indexer") => Json(Indexer.Replace("NAME", "Prowled", StringComparison.Ordinal).Replace("HOST", "prowled", StringComparison.Ordinal)),
                    ("sonarr.example.test", "/api/v3/indexer") => Json(Indexer.Replace("NAME", "Prowled", StringComparison.Ordinal).Replace("HOST", "prowled", StringComparison.Ordinal)),
                    ("sonarr.example.test", "/api/v3/downloadclient") => Json(Client("tvCategory")),
                    ("sonarr.example.test", "/api/v3/rootfolder") => Json("[]"),
                    ("sonarr.example.test", "/api/v3/series") => Json("""[{"title":"Example Show","year":2020,"tvdbId":81189,"tmdbId":1399,"imdbId":"tt0081189","monitored":true,"qualityProfileId":4,"statistics":{"episodeFileCount":0,"episodeCount":1}}]"""),
                    ("sonarr.example.test", "/api/v3/qualityprofile") => Json("""
                        [{"id":4,"name":"HD-1080p","upgradeAllowed":true,"cutoff":1002,"minFormatScore":0,"items":[
                          {"quality":{"id":4,"name":"HDTV-720p","source":"television","resolution":720},"items":[],"allowed":false},
                          {"quality":{"id":9,"name":"HDTV-1080p","source":"television","resolution":1080},"items":[],"allowed":true},
                          {"name":"WEB 1080p","id":1002,"allowed":true,"items":[
                            {"quality":{"id":3,"name":"WEBDL-1080p","source":"web","resolution":1080},"items":[],"allowed":true},
                            {"quality":{"id":15,"name":"WEBRip-1080p","source":"webRip","resolution":1080},"items":[],"allowed":true}]},
                          {"quality":{"id":7,"name":"Bluray-1080p","source":"bluray","resolution":1080},"items":[],"allowed":true},
                          {"quality":{"id":2,"name":"DVD","source":"dvd","resolution":480},"items":[],"allowed":true}],
                         "formatItems":[{"format":1,"name":"x265","score":-10000},{"format":2,"name":"Good groups","score":1750},{"format":3,"name":"Mixed","score":50},{"format":4,"name":"Unused","score":0}]}]
                        """),
                    ("sonarr.example.test", "/api/v3/customformat") => Json("""
                        [{"id":1,"name":"x265","specifications":[{"implementation":"ReleaseTitleSpecification","negate":false,"required":true,"fields":[{"name":"value","value":"[xh]\\.?265"}]}]},
                         {"id":2,"name":"Good groups","specifications":[
                           {"implementation":"ReleaseGroupSpecification","negate":false,"required":false,"fields":[{"name":"value","value":"^(GRP)$"}]},
                           {"implementation":"ReleaseGroupSpecification","negate":false,"required":false,"fields":[{"name":"value","value":"^(TEAM)$"}]}]},
                         {"id":3,"name":"Mixed","specifications":[
                           {"implementation":"ReleaseTitleSpecification","negate":false,"required":true,"fields":[{"name":"value","value":"HDR"}]},
                           {"implementation":"SourceSpecification","negate":false,"required":true,"fields":[{"name":"value","value":7}]}]},
                         {"id":4,"name":"Unused","specifications":[{"implementation":"ReleaseTitleSpecification","negate":false,"required":true,"fields":[{"name":"value","value":"x"}]}]}]
                        """),
                    ("sonarr.example.test", "/api/v3/language") => Json("""[{"id":1,"name":"English"}]"""),
                    ("sonarr.example.test", "/api/v3/indexerflag") => Json("[]"),
                    ("sonarr.example.test", "/api/v3/customformat/schema") => Json("""[{"implementation":"SourceSpecification","fields":[{"name":"value","selectOptions":[{"value":7,"name":"BlurayRaw"}]}]}]"""),
                    ("lidarr.example.test" or "readarr.example.test", "/api/v1/indexer" or "/api/v1/downloadclient" or "/api/v1/customformat") => Json("[]"),
                    ("lidarr.example.test", "/api/v1/qualityprofile") => Json("""
                        [{"id":1,"name":"Lossless","upgradeAllowed":true,"cutoff":6,"items":[
                          {"quality":{"id":2,"name":"MP3-320"},"items":[],"allowed":false},
                          {"quality":{"id":6,"name":"FLAC"},"items":[],"allowed":true},
                          {"quality":{"id":21,"name":"FLAC 24bit"},"items":[],"allowed":true}],"formatItems":[]}]
                        """),
                    ("lidarr.example.test", "/api/v1/metadataprofile") => Json("""
                        [{"id":2,"name":"Albums",
                          "primaryAlbumTypes":[{"albumType":{"id":0,"name":"Album"},"allowed":true},{"albumType":{"id":2,"name":"Single"},"allowed":false}],
                          "secondaryAlbumTypes":[{"albumType":{"id":0,"name":"Studio"},"allowed":true},{"albumType":{"id":3,"name":"Live"},"allowed":false}],
                          "releaseStatuses":[{"releaseStatus":{"id":0,"name":"Official"},"allowed":true},{"releaseStatus":{"id":1,"name":"Promotion"},"allowed":false}]}]
                        """),
                    ("lidarr.example.test", "/api/v1/rootfolder") => Json("""[{"path":"/data/music/","defaultQualityProfileId":1,"defaultMetadataProfileId":2},{"path":"/data/classical"}]"""),
                    ("lidarr.example.test", "/api/v1/album") => Json($$$"""
                        [
                          {"title":"Example Album","releaseDate":"2019-05-03T00:00:00Z","foreignAlbumId":"{{{MusicTests.Album}}}","albumType":"Album","monitored":true,
                           "artist":{"artistName":"Example Artist","path":"/data/music/Example Artist","qualityProfileId":1,"metadataProfileId":2},"statistics":{"trackFileCount":0,"trackCount":3}},
                          {"title":"Some Single","releaseDate":"2021-01-01T00:00:00Z","foreignAlbumId":"single","albumType":"Single","monitored":true,
                           "artist":{"artistName":"Example Artist","path":"/data/music/Example Artist","qualityProfileId":1,"metadataProfileId":2},"statistics":{"trackFileCount":0,"trackCount":1}},
                          {"title":"Bootleg","releaseDate":"2020-01-01T00:00:00Z","monitored":true,"statistics":{"trackFileCount":0,"trackCount":2}},
                          {"title":"Complete","releaseDate":"2018-01-01T00:00:00Z","foreignAlbumId":"x","monitored":true,"statistics":{"trackFileCount":2,"trackCount":2}}
                        ]
                        """),
                    ("readarr.example.test", "/api/v1/qualityprofile") => Json("""
                        [{"id":1,"name":"eBook","upgradeAllowed":false,"cutoff":3,"items":[
                          {"quality":{"id":3,"name":"EPUB"},"items":[],"allowed":true},
                          {"quality":{"id":2,"name":"MOBI"},"items":[],"allowed":true},
                          {"quality":{"id":10,"name":"MP3"},"items":[],"allowed":false}],"formatItems":[]}]
                        """),
                    ("readarr.example.test", "/api/v1/metadataprofile") => Json("""[{"id":1,"name":"Standard","allowedLanguages":"eng, ger","minPages":100}]"""),
                    ("readarr.example.test", "/api/v1/rootfolder") => Json("""[{"path":"/books","defaultQualityProfileId":1,"defaultMetadataProfileId":1}]"""),
                    ("readarr.example.test", "/api/v1/book") => Json("""
                        [
                          {"title":"Pride and Prejudice","releaseDate":"1813-01-28T00:00:00Z","monitored":true,
                           "author":{"authorName":"Jane Austen","path":"/books/Jane Austen","qualityProfileId":1,"metadataProfileId":1},"statistics":{"bookFileCount":0}},
                          {"title":"Unknown Book","releaseDate":"2001-01-01T00:00:00Z","monitored":true,"author":{"authorName":"Nobody"},"statistics":{"bookFileCount":0}}
                        ]
                        """),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                });
            }

            private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
