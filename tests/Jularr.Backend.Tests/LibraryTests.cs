using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Service.System.Operation.V1;
using Jularr.Integrations.Logic.Media;
using Jularr.Integrations.Logic.Tmdb;
using Jularr.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class LibraryTests
    {
        private const string Roots = "/api/v1/admin/library/roots";

        [TestMethod]
        public async Task Roots_CreateRenameDelete_RejectOverlapsStaleVersionsAndNonOwners()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            var mountId = await CreateMountAsync(host);

            var rootId = await CreateRootAsync(host, mountId, "Series", MediaType.Series);
            using var nested = await host.SendAsync(HttpMethod.Post, Roots, new CreateLibraryRootDataV1("Nested", MediaType.Series, mountId, "Series/Anime"));
            Assert.AreEqual(HttpStatusCode.BadRequest, nested.StatusCode);
            StringAssert.Contains(await nested.Content.ReadAsStringAsync(), "overlapping_path");
            using var traversal = await host.SendAsync(HttpMethod.Post, Roots, new CreateLibraryRootDataV1("Escape", MediaType.Movie, mountId, "../Movies"));
            Assert.AreEqual(HttpStatusCode.BadRequest, traversal.StatusCode);
            await CreateRootAsync(host, mountId, "Movies", MediaType.Movie);

            // Renaming is version protected.
            using var renamed = await host.SendAsync(HttpMethod.Put, Roots + "/" + rootId, new RenameLibraryRootDataV1("Shows", 1));
            Assert.AreEqual(HttpStatusCode.OK, renamed.StatusCode, await renamed.Content.ReadAsStringAsync());
            using var stale = await host.SendAsync(HttpMethod.Put, Roots + "/" + rootId, new RenameLibraryRootDataV1("Stale", 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);

            // Connections in use cannot be removed.
            using var inUse = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts/" + mountId + "/removal", new RemoveStorageMountDataV1(1));
            Assert.AreEqual(HttpStatusCode.BadRequest, inUse.StatusCode);
            StringAssert.Contains(await inUse.Content.ReadAsStringAsync(), "in_use");

            using var deleted = await host.SendAsync(HttpMethod.Delete, Roots + "/" + rootId + "?version=2");
            Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"LibraryRoots\""));

            // MediaManager manages media, not storage.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var denied = await host.SendAsync(HttpMethod.Post, Roots, new CreateLibraryRootDataV1("Books", MediaType.Book, mountId, "Books"));
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using var files = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/library/files");
            Assert.AreEqual(HttpStatusCode.OK, files.StatusCode, await files.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task Scan_IndexesAssignsGrowsKnownFoldersAndKeepsInventoryWhenStorageIsOffline()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            var mountId = await CreateMountAsync(host);
            var series = Path.Combine(storage.Path, mountId.ToString(), "Series", "Show (2020)", "Season 1");
            Directory.CreateDirectory(series);
            File.WriteAllText(Path.Combine(series, "Show - S01E01.mkv"), "one");
            File.WriteAllText(Path.Combine(series, "Show - S01E01.de.srt"), "subtitle");
            File.WriteAllText(Path.Combine(series, "Show - S01E02.mkv"), "two");
            File.WriteAllText(Path.Combine(series, "sample.mkv"), "sample");
            File.WriteAllText(Path.Combine(series, "notes.txt"), "ignored");
            var rootId = await CreateRootAsync(host, mountId, "Series", MediaType.Series);

            // Scans run in the background worker.
            await ScanAsync(host, rootId);
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\""));
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT \"ProcessedCount\" FROM \"Operations\" WHERE \"OperationStatusTypeId\" = 3"));
            using var unmatched = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/library/files");
            var files = (await unmatched.Content.ReadFromJsonAsync<PageResult<LibraryFileDetailsV1>>())!.Items;
            Assert.AreEqual(2, files.Count);
            var first = files.Single(file => file.RelativePath.EndsWith("S01E01.mkv", StringComparison.Ordinal));
            Assert.AreEqual("Show", first.SuggestedTitle);
            Assert.AreEqual(WorkUnitType.Episode, first.SuggestedWorkUnitTypeId);
            Assert.AreEqual(1m, first.SuggestedUnitNumber);
            Assert.AreEqual(1m, first.SuggestedParentUnitNumber);

            // Manual identity takes subtitles along.
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + first.Id + "/assignment",
                new AssignLibraryFileDataV1(null, "Show", WorkUnitType.Episode, 1m, 1m));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"MediaAssetId\" IS NOT NULL"));
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!;
            using var again = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + first.Id + "/assignment",
                new AssignLibraryFileDataV1(workId, null, WorkUnitType.Episode, 1m, 1m));
            Assert.AreEqual(HttpStatusCode.BadRequest, again.StatusCode);

            // Known folders grow; copies become versions.
            File.WriteAllText(Path.Combine(series, "Show - S01E03.mkv"), "three");
            Directory.CreateDirectory(Path.Combine(series, "Copy"));
            File.WriteAllText(Path.Combine(series, "Copy", "Show - S01E01.mkv"), "copy");
            File.Delete(Path.Combine(series, "Show - S01E02.mkv"));
            await ScanAsync(host, rootId);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '%S01E02%'"));
            Assert.AreEqual(4L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"MediaAssetId\" IS NOT NULL"));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkVersions\""));
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT count(*) FROM \"WorkUnits\""));

            // Profiles read stored availability only.
            File.Delete(Path.Combine(series, "Show - S01E01.mkv"));
            await ScanAsync(host, rootId);
            using var availability = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + workId + "/availability");
            Assert.AreEqual(HttpStatusCode.OK, availability.StatusCode, await availability.Content.ReadAsStringAsync());
            var assets = (await availability.Content.ReadFromJsonAsync<PageResult<WorkAvailabilityV1>>())!.Items;
            Assert.AreEqual(3, assets.Count);
            Assert.AreEqual(1, assets.Count(asset => !asset.IsAvailable));
            Assert.IsTrue(assets.All(asset => asset.ParentUnitNumber == 1m));

            // Offline storage fails the scan, not the inventory.
            Directory.Move(Path.Combine(storage.Path, mountId.ToString(), "Series"), Path.Combine(storage.Path, mountId.ToString(), "Offline"));
            await ScanAsync(host, rootId);
            Assert.AreEqual("storage_unavailable", await host.ScalarAsync("SELECT \"ErrorKey\" FROM \"Operations\" ORDER BY \"Id\" DESC LIMIT 1"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"MissingSince\" IS NOT NULL"));

            // Unassigning drops empty assets.
            var copyId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '%Copy%'"))!;
            using var released = await host.SendAsync(HttpMethod.Delete, "/api/v1/admin/library/files/" + copyId + "/assignment");
            Assert.AreEqual(HttpStatusCode.OK, released.StatusCode, await released.Content.ReadAsStringAsync());
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"MediaAssets\""));
        }

        [TestMethod]
        public async Task StoredFiles_SubtitleColumn_MatchesTheFileTypes()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            var rootId = await CreateRootAsync(host, await CreateMountAsync(host), "Movies", MediaType.Movie);
            foreach (var path in new[] { "a.SRT", "b.ass", "c.Ssa", "d.vtt", "e.sub", "f.idx", "g.sup", "h.mkv", "srt.txt", "i.srt.mkv" })
            {
                await host.Fixture.RunSqlAsync($"""INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt") VALUES ({rootId}, '{path}', 1, now(), now())""");
                Assert.AreEqual(LibraryFileTypes.IsSubtitle(path), await host.ScalarAsync($"""SELECT "IsSubtitle" FROM "StoredFiles" WHERE "RelativePath" = '{path}'"""), path);
            }
        }

        [TestMethod]
        public async Task Operations_QueueOncePerRootAndResumeAfterRestart()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            var mountId = await CreateMountAsync(host);
            var rootId = await CreateRootAsync(host, mountId, "Movies", MediaType.Movie);

            using var queued = await host.SendAsync(HttpMethod.Post, Roots + "/" + rootId + "/scans");
            using var duplicate = await host.SendAsync(HttpMethod.Post, Roots + "/" + rootId + "/scans");
            Assert.AreEqual((await queued.Content.ReadFromJsonAsync<QueueLibraryScanResultV1>())!.Id, (await duplicate.Content.ReadFromJsonAsync<QueueLibraryScanResultV1>())!.Id);

            // A crashed worker leaves running work behind.
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"OperationStatusTypeId\" = 2, \"StartedAt\" = now()");
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<RequeueOperationsV1>().ExecuteAsync<UpdateResult>(new OperationQueueParametersV1(), default);
            Assert.AreEqual((short)1, await host.ScalarAsync("SELECT \"OperationStatusTypeId\" FROM \"Operations\""));

            using var operations = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/activity?activityTabTypeId=Activity");
            Assert.AreEqual(OperationType.LibraryScan, (await operations.Content.ReadFromJsonAsync<PageResult<ActivityItemV1>>())!.Items.Single().OperationTypeId);
        }

        [TestMethod]
        public async Task Scan_IsQueuedAgainAfterTheIntervalButNotForOfflineStorage()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            var mountId = await CreateMountAsync(host);
            await CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            async Task<bool> ScheduleAsync()
            {
                await using var scope = host.Services.CreateAsyncScope();
                return (await scope.ServiceProvider.GetRequiredService<ScheduleLibraryScansV1>().ExecuteAsync<UpdateResult>(new LibraryScanScheduleParametersV1(), default,
                    CancellationToken.None)).Success;
            }

            // A recent scan is enough.
            Assert.IsTrue(await ScheduleAsync());
            Assert.IsFalse(await ScheduleAsync());
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"CreatedAt\" = now() - interval '25 hours', \"OperationStatusTypeId\" = 3, \"FinishedAt\" = now()");
            Assert.IsTrue(await ScheduleAsync());
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 1"));

            // A sleeping NAS stays asleep.
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"CreatedAt\" = now() - interval '25 hours', \"OperationStatusTypeId\" = 3, \"FinishedAt\" = now()");
            await host.Fixture.RunSqlAsync("INSERT INTO \"StorageMountStates\" (\"StorageMountId\", \"StorageAvailabilityTypeId\", \"CheckedAt\") VALUES (" + mountId + ", 2, now())");
            Assert.IsFalse(await ScheduleAsync());

            // Off in the instance settings, nothing is rescanned; finished history leaves after 30 days.
            await host.Fixture.RunSqlAsync("DELETE FROM \"StorageMountStates\"; UPDATE \"InstanceSettings\" SET \"LibraryScanIntervalHours\" = 0");
            Assert.IsFalse(await ScheduleAsync());
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"FinishedAt\" = now() - interval '31 days' WHERE \"Id\" = (SELECT min(\"Id\") FROM \"Operations\")");
            await using var scope = host.Services.CreateAsyncScope();
            Assert.IsTrue((await scope.ServiceProvider.GetRequiredService<PruneOperationsV1>().ExecuteAsync<UpdateResult>(new PruneOperationsParametersV1(), default, CancellationToken.None)).Success);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\""));

            // Beyond its room the oldest finished entries leave first.
            await host.Fixture.RunSqlAsync("""
                UPDATE "InstanceSettings" SET "HistoryMegabytes" = 1;
                INSERT INTO "Operations" ("OperationTypeId", "OperationStatusTypeId", "FinishedAt") SELECT 3, 3, now() - make_interval(secs => number) FROM generate_series(1, 20000) AS number;
                """);
            Assert.IsTrue((await scope.ServiceProvider.GetRequiredService<PruneOperationsV1>().ExecuteAsync<UpdateResult>(new PruneOperationsParametersV1(), default, CancellationToken.None)).Success);
            Assert.IsTrue((long)(await host.ScalarAsync("SELECT count(*) FROM \"Operations\""))! < 20000);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 3 AND \"FinishedAt\" < now() - interval '19999 seconds'"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 3 AND \"FinishedAt\" = (SELECT max(\"FinishedAt\") FROM \"Operations\" WHERE \"OperationTypeId\" = 3)"));
        }

        [TestMethod]
        public async Task Storage_LocalPathsWakeAndBrowsing_StayInsideConfiguredConnections()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);

            // Local connections only from deployment configuration.
            using var unknown = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts",
                new SaveStorageMountDataV1("Local", StorageProtocolType.Local, null, "/srv/other", null, null, null, true, null));
            Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
            using var local = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts",
                new SaveStorageMountDataV1("Local", StorageProtocolType.Local, null, "/media/library", null, null, null, true, null));
            Assert.AreEqual(HttpStatusCode.OK, local.StatusCode, await local.Content.ReadAsStringAsync());

            // Host-native paths come only from the allowlist; exports stay POSIX.
            using var traversal = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts",
                new SaveStorageMountDataV1("Local", StorageProtocolType.Local, null, "/media/library/../other", null, null, null, true, null));
            Assert.AreEqual(HttpStatusCode.BadRequest, traversal.StatusCode);
            using var windowsExport = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts",
                new SaveStorageMountDataV1("NAS", StorageProtocolType.Nfs, "nas.test", "D:\volume1", "4.1", null, null, true, null));
            Assert.AreEqual(HttpStatusCode.BadRequest, windowsExport.StatusCode);
            if (OperatingSystem.IsWindows())
            {
                using var drive = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts",
                    new SaveStorageMountDataV1("Drive", StorageProtocolType.Local, null, storage.Path, null, null, null, true, null));
                Assert.AreEqual(HttpStatusCode.OK, drive.StatusCode, await drive.Content.ReadAsStringAsync());
            }

            // Wake settings require a device address.
            using var invalidWake = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts",
                new SaveStorageMountDataV1("NAS", StorageProtocolType.Nfs, "nas.test", "/volume1", "4.1", null, null, true, null, null, null, true));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidWake.StatusCode);
            using var invalidMac = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts",
                new SaveStorageMountDataV1("NAS", StorageProtocolType.Nfs, "nas.test", "/volume1", "4.1", null, null, true, null, "00:11:22:33:44", null, true));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidMac.StatusCode);
            var mountId = await CreateMountAsync(host);
            using var wake = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts/" + mountId + "/wake");
            Assert.AreEqual(HttpStatusCode.OK, wake.StatusCode, await wake.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"WakeOnDemand\" FROM \"StorageMounts\" WHERE \"Id\" = " + mountId));

            // Directory choices stay below the connection.
            Directory.CreateDirectory(Path.Combine(storage.Path, mountId.ToString(), "Movies"));
            Directory.CreateDirectory(Path.Combine(storage.Path, mountId.ToString(), "Series"));
            using var listed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts/" + mountId + "/directories", new ReadStorageDirectoriesDataV1(""));
            Assert.AreEqual(HttpStatusCode.OK, listed.StatusCode, await listed.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(new[] { "Movies", "Series" }, (await listed.Content.ReadFromJsonAsync<StorageDirectoryV1[]>())!.Select(item => item.Name).ToArray());
            using var escaped = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts/" + mountId + "/directories", new ReadStorageDirectoriesDataV1(".."));
            Assert.AreEqual(HttpStatusCode.BadRequest, escaped.StatusCode);
            using var missing = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts/" + mountId + "/directories", new ReadStorageDirectoriesDataV1("Missing"));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, missing.StatusCode);

            // Removal without a helper forgets the configuration.
            using var removed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts/" + mountId + "/removal", new RemoveStorageMountDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode, await removed.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task Razor_StoragePathsAndLibraryIdentity_UseRealForms()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path, razor: true);
            var mountId = await CreateMountAsync(host);
            var movies = Path.Combine(storage.Path, mountId.ToString(), "Movies", "Film (2001)");
            Directory.CreateDirectory(movies);
            File.WriteAllText(Path.Combine(movies, "Film.2001.1080p.mkv"), "film");

            // Paths are chosen through the safe folder browser.
            using var browse = await host.SendAsync(HttpMethod.Get, "/Admin/Storage?storageMountId=" + mountId + "&browse=");
            var html = await browse.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, browse.StatusCode, html);
            StringAssert.Contains(html, "browse=Movies");
            using var added = await host.SendAsync(HttpMethod.Post, "/Admin/Storage?storageMountId=" + mountId + "&handler=Root&browse=Movies", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = Token(html), ["StorageMountId"] = mountId.ToString(), ["RelativePath"] = "Movies", ["Name"] = "Movies", ["MediaTypeId"] = "1"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, added.StatusCode, await added.Content.ReadAsStringAsync());
            var rootId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"LibraryRoots\""))!;
            await ScanAsync(host, rootId);

            // Identification creates the Work.
            using var overview = await host.SendAsync(HttpMethod.Get, "/Admin/Library");
            html = await overview.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, overview.StatusCode, html);
            StringAssert.Contains(html, "/Admin/Library/Unmatched?root=" + rootId);
            using var rescan = await host.SendAsync(HttpMethod.Post, "/Admin/Library?PageNumber=1&handler=Scan&libraryRootId=" + rootId,
                new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(html) });
            Assert.AreEqual(HttpStatusCode.Redirect, rescan.StatusCode, await rescan.Content.ReadAsStringAsync());
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 1 AND \"OperationStatusTypeId\" IN (1, 2)"));
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsTrue(await OperationWorker.RunNextAsync(scope.ServiceProvider, NullLogger.Instance, CancellationToken.None));
            }
            using var library = await host.SendAsync(HttpMethod.Get, "/Admin/Library/Unmatched");
            html = await library.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, library.StatusCode, html);
            StringAssert.Contains(html, "value=\"Film\"");
            var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\""))!;
            using var assigned = await host.SendAsync(HttpMethod.Post, "/Admin/Library/Unmatched?PageNumber=1&handler=Assign&storedFileId=" + fileId, new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = Token(html), ["Title"] = "Film"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\" WHERE \"CanonicalTitle\" = 'Film'"))!;

            // The overview counts the identified file, its title and the finished scans.
            using var counted = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/library/overview");
            var shown = (await counted.Content.ReadFromJsonAsync<BaseDataList<LibraryOverviewV1>>())!.Single();
            Assert.AreEqual((1L, 1L, 0L, 0L, false), (shown.WorkCount, shown.FileCount, shown.UnmatchedCount, shown.MissingCount, shown.IsScanning));
            Assert.IsNotNull(shown.LastScanAt);
            using var details = await host.SendAsync(HttpMethod.Get, "/Details/" + workId);
            Assert.AreEqual(HttpStatusCode.OK, details.StatusCode, await details.Content.ReadAsStringAsync());
            StringAssert.Contains(await details.Content.ReadAsStringAsync(), "Versions &amp; languages");

            // Renaming moves files through the confirmed preview only.
            using var preview = await host.SendAsync(HttpMethod.Get, "/Admin/Library/Rename");
            html = await preview.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode, html);
            StringAssert.Contains(html, "Film/Film [HDTV-1080p].mkv");
            using var renamed = await host.SendAsync(HttpMethod.Post, "/Admin/Library/Rename?PageNumber=1&handler=Rename", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = Token(html), ["Fingerprint"] = Regex.Match(html, "value=\"([0-9a-f]{64})\"").Groups[1].Value
            });
            Assert.AreEqual(HttpStatusCode.Redirect, renamed.StatusCode, await renamed.Content.ReadAsStringAsync());
            Assert.IsTrue(File.Exists(Path.Combine(storage.Path, mountId.ToString(), "Movies", "Film", "Film [HDTV-1080p].mkv")));
            using var done = await host.SendAsync(HttpMethod.Get, renamed.Headers.Location!.OriginalString);
            StringAssert.Contains(await done.Content.ReadAsStringAsync(), "Files renamed: 1. Skipped: 0.");

            // Path pages stay navigable when empty.
            await host.Fixture.RunSqlAsync("INSERT INTO \"LibraryRoots\" (\"Name\", \"MediaTypeId\", \"StorageMountId\", \"RelativePath\") SELECT 'Root ' || n, 1, " + mountId + ", 'Root' || n FROM generate_series(1, 25) AS n");
            using var paths = await host.SendAsync(HttpMethod.Get, "/Admin/Storage?storageMountId=" + mountId + "&rootPageNumber=1");
            StringAssert.Contains(await paths.Content.ReadAsStringAsync(), "rootPageNumber=2");
            using var emptyPaths = await host.SendAsync(HttpMethod.Get, "/Admin/Storage?storageMountId=" + mountId + "&rootPageNumber=3");
            StringAssert.Contains(await emptyPaths.Content.ReadAsStringAsync(), "rootPageNumber=2");
        }

        [TestMethod]
        [DataRow("Show (2020)/Season 2/Show.S02E05.1080p.WEB-DL.mkv", MediaType.Series, "Show", WorkUnitType.Episode, "5", "2")]
        [DataRow("Series Name - tt39551287/Season 1/Series Name - S01E07 tt39551287.mkv", MediaType.Series, "Series Name", WorkUnitType.Episode, "7", "1")]
        [DataRow("Series Name - tvdb-12345/Season 1/Series Name - S01E07.mkv", MediaType.Series, "Series Name", WorkUnitType.Episode, "7", "1")]
        [DataRow("Anime Name/Anime Name - 012 [1080p].mkv", MediaType.Series, "Anime Name", WorkUnitType.Episode, "12", "1")]
        [DataRow("Manga Title/Manga Title v03 c012.5.cbz", MediaType.Book, "Manga Title", WorkUnitType.Chapter, "12.5", "3")]
        [DataRow("Novel/Novel Vol. 4.epub", MediaType.Book, "Novel", WorkUnitType.Volume, "4", null)]
        [DataRow("Album (1999)/03 - Song.flac", MediaType.Music, "Album", WorkUnitType.Track, "3", null)]
        [DataRow("Nintendo Switch/Example Quest (2017)/quest.nsp", MediaType.Game, "Example Quest", null, null, null)]
        [DataRow("Movie.Name.2010.2160p.BluRay.x265.mkv", MediaType.Movie, "Movie Name", null, null, null)]
        public void Parser_CommonLayouts_SuggestTitleAndUnit(string path, MediaType mediaType, string title, WorkUnitType? unitType, string? number, string? parent)
        {
            var parsed = MediaPathParser.Parse(path, mediaType);
            Assert.AreEqual(title, parsed.Title);
            Assert.AreEqual(unitType, parsed.WorkUnitTypeId);
            Assert.AreEqual(number is null ? null : decimal.Parse(number, System.Globalization.CultureInfo.InvariantCulture), parsed.UnitNumber);
            Assert.AreEqual(parent is null ? null : decimal.Parse(parent, System.Globalization.CultureInfo.InvariantCulture), parsed.ParentUnitNumber);
        }

        [DataTestMethod]
        [DataRow("Series/Season 1/Series - S01E01-E02.mkv")]
        [DataRow("Series/Season 1/Series - S01E01E02.mkv")]
        [DataRow("Series/Season 1/Series - S01E01-02.mkv")]
        [DataRow("Series/Season 1/Series - 1x01-02.mkv")]
        [DataRow("Series/Season 2/Series - S01E07.mkv")]
        [DataRow("Series/Season 1/Series - 2024.10.17.mkv")]
        [DataRow("Series/Season 1/Series - 2024-10-17.mkv")]
        public void Parser_AmbiguousEpisodePaths_NeedManualReview(string path)
        {
            var parsed = MediaPathParser.Parse(path, MediaType.Series);
            Assert.IsNull(parsed.WorkUnitTypeId, path);
        }

        [TestMethod]
        public async Task Scan_MatchesImportedSeriesAndKeepsAmbiguousFilesUnassigned()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            var mountId = await CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Series", "Imported Show - tt39551287", "Season 1");
            var ambiguous = Path.Combine(storage.Path, mountId.ToString(), "Series", "Duplicate Show", "Season 1");
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(ambiguous);
            File.WriteAllText(Path.Combine(folder, "Imported Show - S01E07 HDTV-1080p tt39551287.mkv"), "episode");
            File.WriteAllText(Path.Combine(ambiguous, "Duplicate Show - S01E01.mkv"), "ambiguous");
            var rootId = await CreateRootAsync(host, mountId, "Series", MediaType.Series);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle", "IsMediaSubtypeManual")
                VALUES (2, 'Imported Show', FALSE), (2, 'Duplicate Show', FALSE), (2, 'Duplicate Show', FALSE);
                """);

            await host.Fixture.RunSqlAsync("""
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                SELECT "Id", 'imdb', 'tt39551287' FROM "Works" WHERE "CanonicalTitle" = 'Imported Show';
                """);
            await ScanAsync(host, rootId);
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "StoredFiles" WHERE "MediaAssetId" IS NOT NULL AND "RelativePath" LIKE 'Imported Show%' """));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "StoredFiles" WHERE "MediaAssetId" IS NULL AND "RelativePath" LIKE 'Duplicate Show%' """));
            await ScanAsync(host, rootId);
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "MediaAssets" """));
        }

        [TestMethod]
        public void Parser_NfoIdentity_RejectsConflictsAndExternalEntities()
        {
            var (ids, conflicted) = MediaPathParser.ParseNfo(
                """<tvshow><uniqueid type="imdb">tt39551287</uniqueid><tvdbid>24680</tvdbid></tvshow>""", MediaType.Series);
            Assert.IsFalse(conflicted);
            Assert.AreEqual("tt39551287", ids["imdb"]);
            Assert.AreEqual("24680", ids["tvdb"]);
            Assert.IsFalse(MediaPathParser.ParseNfo("\uFEFF" + """<tvshow><imdbid>tt39551287</imdbid></tvshow>""", MediaType.Series).Conflicted);

            Assert.IsTrue(MediaPathParser.ParseNfo(
                """<tvshow><imdbid>tt39551287</imdbid><uniqueid type="imdb">tt12345678</uniqueid></tvshow>""", MediaType.Series).Conflicted);
            Assert.IsTrue(MediaPathParser.ParseNfo(
                """<!DOCTYPE tvshow [<!ENTITY ex SYSTEM "file:///etc/passwd">]><tvshow><imdbid>&ex;</imdbid></tvshow>""", MediaType.Series).Conflicted);
        }

        [TestMethod]
        public async Task Scan_NfoIdentifiesSeriesWithDifferentFolderTitle()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            var mountId = await CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Series", "Localized Folder", "Season 1");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(Directory.GetParent(folder)!.FullName, "tvshow.nfo"),
                """<tvshow><uniqueid type="imdb">tt39551287</uniqueid></tvshow>""");
            File.WriteAllText(Path.Combine(folder, "Localized Folder - S01E07.mkv"), "episode");
            var rootId = await CreateRootAsync(host, mountId, "Series", MediaType.Series);
            await host.Fixture.RunSqlAsync("""
                WITH work AS (
                    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (2, 'Original Name') RETURNING "Id"
                )
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                SELECT "Id", 'imdb', 'tt39551287' FROM work;
                """);

            await ScanAsync(host, rootId);
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "StoredFiles" WHERE "MediaAssetId" IS NOT NULL """));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));
        }

        [TestMethod]
        public void Parser_PlexMatch_ReadsSeriesAndExplicitEpisodeHints()
        {
            var hints = MediaPathParser.ParsePlexMatch("""
                # Series folder
                show: A Different Series Title
                year: 2020
                imdbid: 39551287
                tvdbid: 24680
                ep: S01E07: Season 1/Unusual File.mkv
                """);
            Assert.IsFalse(hints.Conflicted);
            Assert.AreEqual("tt39551287", hints.Ids["imdb"]);
            Assert.AreEqual("24680", hints.Ids["tvdb"]);
            var parsed = MediaPathParser.ApplyPlexMatch(
                MediaPathParser.Parse("Unmatched Folder/Season 1/Unusual File.mkv", MediaType.Series),
                "Season 1/Unusual File.mkv", "Unusual File.mkv", hints, MediaPathParser.ParsePlexMatch(""));
            Assert.AreEqual("A Different Series Title", parsed.Title);
            Assert.AreEqual(2020, parsed.Year);
            Assert.AreEqual(1m, parsed.ParentUnitNumber);
            Assert.AreEqual(7m, parsed.UnitNumber);
            Assert.AreEqual(WorkUnitType.Episode, parsed.WorkUnitTypeId);

            var seasonHint = MediaPathParser.ParsePlexMatch("Season: 2\nEpisode: SP01: Special.mkv");
            var special = MediaPathParser.ApplyPlexMatch(parsed, "Season 2/Special.mkv", "Special.mkv", hints, seasonHint);
            Assert.AreEqual(0m, special.ParentUnitNumber);
            Assert.AreEqual(1m, special.UnitNumber);

            Assert.IsTrue(MediaPathParser.ParsePlexMatch("imdbid: tt39551287\nimdbid: tt12345678").Conflicted);
            Assert.IsTrue(MediaPathParser.ParsePlexMatch("Pattern: Show {se} {ep}").Conflicted);
        }

        [TestMethod]
        public async Task Scan_PlexMatchIdentifiesSeriesAndUnnumberedEpisode()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            var mountId = await CreateMountAsync(host);
            var series = Path.Combine(storage.Path, mountId.ToString(), "Series", "Unrelated Folder");
            var folder = Path.Combine(series, "Season 1");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(series, ".plexmatch"), """
                imdbid: tt39551287
                episode: S01E07: Season 1/Unnumbered.mkv
                """);
            File.WriteAllText(Path.Combine(folder, "Unnumbered.mkv"), "episode");
            var rootId = await CreateRootAsync(host, mountId, "Series", MediaType.Series);
            await host.Fixture.RunSqlAsync("""
                WITH work AS (
                    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (2, 'Canonical Show') RETURNING "Id"
                )
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                SELECT "Id", 'imdb', 'tt39551287' FROM work;
                """);

            await ScanAsync(host, rootId);
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "StoredFiles" WHERE "MediaAssetId" IS NOT NULL """));
        }

        [TestMethod]
        public async Task Scan_SeasonPlexMatchOverridesUnnumberedEpisode()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            var mountId = await CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Series", "Different Title", "Season Two");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, ".plexmatch"), "Season: 2\nTVDBID: 13579\nEP: 07: Episode Seven.mkv");
            File.WriteAllText(Path.Combine(folder, "Episode Seven.mkv"), "episode");
            var rootId = await CreateRootAsync(host, mountId, "Series", MediaType.Series);
            await host.Fixture.RunSqlAsync("""
                WITH work AS (
                    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (2, 'Canonical Name') RETURNING "Id"
                )
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                SELECT "Id", 'tvdb', '13579' FROM work;
                """);

            await ScanAsync(host, rootId);
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "StoredFiles" WHERE "MediaAssetId" IS NOT NULL """));
        }

        [TestMethod]
        public async Task Scan_UnsupportedPlexMatchPattern_DoesNotGuessAnEpisode()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            var mountId = await CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Series", "Canonical Name", "Season 1");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(Directory.GetParent(folder)!.FullName, ".plexmatch"), "Pattern: Show Part {se} - {ep} - *");
            File.WriteAllText(Path.Combine(folder, "Canonical Name - S01E01.mkv"), "episode");
            var rootId = await CreateRootAsync(host, mountId, "Series", MediaType.Series);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (2, 'Canonical Name');
                """);

            await ScanAsync(host, rootId);
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "StoredFiles" WHERE "MediaAssetId" IS NULL AND NOT "IsSubtitle" """));
        }

        [TestMethod]
        public async Task Scan_MovieNfoMatchesAnUnrelatedFolderName()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            var mountId = await CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Movies", "Arbitrary Folder");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "movie.nfo"), """<movie><uniqueid type="imdb">tt12345678</uniqueid></movie>""");
            File.WriteAllText(Path.Combine(folder, "Unknown Title.mkv"), "movie");
            var rootId = await CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await host.Fixture.RunSqlAsync("""
                WITH work AS (
                    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (1, 'Canonical Film') RETURNING "Id"
                )
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                SELECT "Id", 'imdb', 'tt12345678' FROM work;
                """);

            await ScanAsync(host, rootId);
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "StoredFiles" WHERE "MediaAssetId" IS NOT NULL """));
        }

        [TestMethod]
        public async Task Scan_ConflictingNfoIdentity_DoesNotAssociateMovie()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            var mountId = await CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Movies", "Canonical Film - tt12345678");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "movie.nfo"), """<movie><imdbid>tt39551287</imdbid></movie>""");
            File.WriteAllText(Path.Combine(folder, "Canonical Film - tt12345678.mkv"), "movie");
            var rootId = await CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await host.Fixture.RunSqlAsync("""
                WITH work AS (
                    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (1, 'Canonical Film') RETURNING "Id"
                )
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                SELECT "Id", 'imdb', 'tt12345678' FROM work;
                """);

            await ScanAsync(host, rootId);
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "StoredFiles" WHERE "MediaAssetId" IS NULL """));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));
        }

        [TestMethod]
        public async Task Scan_MultiEpisodeFile_DoesNotLinkToOnlyTheFirstEpisode()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            var mountId = await CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Series", "Example Show - tt39551287", "Season 1");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Example Show - S01E01.mkv"), "first");
            File.WriteAllText(Path.Combine(folder, "Example Show - S01E01-E02.mkv"), "double");
            var rootId = await CreateRootAsync(host, mountId, "Series", MediaType.Series);
            await host.Fixture.RunSqlAsync("""
                WITH work AS (
                    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (2, 'Example Show') RETURNING "Id"
                )
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                SELECT "Id", 'imdb', 'tt39551287' FROM work;
                """);

            await ScanAsync(host, rootId);
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "StoredFiles" WHERE "MediaAssetId" IS NOT NULL """));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "StoredFiles" WHERE "RelativePath" LIKE '%S01E01-E02.mkv' AND "MediaAssetId" IS NULL """));
        }

        [TestMethod]
        public async Task Scan_UnknownImdbId_ResolvesViaTmdbOnceAndImportsEpisode()
        {
            using var storage = new TemporaryDirectory();
            var source = new FakeTmdbIdentity();
            await using var host = await CreateHostAsync(storage.Path, services: services =>
                services.AddHttpClient<TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => source));
            var tmdb = (long)(await host.ScalarAsync("""SELECT "Id" FROM "Providers" WHERE "Key" = 'tmdb'"""))!;
            using var secret = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + tmdb + "/secret", new SaveProviderSecretDataV1("eyJ-library-identity-token"));
            Assert.AreEqual(HttpStatusCode.OK, secret.StatusCode, await secret.Content.ReadAsStringAsync());

            var mountId = await CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Series", "Example Show - tt39551287", "Season 1");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Example Show - S01E07 - Night tt39551287.mkv"), "episode");
            var rootId = await CreateRootAsync(host, mountId, "Series", MediaType.Series);

            await ScanAsync(host, rootId);
            Assert.AreEqual(1L, await host.ScalarAsync("""
                SELECT count(*)
                FROM "StoredFiles" AS file
                JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                JOIN "WorkExternalIdentities" AS identity ON identity."WorkId" = asset."WorkId"
                WHERE identity."Namespace" = 'imdb' AND identity."Value" = 'tt39551287';
                """));
            Assert.AreEqual(1, source.FindRequests);
            await ScanAsync(host, rootId);
            Assert.AreEqual(1, source.FindRequests);
        }

        [TestMethod]
        public async Task Scan_WithoutProviderId_MatchesUniqueLocalizedTitle()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            var mountId = await CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Series", "Deutscher Titel (2020)", "Season 1");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Deutscher Titel - S01E07.mkv"), "episode");
            var rootId = await CreateRootAsync(host, mountId, "Series", MediaType.Series);
            await host.Fixture.RunSqlAsync("""
                WITH work AS (
                    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (2, 'Original Title', 2020) RETURNING "Id"
                )
                INSERT INTO "WorkTitles" ("WorkId", "Title", "IsManual")
                SELECT "Id", 'Deutscher Titel', TRUE FROM work;
                """);

            await ScanAsync(host, rootId);
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "StoredFiles" WHERE "MediaAssetId" IS NOT NULL """));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));
        }

        [TestMethod]
        public async Task Scan_UnknownSecondProviderId_DoesNotGuessFromTheFirst()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            var mountId = await CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Series", "Example Show - tt39551287 - tvdb-4444", "Season 1");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Example Show - S01E07.mkv"), "episode");
            var rootId = await CreateRootAsync(host, mountId, "Series", MediaType.Series);
            await host.Fixture.RunSqlAsync("""
                WITH work AS (
                    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (2, 'Example Show') RETURNING "Id"
                )
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                SELECT "Id", 'imdb', 'tt39551287' FROM work;
                """);

            await ScanAsync(host, rootId);
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "StoredFiles" WHERE "MediaAssetId" IS NULL AND NOT "IsSubtitle" """));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));
        }

        private sealed class FakeTmdbIdentity : HttpMessageHandler
        {
            public int FindRequests { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/3/find/tt39551287")
                {
                    FindRequests++;
                }

                var json = path switch
                {
                    "/3/find/tt39551287" => """{"movie_results":[],"tv_results":[{"id":1399}]}""",
                    "/3/tv/1399" => """{"id":1399,"name":"Example Show","first_air_date":"2020-01-01","external_ids":{"imdb_id":"tt39551287"},"seasons":[{"season_number":1,"name":"Season 1","episode_count":1}]}""",
                    "/3/tv/1399/season/1" => """{"episodes":[{"season_number":1,"episode_number":7,"name":"Night"}]}""",
                    _ => null
                };
                return Task.FromResult(json is null ? new HttpResponseMessage(HttpStatusCode.NotFound) :
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
            }
        }

        [TestMethod]
        public void Parser_ProviderIds_MatchAcrossFoldersAndRejectConflicts()
        {
            const string path = "Series - tt39551287 - tvdb-12345/Season 1/Series - S01E07 tt39551287.mkv";
            var (ids, conflicted) = MediaPathParser.ProviderIds(path, MediaType.Series);
            Assert.IsFalse(conflicted);
            Assert.AreEqual("tt39551287", ids["imdb"]);
            Assert.AreEqual("12345", ids["tvdb"]);

            var mismatched = MediaPathParser.ProviderIds(path.Replace("S01E07 tt39551287", "S01E07 tt12345678", StringComparison.Ordinal), MediaType.Series);
            Assert.IsTrue(mismatched.Conflicted);

            const string plex = "Series [imdbid-tt39551287] [tmdbid-1399] [tvdbid-24680]/Season 1/Series - S01E07.mkv";
            var (plexIds, plexConflict) = MediaPathParser.ProviderIds(plex, MediaType.Series);
            Assert.IsFalse(plexConflict);
            Assert.AreEqual("tt39551287", plexIds["imdb"]);
            Assert.AreEqual("1399", plexIds["tmdb_tv"]);
            Assert.AreEqual("24680", plexIds["tvdb"]);
            Assert.AreEqual("Series", MediaPathParser.Parse(plex, MediaType.Series).Title);

            const string plain = "Movie - imdb-tt1234567.mkv";
            Assert.AreEqual("Movie", MediaPathParser.Parse(plain, MediaType.Movie).Title);
            Assert.AreEqual("tt1234567", MediaPathParser.ProviderIds(plain, MediaType.Movie).Ids["imdb"]);
        }

        [TestMethod]
        public void Probe_FfprobeJson_MapsContainerAndStreams()
        {
            var probe = MediaProbeClient.Parse("""
                {"streams":[{"index":0,"codec_type":"video","codec_name":"hevc","width":3840,"height":2160,"disposition":{"default":1}},
                {"index":1,"codec_type":"audio","codec_name":"eac3","channels":6,"tags":{"language":"ger","title":"Deutsch"}},
                {"index":2,"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"und"},"disposition":{"forced":1}},
                {"index":3,"codec_type":"attachment"}],
                "format":{"format_name":"matroska,webm","duration":"1442.512","bit_rate":"8000000"}}
                """);
            Assert.AreEqual("matroska", probe.Container);
            Assert.AreEqual(1442512L, probe.DurationMs);
            Assert.AreEqual(3, probe.Streams.Count);
            Assert.AreEqual(2160, probe.Streams[0].Height);
            Assert.AreEqual("ger", probe.Streams[1].Language);
            Assert.IsNull(probe.Streams[2].Language);
            Assert.IsTrue(probe.Streams[2].IsForced);
        }

        [TestMethod]
        public async Task Rename_MovesOnlyTheConfirmedPreviewAndKeepsIdentity()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path);
            var mountId = await CreateMountAsync(host);
            var movies = Path.Combine(storage.Path, mountId.ToString(), "Movies");
            Directory.CreateDirectory(Path.Combine(movies, "Some Folder"));
            Directory.CreateDirectory(Path.Combine(movies, "Example Movie (2020)"));
            File.WriteAllText(Path.Combine(movies, "Some Folder", "movie file [Bluray-1080p].mkv"), "movie");
            File.WriteAllText(Path.Combine(movies, "Some Folder", "movie file [Bluray-1080p].en.srt"), "subtitle");
            File.WriteAllText(Path.Combine(movies, "Example Movie (2020)", "Example Movie (2020) [Bluray-1080p].mkv"), "someone else");
            var rootId = await CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await ScanAsync(host, rootId);
            var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\" WHERE \"RelativePath\" = 'Some Folder/movie file [Bluray-1080p].mkv'"))!;
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment", new AssignLibraryFileDataV1(null, "Example Movie", null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            await host.Fixture.RunSqlAsync("UPDATE \"Works\" SET \"ReleaseYear\" = 2020");

            // The preview names every move; a taken name gets a counter; nothing moves yet.
            using var preview = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/library/renames");
            Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode, await preview.Content.ReadAsStringAsync());
            var plan = (await preview.Content.ReadFromJsonAsync<LibraryRenamePlanV1>())!;
            CollectionAssert.AreEquivalent(new[] { "Example Movie (2020)/Example Movie (2020) [Bluray-1080p] (2).mkv", "Example Movie (2020)/Example Movie (2020) [Bluray-1080p] (2).en.srt" },
                plan.Items.Select(item => item.TargetPath).ToArray());
            Assert.IsTrue(File.Exists(Path.Combine(movies, "Some Folder", "movie file [Bluray-1080p].mkv")));

            // Only the exact preview is applied.
            using var changed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/library/renames", new ApplyLibraryRenamesDataV1(new string('0', 64)));
            Assert.AreEqual(HttpStatusCode.BadRequest, changed.StatusCode);
            StringAssert.Contains(await changed.Content.ReadAsStringAsync(), "library_rename_changed");
            using var applied = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/library/renames", new ApplyLibraryRenamesDataV1(plan.Fingerprint));
            Assert.AreEqual(HttpStatusCode.OK, applied.StatusCode, await applied.Content.ReadAsStringAsync());
            var result = (await applied.Content.ReadFromJsonAsync<ApplyLibraryRenamesResultV1>())!;
            Assert.AreEqual(2, result.RenamedCount);
            Assert.AreEqual(0, result.SkippedCount);
            Assert.IsTrue(File.Exists(Path.Combine(movies, "Example Movie (2020)", "Example Movie (2020) [Bluray-1080p] (2).en.srt")));
            Assert.IsFalse(Directory.Exists(Path.Combine(movies, "Some Folder")));
            Assert.AreEqual("Example Movie (2020)/Example Movie (2020) [Bluray-1080p] (2).mkv",
                await host.ScalarAsync("SELECT \"RelativePath\" FROM \"StoredFiles\" WHERE \"Id\" = " + fileId + " AND \"MediaAssetId\" IS NOT NULL"));
            using var empty = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/library/renames");
            Assert.AreEqual(0, (await empty.Content.ReadFromJsonAsync<LibraryRenamePlanV1>())!.Items.Count);

            // A name taken on disk only skips the media file and its subtitles together.
            Directory.CreateDirectory(Path.Combine(movies, "Second"));
            File.WriteAllText(Path.Combine(movies, "Second", "second.mkv"), "second");
            File.WriteAllText(Path.Combine(movies, "Second", "second.en.srt"), "subtitle");
            await ScanAsync(host, rootId);
            var secondId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\" WHERE \"RelativePath\" = 'Second/second.mkv'"))!;
            using var second = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + secondId + "/assignment", new AssignLibraryFileDataV1(null, "Second Movie", null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, second.StatusCode, await second.Content.ReadAsStringAsync());
            Directory.CreateDirectory(Path.Combine(movies, "Second Movie"));
            File.WriteAllText(Path.Combine(movies, "Second Movie", "Second Movie.mkv"), "unscanned");
            using var blockedPreview = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/library/renames");
            var blockedPlan = (await blockedPreview.Content.ReadFromJsonAsync<LibraryRenamePlanV1>())!;
            Assert.AreEqual(2, blockedPlan.Items.Count);
            using var blocked = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/library/renames", new ApplyLibraryRenamesDataV1(blockedPlan.Fingerprint));
            var blockedResult = (await blocked.Content.ReadFromJsonAsync<ApplyLibraryRenamesResultV1>())!;
            Assert.AreEqual(0, blockedResult.RenamedCount);
            Assert.AreEqual(2, blockedResult.SkippedCount);
            Assert.IsTrue(File.Exists(Path.Combine(movies, "Second", "second.en.srt")));
            Assert.AreEqual("unscanned", File.ReadAllText(Path.Combine(movies, "Second Movie", "Second Movie.mkv")));

            // Moving files is the Owner's.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var denied = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/library/renames");
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        [TestMethod]
        public void Rename_Plan_UsesTheImportLayoutPerType()
        {
            static LibraryRenameFileData File(long id, MediaType type, string path, string? title, WorkUnitType? unit = null, decimal? number = null, decimal? parent = null,
                MediaSubtype? subtype = null, string? unitTitle = null, DateTime? missing = null, int? width = null) =>
                new(id, 1, "Library", type, subtype, path, missing, title is null ? null : id, title, 2019, "Artist A, Artist B", unit, number, unitTitle, parent, null, null, null, 1, width);

            var plan = LibraryRenameLogic.Plan([
                File(1, MediaType.Series, "show/s1/ep2 [WEBDL-1080p].mkv", "Show", WorkUnitType.Episode, 2m, 1m),
                File(2, MediaType.Music, "rip/track.flac", "Album", WorkUnitType.Track, 3m, unitTitle: "Song: One"),
                File(3, MediaType.Book, "manga/half.cbz", "Manga", WorkUnitType.Volume, 1.5m, subtype: MediaSubtype.Manga),
                File(4, MediaType.Game, "roms/game.sfc", "Game"),
                File(5, MediaType.Movie, "gone.mkv", "Gone", missing: DateTime.UtcNow),
                File(6, MediaType.Movie, "kept (2019)/KEPT (2019).mkv", "Kept"),
                File(7, MediaType.Book, "loose.epub", "Book"),
                File(8, MediaType.Movie, "wide.mkv", "Wide", width: 1920)], NamingPatternTests.Defaults);

            CollectionAssert.AreEqual(new[]
            {
                "Show/Season 01/Show - S01E02 [WEBDL-1080p].mkv", "Artist A, Artist B/Album (2019)/03 - Song One.flac", "Manga/Manga v01.5.cbz", "Artist A/Book (2019)/Book.epub",
                "Wide (2019)/Wide (2019) [1080p].mkv"
            }, plan.Select(rename => rename.TargetPath).ToArray());
        }

        internal static async Task ScanAsync(Host host, long rootId)
        {
            using var queued = await host.SendAsync(HttpMethod.Post, Roots + "/" + rootId + "/scans");
            Assert.AreEqual(HttpStatusCode.OK, queued.StatusCode, await queued.Content.ReadAsStringAsync());
            await using var scope = host.Services.CreateAsyncScope();
            Assert.IsTrue(await OperationWorker.RunNextAsync(scope.ServiceProvider, NullLogger.Instance, CancellationToken.None));
        }

        internal static async Task<long> CreateMountAsync(Host host)
        {
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts",
                new SaveStorageMountDataV1("NAS", StorageProtocolType.Smb, "nas.test", "/media", "3.1.1", "jularr", null, true, "secret", "00:11:22:AA:BB:CC", "127.0.0.1", true, isVolume: true));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            return (await created.Content.ReadFromJsonAsync<CreateStorageMountResultV1>())!.Id;
        }

        internal static async Task<long> CreateRootAsync(Host host, long mountId, string path, MediaType mediaType, MediaSubtype? subtype = null)
        {
            using var created = await host.SendAsync(HttpMethod.Post, Roots, new CreateLibraryRootDataV1(path, mediaType, mountId, path, subtype));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            return (await created.Content.ReadFromJsonAsync<CreateLibraryRootResultV1>())!.Id;
        }

        internal static async Task<Host> CreateHostAsync(string mountRoot, bool razor = false, IReadOnlyDictionary<string, string>? settings = null,
            Action<IServiceCollection>? services = null)
        {
            var configuration = new Dictionary<string, string>(settings ?? new Dictionary<string, string>())
            {
                ["Storage:MountRoot"] = mountRoot,
                ["Storage:LocalPaths:0"] = "/media/library",
                ["Storage:LocalPaths:1"] = mountRoot
            };
            var host = await Host.CreateAsync(razor: razor, library: true, settings: configuration, services: services);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            return host;
        }

        internal static string Token(string html) =>
            WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);

        internal sealed class TemporaryDirectory : IDisposable
        {
            public string Path { get; } = Directory.CreateTempSubdirectory("jularr-library-").FullName;

            public void Dispose() => Directory.Delete(Path, true);
        }
    }
}
