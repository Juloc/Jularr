using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Request.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Game.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Logic.Igdb;
using Jularr.Integrations.Logic.Libretro;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Sabnzbd;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class GameTests
    {
        private const string ClientId = "twitch-client-id";
        private const string ClientSecret = "twitch-client-secret";

        [TestMethod]
        public async Task Game_IgdbApplicationSearchAndImport_KeepsPlatformsAsIdentities()
        {
            var igdb = new FakeIgdb();
            await using var host = await Host.CreateAsync(razor: true, library: true, services: services => services.AddHttpClient<IgdbClient>().ConfigurePrimaryHttpMessageHandler(() => igdb));
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            var search = new SearchMetadataDataV1(MediaType.Game, "Example Quest");

            // Without the Twitch application there is no games provider.
            using var unavailable = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/metadata/search", search);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            var provider = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'igdb'"))!;
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + provider + "/secret", new SaveProviderSecretDataV1(ClientSecret, ClientId));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var providers = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/providers");
            Assert.IsFalse((await providers.Content.ReadAsStringAsync()).Contains(ClientSecret, StringComparison.Ordinal));

            // IGDB names the game, its developer and platforms.
            using var found = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/metadata/search", search);
            Assert.AreEqual(HttpStatusCode.OK, found.StatusCode, await found.Content.ReadAsStringAsync());
            var result = (await found.Content.ReadFromJsonAsync<BaseDataList<MetadataSearchResultV1>>())!.Single();
            Assert.AreEqual("1022", result.ExternalId);
            Assert.AreEqual("Example Studio", result.Author);
            Assert.AreEqual(2017, result.ReleaseYear);

            // Import keeps the platforms as shared identities; a repeat changes nothing.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Game, "1022"));
                Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
            }

            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\" WHERE \"MediaTypeId\" = 7"));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"GamePlatforms\""));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkPlatforms\""));
            Assert.AreEqual("Switch", await host.ScalarAsync("SELECT \"Abbreviation\" FROM \"GamePlatforms\" WHERE \"IgdbId\" = 130"));
            Assert.AreEqual("Example Studio", await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Developer'"));
            Assert.AreEqual("https://images.igdb.com/igdb/image/upload/t_cover_big/co1abc.jpg", await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Poster'"));

            // One Twitch token serves every request; IGDB only sees the application id.
            Assert.AreEqual(1, igdb.TokenRequests);
            Assert.IsTrue(igdb.ApiClientIds.All(id => id == ClientId));
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Providers");
            StringAssert.Contains(await page.Content.ReadAsStringAsync(), "ClientId");
        }

        [TestMethod]
        public async Task Game_RequestedPlatform_IsSearchedGrabbedAndImportedForThatPlatform()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            using var downloads = new LibraryTests.TemporaryDirectory();
            var igdb = new FakeIgdb();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, services: services =>
            {
                services.AddHttpClient<IgdbClient>().ConfigurePrimaryHttpMessageHandler(() => igdb);
                services.AddHttpClient<NewznabClient>().ConfigurePrimaryHttpMessageHandler(() => igdb);
                services.AddHttpClient<SabnzbdClient>().ConfigurePrimaryHttpMessageHandler(() => igdb);
            });
            var mountId = await LibraryTests.CreateMountAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"IsReadOnly\" = FALSE");
            Directory.CreateDirectory(Path.Combine(storage.Path, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Games"));
            await LibraryTests.CreateRootAsync(host, mountId, "Games", MediaType.Game);
            foreach (var data in new[]
            {
                new SaveAcquisitionConnectionDataV1("Indexer", AcquisitionConnectionType.Newznab, "https://indexer.example.test/", "indexer-key", true, 10, [], null, null, null),
                new SaveAcquisitionConnectionDataV1("SAB", AcquisitionConnectionType.Sabnzbd, "https://sab.example.test", "client-key", true, 10, [], "games", "/downloads", downloads.Path)
            })
            {
                using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/acquisition/connections", data);
                Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            }

            var provider = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'igdb'"))!;
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + provider + "/secret", new SaveProviderSecretDataV1(ClientSecret, ClientId));
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Game, "1022"));
            var work = (await imported.Content.ReadFromJsonAsync<Jularr.Backend.Data.Admin.Media.V1.WorkDetailsV1>())!;
            using var listed = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + work.Id + "/platforms");
            var platforms = (await listed.Content.ReadFromJsonAsync<BaseDataList<GamePlatformV1>>())!;
            var nsw = platforms.Single(platform => platform.Name == "Nintendo Switch");

            // A game request names one of the game's platforms.
            using var missing = await host.SendAsync(HttpMethod.Post, "/api/v1/works/" + work.Id + "/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
            using var foreign = await host.SendAsync(HttpMethod.Post, "/api/v1/works/" + work.Id + "/requests", new CreateRequestDataV1(1, 999_999));
            Assert.AreEqual(HttpStatusCode.BadRequest, foreign.StatusCode);
            using var requested = await host.SendAsync(HttpMethod.Post, "/api/v1/works/" + work.Id + "/requests", new CreateRequestDataV1(1, nsw.Id));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            // The Owner's own request needs no approval.
            Assert.AreEqual((short)2, await host.ScalarAsync("SELECT \"AcquisitionRequestStatusTypeId\" FROM \"AcquisitionRequests\""));
            Assert.AreEqual(nsw.Id, (long)(await host.ScalarAsync("SELECT \"GamePlatformId\" FROM \"WantedItems\""))!);

            // Only the wanted platform's release is taken; the best form wins.
            await RunAsync<SearchDueWantedV1>(host);
            CollectionAssert.AreEqual(new[] { "https://indexer.example.test/getnzb/nsp" }, igdb.Grabbed);

            // The release lands below its platform and tags the version.
            var job = Directory.CreateDirectory(Path.Combine(downloads.Path, "Example.Quest.Switch.NSP-GRP2")).FullName;
            File.WriteAllBytes(Path.Combine(job, "example.quest.nsp"), new byte[5000]);
            File.WriteAllText(Path.Combine(job, "grp2.nfo"), "release notes");
            igdb.History = """{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_1","name":"Game","status":"Completed","storage":"/downloads/Example.Quest.Switch.NSP-GRP2"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);
            var folder = Path.Combine(storage.Path, "1", "Games", "Nintendo Switch", "Example Quest (2017)");
            CollectionAssert.AreEqual(new[] { "example.quest.nsp" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
            Assert.AreEqual(nsw.Id, (long)(await host.ScalarAsync("SELECT \"GamePlatformId\" FROM \"GameReleases\""))!);
            Assert.AreEqual((short)6, await host.ScalarAsync("SELECT \"AcquisitionRequestStatusTypeId\" FROM \"AcquisitionRequests\""));

            // A scanned copy takes the platform its folder names; details list every platform on hand.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt")
                SELECT "Id", 'PC/Example Quest (2017)/setup.iso', 10, now(), now() FROM "LibraryRoots"
                """);
            var scanned = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE 'PC/%'"))!;
            using var identified = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + scanned + "/assignment", new AssignLibraryFileDataV1(work.Id, null, null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, identified.StatusCode, await identified.Content.ReadAsStringAsync());
            using var details = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + work.Id);
            CollectionAssert.AreEqual(new[] { "Nintendo Switch", "PC (Microsoft Windows)" }, (await details.Content.ReadFromJsonAsync<Jularr.Backend.Data.User.Media.V1.WorkDetailsV1>())!.Platforms);
        }

        [TestMethod]
        public async Task Game_ScannedRoms_AreIdentifiedByTheirVerifiedDump()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var rom = new byte[1024];
            Encoding.ASCII.GetBytes("example quest cartridge").CopyTo(rom, 0);
            var unknown = Encoding.ASCII.GetBytes("unknown adventure cartridge");
            var libretro = new FakeLibretro($$"""
                clrmamepro (
                	name "Nintendo - Super Nintendo Entertainment System"
                	version "2026.10.07"
                )

                game (
                	name "Example Quest, The (USA) (Rev 1)"
                	rom ( name "Example Quest, The (USA) (Rev 1).sfc" size 1024 crc 00000000 md5 00 sha1 {{Convert.ToHexString(SHA1.HashData(rom))}} )
                )
                game (
                	name "Unknown Adventure (Japan)"
                	rom ( name "Unknown Adventure (Japan).sfc" size 27 crc 00000000 md5 00 sha1 {{Convert.ToHexString(SHA1.HashData(unknown))}} )
                )
                """);
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, services: services => services.AddHttpClient<LibretroClient>().ConfigurePrimaryHttpMessageHandler(() => libretro));
            var mountId = await LibraryTests.CreateMountAsync(host);
            var retro = Path.Combine(storage.Path, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Games", "Retro");
            Directory.CreateDirectory(retro);
            File.WriteAllBytes(Path.Combine(retro, "eq.sfc"), rom);
            File.WriteAllBytes(Path.Combine(retro, "eq-copier.smc"), [.. new byte[512], .. rom]);
            File.WriteAllText(Path.Combine(retro, "README.md"), "notes");
            File.WriteAllBytes(Path.Combine(retro, "ua.sfc"), unknown);
            File.WriteAllBytes(Path.Combine(retro, "homebrew.sfc"), Encoding.ASCII.GetBytes("homebrew"));
            using (var archive = ZipFile.Open(Path.Combine(retro, "eq-copy.zip"), ZipArchiveMode.Create))
            {
                await using var entry = archive.CreateEntry("eq.smc").Open();
                await entry.WriteAsync(rom);
            }

            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Games", MediaType.Game);
            await host.Fixture.RunSqlAsync("""
                WITH work AS (INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (7, 'The Example Quest') RETURNING "Id")
                INSERT INTO "WorkTitles" ("WorkId", "Title", "IsManual") SELECT "Id", 'The Example Quest', TRUE FROM work
                """);

            // The platform's DAT names every copy, also behind a copier header or in an archive; their title finds the single game and the DAT its platform.
            await LibraryTests.ScanAsync(host, rootId);
            Assert.AreEqual(1, libretro.Requests);
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"MediaAssetId\" IS NOT NULL"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '%.md'"));
            Assert.AreEqual(Convert.ToHexStringLower(SHA1.HashData(rom)), await host.ScalarAsync("SELECT \"Sha1\" FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '%eq-copy.zip'"));
            Assert.AreEqual("SNES", await host.ScalarAsync("""
                SELECT DISTINCT platform."Abbreviation" FROM "GameReleases" AS release JOIN "GamePlatforms" AS platform ON platform."Id" = release."GamePlatformId"
                """));
            Assert.AreEqual(19, await host.ScalarAsync("SELECT \"IgdbId\" FROM \"GamePlatforms\""));

            // A dump without its game suggests the verified title; unknown files keep their path.
            using var unmatched = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/library/files?onlyUnmatched=true");
            var files = (await unmatched.Content.ReadFromJsonAsync<PageResult<LibraryFileDetailsV1>>())!.Items;
            Assert.AreEqual("Unknown Adventure", files.Single(file => file.RelativePath.EndsWith("ua.sfc", StringComparison.Ordinal)).SuggestedTitle);
            Assert.AreEqual("Retro", files.Single(file => file.RelativePath.EndsWith("homebrew.sfc", StringComparison.Ordinal)).SuggestedTitle);

            // A fresh DAT and unchanged files need no second pass.
            await LibraryTests.ScanAsync(host, rootId);
            Assert.AreEqual(1, libretro.Requests);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"ProbedAt\" IS NULL"));
        }

        [TestMethod]
        public async Task Game_PlaysInTheBrowserWithSavesAndPlayTimeOfTheProfile()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var rom = new byte[1024];
            Encoding.ASCII.GetBytes("example quest cartridge").CopyTo(rom, 0);
            var libretro = new FakeLibretro($$"""
                clrmamepro (
                	name "Nintendo - Super Nintendo Entertainment System"
                	version "2026.10.07"
                )

                game (
                	name "Example Quest, The (USA)"
                	rom ( name "Example Quest, The (USA).sfc" size 1024 crc 00000000 md5 00 sha1 {{Convert.ToHexString(SHA1.HashData(rom))}} )
                )
                """);
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, services: services => services.AddHttpClient<LibretroClient>().ConfigurePrimaryHttpMessageHandler(() => libretro));
            var mountId = await LibraryTests.CreateMountAsync(host);
            var retro = Path.Combine(storage.Path, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Games", "Retro");
            Directory.CreateDirectory(retro);
            File.WriteAllBytes(Path.Combine(retro, "eq.sfc"), rom);
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Games", MediaType.Game);
            await host.Fixture.RunSqlAsync("""
                WITH work AS (INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (7, 'The Example Quest') RETURNING "Id")
                INSERT INTO "WorkTitles" ("WorkId", "Title", "IsManual") SELECT "Id", 'The Example Quest', TRUE FROM work
                """);
            await LibraryTests.ScanAsync(host, rootId);
            var asset = (long)(await host.ScalarAsync("SELECT \"MediaAssetId\" FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '%eq.sfc'"))!;
            var workId = (long)(await host.ScalarAsync("SELECT \"WorkId\" FROM \"MediaAssets\" WHERE \"Id\" = " + asset))!;
            var api = "/api/v1/games/assets/" + asset;

            // The SNES platform plays in snes9x; the file keeps its name for the emulator.
            var game = await ReadAsync<GameV1>(host, api);
            Assert.AreEqual("snes9x", game.Emulator);
            Assert.AreEqual("eq.sfc", game.FileName);
            Assert.AreEqual(0L, game.PlayedMs);
            using var file = await host.SendAsync(HttpMethod.Get, api + "/files/eq.sfc");
            Assert.AreEqual(HttpStatusCode.OK, file.StatusCode);
            CollectionAssert.AreEqual(rom, await file.Content.ReadAsByteArrayAsync());
            using var head = await host.SendAsync(HttpMethod.Head, api + "/files/eq.sfc");
            Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
            Assert.AreEqual(rom.Length, head.Content.Headers.ContentLength);
            using var otherName = await host.SendAsync(HttpMethod.Get, api + "/files/other.sfc");
            Assert.AreEqual(HttpStatusCode.NotFound, otherName.StatusCode);

            // Save memory and the state belong to the profile and come back unchanged.
            using var noSave = await host.SendAsync(HttpMethod.Get, api + "/saves/Memory");
            Assert.AreEqual(HttpStatusCode.NotFound, noSave.StatusCode);
            byte[] memory = [1, 2, 3, 4];
            byte[] state = [.. Enumerable.Range(0, 4096).Select(index => (byte)index)];
            using var savedMemory = await host.SendAsync(HttpMethod.Put, api + "/saves/Memory", new UpdateGameSaveDataV1(memory));
            Assert.AreEqual(HttpStatusCode.OK, savedMemory.StatusCode, await savedMemory.Content.ReadAsStringAsync());
            using var savedState = await host.SendAsync(HttpMethod.Put, api + "/saves/State", new UpdateGameSaveDataV1(state));
            Assert.AreEqual(HttpStatusCode.OK, savedState.StatusCode, await savedState.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(memory, (await ReadAsync<GameSaveV1>(host, api + "/saves/Memory")).Data);
            CollectionAssert.AreEqual(state, (await ReadAsync<GameSaveV1>(host, api + "/saves/State")).Data);
            using var emptySave = await host.SendAsync(HttpMethod.Put, api + "/saves/Memory", new UpdateGameSaveDataV1([]));
            Assert.AreEqual(HttpStatusCode.BadRequest, emptySave.StatusCode);

            // Play time adds up in the game's progress, never as a resume position or a Continue entry.
            foreach (var seconds in new[] { 60, 45 })
            {
                using var played = await host.SendAsync(HttpMethod.Post, api + "/play-time", new RecordGamePlayDataV1(seconds));
                Assert.AreEqual(HttpStatusCode.OK, played.StatusCode, await played.Content.ReadAsStringAsync());
            }

            using var tooLong = await host.SendAsync(HttpMethod.Post, api + "/play-time", new RecordGamePlayDataV1(301));
            Assert.AreEqual(HttpStatusCode.BadRequest, tooLong.StatusCode);
            game = await ReadAsync<GameV1>(host, api);
            Assert.AreEqual(105_000L, game.PlayedMs);
            Assert.IsFalse((await ReadAsync<BaseDataList<ContinueItemV1>>(host, "/api/v1/continue")).Any(item => item.MediaAssetId == asset));

            // Details offers the game; its page starts EmulatorJS from Jularr only.
            using var details = await host.SendAsync(HttpMethod.Get, "/Details/" + workId);
            var detailsHtml = await details.Content.ReadAsStringAsync();
            StringAssert.Contains(detailsHtml, "href=\"/Game/" + asset + "\"");
            Assert.IsFalse(detailsHtml.Contains("/Player/" + asset, StringComparison.Ordinal));
            using var page = await host.SendAsync(HttpMethod.Get, "/Game/" + asset);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "data-emulator=\"snes9x\"");
            StringAssert.Contains(html, string.Format(System.Globalization.CultureInfo.CurrentCulture, "Played {0} min", 1));
            StringAssert.Contains(html, "data-file=\"" + api + "/files/eq.sfc\"");
            StringAssert.Contains(page.Headers.GetValues("Content-Security-Policy").Single(), "connect-src 'self'");

            // Switched off, nothing plays and Details offers nothing.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Emulation'");
            using var disabled = await host.SendAsync(HttpMethod.Get, api + "/files/eq.sfc");
            Assert.AreEqual(HttpStatusCode.Forbidden, disabled.StatusCode);
            using var detailsOff = await host.SendAsync(HttpMethod.Get, "/Details/" + workId);
            Assert.IsFalse((await detailsOff.Content.ReadAsStringAsync()).Contains("/Game/" + asset, StringComparison.Ordinal));
        }

        private static async Task<T> ReadAsync<T>(Host host, string path)
        {
            using var response = await host.SendAsync(HttpMethod.Get, path);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }

        [TestMethod]
        public void Game_RomNames_NameTheirGame()
        {
            Assert.AreEqual("The Legend of Zelda - A Link to the Past", GameRomMatching.Title("Legend of Zelda, The - A Link to the Past (USA) [b]"));
            Assert.AreEqual("Super Mario World", GameRomMatching.Title("Super Mario World (USA)"));
            Assert.AreEqual(GameRomMatching.Key("The Legend of Zelda: A Link to the Past"), GameRomMatching.Key("The Legend of Zelda - A Link to the Past"));
            Assert.AreEqual("Nintendo - Game Boy Color", GameRomMatching.System("Retro/Game.GBC")!.Dat);
            Assert.IsNull(GameRomMatching.System("PC/setup.iso"));

            // Every emulated platform starts a bundled core; Mega Drive is named by system because its core also serves Master System.
            Assert.AreEqual("segaMD", GameRomMatching.Emulated(29)!.Emulator);
            Assert.AreEqual("genesis_plus_gx", GameRomMatching.Emulated(64)!.Emulator);
            Assert.IsNull(GameRomMatching.Emulated(6));
            Assert.AreEqual(GameRomMatching.Systems.Count, GameRomMatching.EmulatedPlatforms.Distinct().Count());
        }

        [TestMethod]
        [DataRow("Example.Quest.Switch.NSP-GRP2", 130, "Matches")]
        [DataRow("Example.Quest.PS4-DUPLEX", 130, "WrongPlatform")]
        [DataRow("Example.Quest-CODEX", 130, "AmbiguousTitle")]
        [DataRow("Example.Quest-CODEX", 6, "Matches")]
        [DataRow("Example.Quest.Update.v1.2.NSW-GRP", 130, "AmbiguousTitle")]
        [DataRow("Example.Quest.2.NSW-GRP", 130, "AmbiguousTitle")]
        [DataRow("Example.Quest.XBOX.ONE-GRP", 49, "Matches")]
        [DataRow("Other.Game.NSW-GRP", 130, "WrongTitle")]
        [DataRow("Example.Quest.XBOX.ONE-GRP", 11, "WrongPlatform")]
        [DataRow("Example.Quest.XBOX360-GRP", 11, "WrongPlatform")]
        [DataRow("Example.Quest.XBOX-GRP", 11, "Matches")]
        [DataRow("Example.Quest.Wii.U-GRP", 5, "WrongPlatform")]
        [DataRow("Example.Quest.v1.0.3-CODEX", 6, "Matches")]
        [DataRow("Example.Quest.Amiga-GRP", 16, "Matches")]
        [DataRow("Example.Quest.Xbox.Series.X-GRP", 169, "Matches")]
        public void Game_ReleaseNames_AreJudgedForTheWantedPlatform(string release, int platform, string expected) =>
            Assert.AreEqual(expected, GameReleaseJudge.Judge(["Example Quest"], platform, platform == 16 ? "Amiga" : null,
                new ReleaseOffer(release, 1, "Indexer", 25, "guid", new Uri("https://indexer.example.test/guid"), null, 1_000_000, DateTimeOffset.UtcNow, ["title"])).Match.ToString());

        private static async Task RunAsync<TService>(Host host) where TService : Service.Core.SystemService<AcquisitionCycleParametersV1, NoData>
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TService>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default);
        }

        private sealed class FakeLibretro(string dat) : HttpMessageHandler
        {
            public int Requests { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                Assert.AreEqual("/libretro/libretro-database/master/metadat/no-intro/Nintendo%20-%20Super%20Nintendo%20Entertainment%20System.dat", request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(dat) });
            }
        }

        private sealed class FakeIgdb : HttpMessageHandler
        {
            private const string Game = """
                {"id":1022,"name":"Example Quest","first_release_date":1488499200,"summary":"A test adventure.","cover":{"image_id":"co1abc"},
                 "platforms":[{"id":130,"name":"Nintendo Switch","abbreviation":"Switch"},{"id":130,"name":"Nintendo Switch","abbreviation":"NSW"},{"id":6,"name":"PC (Microsoft Windows)","abbreviation":"PC"}],
                 "involved_companies":[{"company":{"name":"Example Studio"},"developer":true},{"company":{"name":"Example Publisher"},"developer":false}],
                 "alternative_names":[{"name":"Beispiel Quest"}]}
                """;

            public int TokenRequests { get; private set; }
            public List<string> ApiClientIds { get; } = [];
            public List<string> Grabbed { get; } = [];
            public string History { get; set; } = """{"history":{"slots":[]}}""";

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                if (uri.Host == "id.twitch.tv")
                {
                    var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                    TokenRequests++;
                    return form.Contains("client_secret=" + ClientSecret, StringComparison.Ordinal) && form.Contains("client_id=" + ClientId, StringComparison.Ordinal)
                        ? Json("""{"access_token":"igdb-access-token","expires_in":5000000,"token_type":"bearer"}""")
                        : new HttpResponseMessage(HttpStatusCode.BadRequest);
                }

                if (uri.Host == "indexer.example.test")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(uri.Query.Contains("t=caps", StringComparison.Ordinal)
                            ? """<caps><limits max="100" default="100" /><searching><search available="yes" supportedParams="q" /></searching></caps>"""
                            : """
                              <rss><channel>
                                <item><title>Example.Quest.NSW-GRP</title><guid>nsw</guid><link>https://indexer.example.test/getnzb/nsw</link><size>5000000000</size></item>
                                <item><title>Example.Quest.Switch.NSP-GRP2</title><guid>nsp</guid><link>https://indexer.example.test/getnzb/nsp</link><size>5000000000</size></item>
                                <item><title>Example.Quest-CODEX</title><guid>pc</guid><link>https://indexer.example.test/getnzb/pc</link><size>5000000000</size></item>
                                <item><title>Example.Quest.PS4-DUPLEX</title><guid>ps4</guid><link>https://indexer.example.test/getnzb/ps4</link><size>5000000000</size></item>
                              </channel></rss>
                              """, Encoding.UTF8, "application/rss+xml")
                    };
                }

                if (uri.Host == "sab.example.test")
                {
                    var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                    switch (form["mode"])
                    {
                        case "addurl":
                            Grabbed.Add(form["name"]!);
                            return Json("{\"status\":true,\"nzo_ids\":[\"SABnzbd_nzo_" + Grabbed.Count + "\"]}");
                        case "queue":
                            return Json("""{"queue":{"paused":false,"slots":[]}}""");
                        case "history":
                            return Json(History);
                        default:
                            return Json("""{"version":"4.5.0"}""");
                    }
                }

                if (request.Headers.Authorization?.Parameter != "igdb-access-token")
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                ApiClientIds.Add(request.Headers.GetValues("Client-ID").Single());
                return Json("[" + Game + "]");
            }

            private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
