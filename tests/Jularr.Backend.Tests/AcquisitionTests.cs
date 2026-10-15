using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Integrations.Data.Newznab;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Sabnzbd;
using Jularr.Integrations.Logic.Storage;
using Jularr.Integrations.Logic.Tmdb;
using Jularr.Frontend.Framework;
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
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode, await searched.Content.ReadAsStringAsync());
            var candidates = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.ToArray();
            Assert.AreEqual(Feature, candidates[0].Title);
            Assert.AreEqual(ReleaseDecisionType.Eligible, candidates[0].ReleaseDecisionTypeId);
            Assert.AreEqual("HigherQuality", candidates[0].ReasonCode);
            var wrong = candidates.Single(candidate => candidate.Title.StartsWith("Other.Movie", StringComparison.Ordinal));
            Assert.AreEqual(ReleaseDecisionType.Rejected, wrong.ReleaseDecisionTypeId);
            Assert.AreEqual("WrongTitle", wrong.ReasonCode);
            Assert.AreEqual("WrongYear", candidates.Single(candidate => candidate.Title.Contains("1999", StringComparison.Ordinal)).ReasonCode);

            // Rejected candidates are not grabbable, not even by a direct call.
            using var rejected = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(wrong.CandidateId));
            Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.AreEqual(0, usenet.Grabbed.Count);

            using var grabbed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(candidates[0].CandidateId));
            Assert.AreEqual(HttpStatusCode.OK, grabbed.StatusCode, await grabbed.Content.ReadAsStringAsync());
            Assert.AreEqual("https://indexer.example.test/getnzb/feature", usenet.Grabbed.Single());
            Assert.AreEqual((short)4, await host.ScalarAsync("SELECT \"AcquisitionRequestStatusTypeId\" FROM \"AcquisitionRequests\""));
            using var repeated = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(candidates[1].CandidateId));
            Assert.AreEqual(HttpStatusCode.BadRequest, repeated.StatusCode);

            // Progress comes from the client queue.
            usenet.Queue = """{"queue":{"paused":false,"slots":[{"nzo_id":"SABnzbd_nzo_1","filename":"Example","percentage":"40","mb":"1024.0","mbleft":"614.4","timeleft":"0:12:30","status":"Downloading"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);
            var running = (await ReadActivityAsync(host, ActivityTabType.Activity)).Single();
            Assert.AreEqual("SAB", running.ConnectionName);
            Assert.AreEqual(DownloadStateType.Downloading, running.DownloadStateTypeId);
            Assert.AreEqual(1024L * 1024 * 1024, running.SizeBytes);
            Assert.AreEqual(750, running.EtaSeconds);
            usenet.Queue = """{"queue":{"paused":true,"slots":[{"nzo_id":"SABnzbd_nzo_1","filename":"Example","percentage":"40","timeleft":"0:00:00","status":"Downloading"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);
            running = (await ReadActivityAsync(host, ActivityTabType.Activity)).Single();
            Assert.AreEqual(DownloadStateType.Paused, running.DownloadStateTypeId);
            Assert.IsNull(running.EtaSeconds);
            Assert.AreEqual(1024L * 1024 * 1024, running.SizeBytes);
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

            // Unpacking is shown until the client reports completion.
            usenet.History = """{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_1","name":"Example","status":"Extracting"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.AreEqual(DownloadStateType.PostProcessing, (await ReadActivityAsync(host, ActivityTabType.Activity)).Single().DownloadStateTypeId);

            // A path outside the client mapping is never read.
            usenet.History = """{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_1","name":"Example","status":"Completed","storage":"/elsewhere/../downloads/x"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.AreEqual((short)2, await host.ScalarAsync("SELECT \"OperationStatusTypeId\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
            var waiting = (await ReadActivityAsync(host, ActivityTabType.ToDo)).Single();
            Assert.AreEqual("import_path_unmapped", waiting.ReasonKey);
            Assert.AreEqual("Example Movie", waiting.WorkTitle);
            Assert.AreEqual(0, (await ReadActivityAsync(host, ActivityTabType.Activity)).Count);

            // An existing library file is kept; the import takes a free name.
            var library = Path.Combine(storage.Path, "1", "Movies", "Example Movie (2020)");
            Directory.CreateDirectory(library);
            File.WriteAllText(Path.Combine(library, "Example Movie (2020) [WEB-1080p].mkv"), "existing");

            // The film's worse earlier release, already played, is replaced: in simple mode it waits in the library's recycle folder.
            File.WriteAllText(Path.Combine(library, "Example Movie (2020) [DVD].mkv"), "earlier");
            await host.Fixture.RunSqlAsync("""
                UPDATE "InstanceSettings" SET "RecycleModeId" = 2;
                INSERT INTO "WorkEditions" ("Id", "WorkId") SELECT 900, "Id" FROM "Works";
                INSERT INTO "WorkVersions" ("Id", "WorkId", "WorkEditionId") SELECT 900, "Id", 900 FROM "Works";
                INSERT INTO "MediaAssets" ("Id", "WorkId", "WorkVersionId", "MediaAssetTypeId") SELECT 900, "Id", 900, 1 FROM "Works";
                INSERT INTO "StoredFiles" ("Id", "LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId")
                    SELECT 900, "Id", 'Example Movie (2020)/Example Movie (2020) [DVD].mkv', 7, now(), now(), 900 FROM "LibraryRoots" WHERE "RelativePath" = 'Movies';
                UPDATE "StoredFiles" SET "QualityKey" = 'WEB-720p' WHERE "Id" = 900;
                INSERT INTO "PlaybackSessions" ("PublicId", "ProfileId", "MediaAssetId", "StoredFileId", "PlaybackModeTypeId", "StartedAt", "LastSeenAt", "EndedAt")
                    VALUES (gen_random_uuid(), 101, 900, 900, 1, now() - interval '1 hour', now(), now());
                """);
            usenet.History = $$$"""{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_1","name":"Example","status":"Completed","storage":"/downloads/{{{Feature}}}"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);

            Assert.AreEqual("existing", File.ReadAllText(Path.Combine(library, "Example Movie (2020) [WEB-1080p].mkv")));
            Assert.IsFalse(File.Exists(Path.Combine(library, "Example Movie (2020) [DVD].mkv")));
            Assert.AreEqual("earlier", File.ReadAllText(Path.Combine(storage.Path, "1", "Movies", ".jularr-recycle", "Example Movie (2020)", "Example Movie (2020) [DVD].mkv")));
            Assert.AreEqual("Movies/.jularr-recycle", await host.ScalarAsync("SELECT \"FolderPath\" FROM \"RecycledFiles\""));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"MediaAssets\" WHERE \"Id\" = 900"));
            Assert.AreEqual("Example Movie (2020)/Example Movie (2020) [WEB-1080p] (2).mkv", await host.ScalarAsync(
                "SELECT file.\"RelativePath\" FROM \"PlaybackSessions\" AS session JOIN \"StoredFiles\" AS file ON file.\"Id\" = session.\"StoredFileId\""));
            Assert.IsTrue(File.Exists(Path.Combine(library, "Example Movie (2020) [WEB-1080p] (2).mkv")));
            Assert.IsTrue(File.Exists(Path.Combine(library, "Example Movie (2020) [WEB-1080p] (2).en.srt")));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"MediaAssetId\" IS NOT NULL"));
            Assert.AreEqual((short)2, await host.ScalarAsync("SELECT \"AcquisitionReleaseStatusTypeId\" FROM \"AcquisitionReleases\""));

            // The grabbed release names the imported file's facts.
            Assert.AreEqual("GRP", await host.ScalarAsync("SELECT \"ReleaseGroup\" FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '%(2).mkv'"));
            Assert.AreEqual("WEB-1080p", await host.ScalarAsync("SELECT \"QualityKey\" FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '%(2).mkv'"));
            Assert.AreEqual((short)6, await host.ScalarAsync("SELECT \"AcquisitionRequestStatusTypeId\" FROM \"AcquisitionRequests\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 1 AND \"OperationStatusTypeId\" = 1"));
            using var history = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/wanted/" + wantedId + "/releases");
            var release = (await history.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseV1>>())!.Single();
            Assert.AreEqual(AcquisitionReleaseStatusType.Imported, release.AcquisitionReleaseStatusTypeId);
            Assert.AreEqual(100, release.ProgressPercent);
            Assert.AreEqual(OperationType.Download, (await ReadActivityAsync(host, ActivityTabType.History)).Single().OperationTypeId);
            Assert.AreEqual(0, (await ReadActivityAsync(host, ActivityTabType.ToDo)).Count);
        }

        [TestMethod]
        public async Task AutomaticSearch_GrabsTheBestRelease_AndSkipsItAfterAReleaseFault()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var usenet = new FakeUsenet();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, usenet, razor: true);
            await CreateWantedMovieAsync(host);

            await RunAsync<SearchDueWantedV1>(host);
            Assert.AreEqual("https://indexer.example.test/getnzb/feature", usenet.Grabbed.Single());
            Assert.AreEqual("grabbed", await host.ScalarAsync("SELECT \"LastOutcomeKey\" FROM \"WantedItems\""));

            // A broken release is failed, never retried, and the next best one is taken.
            usenet.History = """{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_1","name":"Example","status":"Failed","fail_message":"Not enough articles"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.AreEqual("download_failed", await host.ScalarAsync("SELECT \"ReasonKey\" FROM \"AcquisitionReleases\""));

            // The failure waits in To-Do until it is retried.
            using var todo = await host.SendAsync(HttpMethod.Get, "/Admin/Activity?Tab=ToDo");
            var html = await todo.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, todo.StatusCode, html);
            StringAssert.Contains(html, "Download failed");
            var failedId = (await ReadActivityAsync(host, ActivityTabType.ToDo)).Single().Id;
            using var retried = await host.SendAsync(HttpMethod.Post, "/Admin/Activity?Tab=ToDo&PageNumber=1&handler=Retry&operationId=" + failedId,
                new Dictionary<string, string> { ["__RequestVerificationToken"] = LibraryTests.Token(html) });
            Assert.AreEqual(HttpStatusCode.Redirect, retried.StatusCode, await retried.Content.ReadAsStringAsync());
            Assert.AreEqual(0, (await ReadActivityAsync(host, ActivityTabType.ToDo)).Count);
            Assert.AreEqual(failedId, (await ReadActivityAsync(host, ActivityTabType.History)).Single().Id);
            using var again = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/activity/" + failedId + "/dismiss");
            Assert.AreEqual(HttpStatusCode.NotFound, again.StatusCode);
            await RunAsync<SearchDueWantedV1>(host);
            CollectionAssert.AreEqual(new[] { "https://indexer.example.test/getnzb/feature", "https://indexer.example.test/getnzb/lower" }, usenet.Grabbed);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionReleases\""));

            // Nothing new is searched while a download runs.
            await RunAsync<SearchDueWantedV1>(host);
            Assert.AreEqual(2, usenet.Grabbed.Count);
        }

        [TestMethod]
        public async Task Retry_AfterAClientFault_TakesTheSameReleaseAgain()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var usenet = new FakeUsenet();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, usenet);
            await CreateWantedMovieAsync(host);
            await RunAsync<SearchDueWantedV1>(host);

            // A client fault keeps the release; Retry searches now and reuses its operation.
            usenet.History = """{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_1","name":"Example","status":"Failed","fail_message":"Post-processing script failed"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);
            var failed = (await ReadActivityAsync(host, ActivityTabType.ToDo)).Single();
            Assert.AreEqual("download_interrupted", failed.ReasonKey);
            using var retried = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/activity/" + failed.Id + "/retry");
            Assert.AreEqual(HttpStatusCode.OK, retried.StatusCode, await retried.Content.ReadAsStringAsync());
            usenet.History = """{"history":{"slots":[]}}""";
            await RunAsync<SearchDueWantedV1>(host);
            CollectionAssert.AreEqual(new[] { "https://indexer.example.test/getnzb/feature", "https://indexer.example.test/getnzb/feature" }, usenet.Grabbed);
            Assert.AreEqual(failed.Id, (await ReadActivityAsync(host, ActivityTabType.Activity)).Single().Id);
        }

        [TestMethod]
        public async Task Profiles_EditKeepsRulesSwitchesDefaultsAndApplyPerTarget()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var usenet = new FakeUsenet();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, usenet, razor: true);
            var wantedId = await CreateWantedMovieAsync(host);
            const string Profiles = "/api/v1/admin/acquisition/profiles";
            var seeded = await ReadProfilesAsync(host);
            var movies = seeded.Single(profile => profile.MediaTypeId == MediaType.Movie);
            var series = seeded.Single(profile => profile.MediaTypeId == MediaType.Series);
            Assert.IsTrue(movies.IsDefault);
            CollectionAssert.Contains(movies.AllowedQualities, "WEB-1080p");

            // A cutoff outside the allowed qualities is rejected.
            using var invalid = await host.SendAsync(HttpMethod.Post, Profiles,
                new SaveAcquisitionProfileDataV1("4K", MediaType.Movie, false, ["WEB-2160p"], true, "WEB-1080p", null, null, [], []));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);

            // A new default replaces the old one; the old default keeps its score rules.
            using var created = await host.SendAsync(HttpMethod.Post, Profiles,
                new SaveAcquisitionProfileDataV1("4K", MediaType.Movie, true, ["WEB-2160p", "BLURAY-2160p"], true, "BLURAY-2160p", 1000, 80000, ["HDR"], ["CAM"]));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var uhdId = (await created.Content.ReadFromJsonAsync<CreateAcquisitionProfileResultV1>())!.Id;
            var current = await ReadProfilesAsync(host);
            var uhd = current.Single(profile => profile.Id == uhdId);
            Assert.IsTrue(uhd.IsDefault);
            CollectionAssert.AreEqual(new[] { "BLURAY-2160p", "WEB-2160p" }, uhd.AllowedQualities);
            Assert.AreEqual(1000, uhd.MinimumSizeMb);
            CollectionAssert.AreEqual(new[] { "CAM" }, uhd.MustNotContain);
            movies = current.Single(profile => profile.Id == movies.Id);
            Assert.IsFalse(movies.IsDefault);
            using var renamed = await host.SendAsync(HttpMethod.Put, Profiles + "/" + movies.Id, new SaveAcquisitionProfileDataV1("HD", MediaType.Movie, false, ["WEB-1080p"], false, null,
                null, null, [], [], movies.Version));
            Assert.AreEqual(HttpStatusCode.OK, renamed.StatusCode, await renamed.Content.ReadAsStringAsync());
            StringAssert.Contains((string)(await host.ScalarAsync("SELECT \"Definition\"::text FROM \"AcquisitionProfiles\" WHERE \"Id\" = " + movies.Id))!, "Prefer Proper");

            // A default is replaced, never removed or deleted.
            using var undefault = await host.SendAsync(HttpMethod.Put, Profiles + "/" + uhdId, new SaveAcquisitionProfileDataV1("4K", MediaType.Movie, false, ["WEB-2160p"], false, null,
                null, null, [], [], uhd.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, undefault.StatusCode);
            using var deleteDefault = await host.SendAsync(HttpMethod.Delete, Profiles + "/" + uhdId + "?version=" + uhd.Version);
            Assert.AreEqual(HttpStatusCode.BadRequest, deleteDefault.StatusCode);

            // A target takes only a profile of its media type.
            using var wantedRead = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/wanted/" + wantedId);
            var wanted = (await wantedRead.Content.ReadFromJsonAsync<WantedDetailsV1>())!;
            using var mismatch = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/" + wantedId, new UpdateWantedDataV1(false, wanted.Version, series.Id));
            Assert.AreEqual(HttpStatusCode.BadRequest, mismatch.StatusCode);
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/" + wantedId, new UpdateWantedDataV1(false, wanted.Version, movies.Id));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            Assert.AreEqual(1, (await ReadProfilesAsync(host)).Single(profile => profile.Id == movies.Id).WantedCount);
            using var currentTab = await host.SendAsync(HttpMethod.Get, "/Admin/Wanted?WantedItemId=" + wantedId + "&Tab=current");
            var currentHtml = await currentTab.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, currentTab.StatusCode, currentHtml);
            StringAssert.Contains(currentHtml, "value=\"" + movies.Id + "\" selected=\"selected\"");

            // The chosen profile drives the search: only WEB-1080p is eligible.
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            var candidates = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!;
            Assert.AreEqual(ReleaseDecisionType.Eligible, candidates.Single(candidate => candidate.Title == Feature).ReleaseDecisionTypeId);
            Assert.AreEqual(ReleaseDecisionType.Rejected, candidates.Single(candidate => candidate.Title == Lower).ReleaseDecisionTypeId);

            // Deleting a used profile returns its targets to the default.
            movies = (await ReadProfilesAsync(host)).Single(profile => profile.Id == movies.Id);
            using var deleted = await host.SendAsync(HttpMethod.Delete, Profiles + "/" + movies.Id + "?version=" + movies.Version);
            Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"AcquisitionProfileId\" FROM \"WantedItems\""));

            // The editor renders the stored values and saves through the same service.
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Profiles?AcquisitionProfileId=" + uhdId);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "value=\"BLURAY-2160p\" checked=\"checked\"");
            StringAssert.Contains(html, "value=\"CAM\"");
            using var saved = await host.SendAsync(HttpMethod.Post, "/Admin/Acquisition/Profiles?AcquisitionProfileId=" + uhdId, new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", LibraryTests.Token(html)), new("MediaTypeId", "1"), new("Version", uhd.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("Name", "UHD"), new("IsDefault", "True"), new("AllowedQualities", "WEB-2160p"), new("AllowedQualities", "WEB-1080p"), new("UpgradeAllowed", "True"),
                new("UpgradeCutoffQuality", "WEB-2160p"), new("MustContain", "HDR, DV"), new("MustNotContain", "")
            ]));
            Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            uhd = (await ReadProfilesAsync(host)).Single(profile => profile.Id == uhdId);
            Assert.AreEqual("UHD", uhd.Name);
            CollectionAssert.AreEqual(new[] { "WEB-2160p", "WEB-1080p" }, uhd.AllowedQualities);
            CollectionAssert.AreEqual(new[] { "HDR", "DV" }, uhd.MustContain);
        }

        [TestMethod]
        public async Task Blocklist_BlocksAndAllowsReleasesOfTheTarget()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var usenet = new FakeUsenet();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, usenet);
            var wantedId = await CreateWantedMovieAsync(host);
            await RunAsync<SearchDueWantedV1>(host);
            usenet.History = """{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_1","name":"Example","status":"Failed","fail_message":"Not enough articles"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);

            // A release fault blocks; allowing it lets the search take it again.
            var release = (await ReadReleasesAsync(host, wantedId)).Single();
            Assert.IsTrue(release.IsBlocked);
            var address = "/api/v1/admin/wanted/" + wantedId + "/releases/" + release.Id;
            using var allowed = await host.SendAsync(HttpMethod.Put, address, new MarkWantedReleaseDataV1(AcquisitionReleaseStatusType.Allowed));
            Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode, await allowed.Content.ReadAsStringAsync());
            release = (await ReadReleasesAsync(host, wantedId)).Single();
            Assert.AreEqual(AcquisitionReleaseStatusType.Allowed, release.AcquisitionReleaseStatusTypeId);
            Assert.IsFalse(release.IsBlocked);
            using var again = await host.SendAsync(HttpMethod.Put, address, new MarkWantedReleaseDataV1(AcquisitionReleaseStatusType.Allowed));
            Assert.AreEqual(HttpStatusCode.BadRequest, again.StatusCode);

            // A manual block wins over the ranking; other targets cannot touch it.
            using var blocked = await host.SendAsync(HttpMethod.Put, address, new MarkWantedReleaseDataV1(AcquisitionReleaseStatusType.Blocked));
            Assert.AreEqual(HttpStatusCode.OK, blocked.StatusCode, await blocked.Content.ReadAsStringAsync());
            using var foreign = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/1/releases/" + release.Id, new MarkWantedReleaseDataV1(AcquisitionReleaseStatusType.Allowed));
            Assert.AreEqual(HttpStatusCode.BadRequest, foreign.StatusCode);
            usenet.History = """{"history":{"slots":[]}}""";
            await host.Fixture.RunSqlAsync("UPDATE \"WantedItems\" SET \"NextSearchAt\" = now()");
            await RunAsync<SearchDueWantedV1>(host);
            CollectionAssert.AreEqual(new[] { "https://indexer.example.test/getnzb/feature", "https://indexer.example.test/getnzb/lower" }, usenet.Grabbed);
        }

        [TestMethod]
        public async Task Movie_Upgrade_ReplacesOnlyWorseAutomaticVersionsUntilTheAdministratorKeepsOne()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var usenet = new FakeUsenet();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, usenet);
            var wantedId = await CreateWantedMovieAsync(host);
            var library = Path.Combine(storage.Path, "1", "Movies", "Example Movie (2020)");
            Directory.CreateDirectory(library);
            foreach (var name in new[] { "DVD", "Manual", "Bluray", "Unknown" })
            {
                File.WriteAllText(Path.Combine(library, "Example Movie (2020) [" + name + "].mkv"), name);
            }

            // A worse earlier release, one added by hand, a better one and one of unknown quality.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "WorkEditions" ("Id", "WorkId") SELECT 900, "Id" FROM "Works";
                INSERT INTO "WorkVersions" ("Id", "WorkId", "WorkEditionId") SELECT version, "Id", 900 FROM "Works", unnest(ARRAY[900, 901, 902, 903]) AS version;
                INSERT INTO "MediaAssets" ("Id", "WorkId", "WorkVersionId", "MediaAssetTypeId", "IsProtected") SELECT asset, "Id", asset, 1, asset = 901 FROM "Works", unnest(ARRAY[900, 901, 902, 903]) AS asset;
                INSERT INTO "StoredFiles" ("Id", "LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId", "QualityKey")
                    SELECT item.id, root."Id", 'Example Movie (2020)/Example Movie (2020) [' || item.name || '].mkv', 3, now(), now(), item.id, item.quality
                    FROM "LibraryRoots" AS root, (VALUES (900, 'DVD', 'WEB-720p'), (901, 'Manual', NULL), (902, 'Bluray', 'BLURAY-1080p'), (903, 'Unknown', NULL)) AS item(id, name, quality)
                    WHERE root."RelativePath" = 'Movies';
                """);

            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            var candidate = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.First();
            using var grabbed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(candidate.CandidateId));
            Assert.AreEqual(HttpStatusCode.OK, grabbed.StatusCode, await grabbed.Content.ReadAsStringAsync());
            var completed = Path.Combine(downloads.Path, Feature);
            Directory.CreateDirectory(completed);
            File.WriteAllText(Path.Combine(completed, Feature + ".mkv"), new string('x', 4096));
            usenet.Queue = FakeUsenet.EmptyQueue;
            usenet.History = $$$"""{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_1","name":"Example","status":"Completed","storage":"/downloads/{{{Feature}}}"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);

            // Deleted by default: only the worse automatic release; the manual, the better and the unknown one stay.
            Assert.IsTrue(File.Exists(Path.Combine(library, "Example Movie (2020) [WEB-1080p].mkv")));
            Assert.IsFalse(File.Exists(Path.Combine(library, "Example Movie (2020) [DVD].mkv")));
            Assert.IsTrue(File.Exists(Path.Combine(library, "Example Movie (2020) [Manual].mkv")));
            Assert.IsTrue(File.Exists(Path.Combine(library, "Example Movie (2020) [Bluray].mkv")));
            Assert.IsTrue(File.Exists(Path.Combine(library, "Example Movie (2020) [Unknown].mkv")));
            Assert.IsFalse(Directory.Exists(Path.Combine(storage.Path, "1", "Movies", ".jularr-recycle")));

            // Keeping only the new version is the administrator's confirmation.
            var newFile = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '%WEB-1080p].mkv'"))!;
            using var files = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/library/files?onlyUnmatched=false");
            Assert.IsTrue((await files.Content.ReadFromJsonAsync<PageResult<Jularr.Backend.Data.Admin.Library.V1.LibraryFileDetailsV1>>())!.Items.Single(file => file.Id == newFile).HasOtherVersions);
            using var protectedNew = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + newFile + "/protection",
                new Jularr.Backend.Data.Admin.Library.V1.ProtectLibraryFileDataV1(true));
            Assert.AreEqual(HttpStatusCode.OK, protectedNew.StatusCode, await protectedNew.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT asset.\"IsProtected\" FROM \"MediaAssets\" AS asset JOIN \"StoredFiles\" AS file ON file.\"MediaAssetId\" = asset.\"Id\" WHERE file.\"Id\" = " + newFile));
            using var kept = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/library/files/" + newFile + "/keep-only");
            Assert.AreEqual(HttpStatusCode.OK, kept.StatusCode, await kept.Content.ReadAsStringAsync());
            Assert.AreEqual(1, Directory.GetFiles(library).Length);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"MediaAssets\""));
        }

        [TestMethod]
        public async Task Plex_ConnectSyncsAvailabilityAndRefreshesAfterImport()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var usenet = new FakeUsenet();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, usenet, razor: true);
            var wantedId = await CreateWantedMovieAsync(host);
            await host.Fixture.RunSqlAsync("INSERT INTO \"WorkExternalIdentities\" (\"WorkId\", \"Namespace\", \"Value\") SELECT \"WorkId\", 'tmdb_movie', '603' FROM \"WantedItems\"");
            const string Plex = "/api/v1/admin/providers/plex";

            // A wrong token is not stored.
            using var rejected = await host.SendAsync(HttpMethod.Post, Plex, new SavePlexServerDataV1("http://plex.example.test:32400", "wrong"));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            StringAssert.Contains(await rejected.Content.ReadAsStringAsync(), "plex_authentication_failed");
            using var connected = await host.SendAsync(HttpMethod.Post, Plex, new SavePlexServerDataV1("http://plex.example.test:32400", FakeUsenet.PlexToken));
            Assert.AreEqual(HttpStatusCode.OK, connected.StatusCode, await connected.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(new[] { "Movies", "Shows" }, (await connected.Content.ReadFromJsonAsync<PlexServerTestV1>())!.Sections);
            using var read = await host.SendAsync(HttpMethod.Get, Plex);
            var json = await read.Content.ReadAsStringAsync();
            Assert.IsFalse(json.Contains(FakeUsenet.PlexToken, StringComparison.Ordinal));
            var server = (await read.Content.ReadFromJsonAsync<PlexServerDetailsV1>())!;
            Assert.AreEqual("plexmachine1", server.ExternalId);
            Assert.AreEqual("http://plex.example.test:32400/", server.BaseUrl);

            // Known titles are linked, unknown ones become Works; items without ids are skipped.
            using var synced = await host.SendAsync(HttpMethod.Post, Plex + "/sync");
            Assert.AreEqual(HttpStatusCode.OK, synced.StatusCode, await synced.Content.ReadAsStringAsync());
            var result = (await synced.Content.ReadFromJsonAsync<PlexSyncResultV1>())!;
            Assert.AreEqual(3, result.LinkedCount);
            Assert.AreEqual(2, result.CreatedCount);
            using var library = await host.SendAsync(HttpMethod.Get, "/api/v1/library");
            var items = (await library.Content.ReadFromJsonAsync<PageResult<LibraryItemV1>>())!.Items;
            Assert.AreEqual(3, items.Count);
            Assert.IsTrue(items.All(item => item.MediaStateTypeId == MediaStateType.Available));
            var movieId = items.Single(item => item.Title == "Example Movie").Id;
            using var details = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + movieId);
            Assert.AreEqual("https://app.plex.tv/desktop/#!/server/plexmachine1/details?key=%2Flibrary%2Fmetadata%2F11",
                (await details.Content.ReadFromJsonAsync<Jularr.Backend.Data.User.Media.V1.WorkDetailsV1>())!.PlexUrl);
            using var page = await host.SendAsync(HttpMethod.Get, "/Details/" + movieId);
            StringAssert.Contains(await page.Content.ReadAsStringAsync(), "Open in Plex");
            using var providers = await host.SendAsync(HttpMethod.Get, "/Admin/Providers");
            var providersHtml = await providers.Content.ReadAsStringAsync();
            StringAssert.Contains(providersHtml, "3 titles linked");
            Assert.IsFalse(providersHtml.Contains(FakeUsenet.PlexToken, StringComparison.Ordinal));

            // A complete pass unlinks what Plex no longer has; the Work stays.
            usenet.PlexMovies = """{"MediaContainer":{"totalSize":1,"Metadata":[{"ratingKey":"11","title":"Example Movie","year":2020,"Guid":[{"id":"tmdb://603"}]}]}}""";
            using var again = await host.SendAsync(HttpMethod.Post, Plex + "/sync");
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode, await again.Content.ReadAsStringAsync());
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'plex'"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\" WHERE \"CanonicalTitle\" = 'Plex Only'"));

            // An import asks Plex to scan its movie libraries.
            await RunAsync<SearchDueWantedV1>(host);
            var completed = Path.Combine(downloads.Path, Feature);
            Directory.CreateDirectory(completed);
            File.WriteAllText(Path.Combine(completed, Feature + ".mkv"), new string('x', 4096));
            usenet.History = $$$"""{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_1","name":"Example","status":"Completed","storage":"/downloads/{{{Feature}}}"}]}}""";
            usenet.PlexRequests.Clear();
            await RunAsync<MonitorDownloadsV1>(host);
            CollectionAssert.Contains(usenet.PlexRequests, "/library/sections/1/refresh");
            CollectionAssert.DoesNotContain(usenet.PlexRequests, "/library/sections/2/refresh");

            // Disconnecting removes the links, not the Works.
            using var disconnected = await host.SendAsync(HttpMethod.Delete, Plex);
            Assert.AreEqual(HttpStatusCode.OK, disconnected.StatusCode, await disconnected.Content.ReadAsStringAsync());
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'plex'"));
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"ProtectedSecret\" FROM \"Providers\" WHERE \"Key\" = 'plex'"));
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
        public async Task Series_TmdbImportSeasonPackAndEpisodes_CompleteOnlyWhenAllAiredEpisodesExist()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var usenet = new FakeUsenet { Results = FakeUsenet.SeriesResults };
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, usenet);
            await host.Fixture.RunSqlAsync("UPDATE \"LibraryRoots\" SET \"MediaTypeId\" = 2");

            // Without a key TMDB is unavailable; the key is stored write-only.
            var search = new SearchMetadataDataV1(MediaType.Series, "Example Show");
            using var unavailable = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/metadata/search", search);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            var tmdb = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'tmdb'"))!;
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + tmdb + "/secret", new SaveProviderSecretDataV1(FakeUsenet.TmdbToken));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var providers = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/providers");
            Assert.IsFalse((await providers.Content.ReadAsStringAsync()).Contains(FakeUsenet.TmdbToken, StringComparison.Ordinal));

            using var found = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/metadata/search", search);
            Assert.AreEqual(HttpStatusCode.OK, found.StatusCode, await found.Content.ReadAsStringAsync());
            var result = (await found.Content.ReadFromJsonAsync<BaseDataList<MetadataSearchResultV1>>())!.Single();
            Assert.AreEqual(2020, result.ReleaseYear);
            Assert.IsNull(result.WorkId);

            // Import maps identities and the episode catalog; a repeat updates the same Work.
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Series, result.ExternalId));
            Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
            var work = (await imported.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            using var again = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Series, result.ExternalId));
            Assert.AreEqual(work.Id, (await again.Content.ReadFromJsonAsync<WorkDetailsV1>())!.Id);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT count(*) FROM \"WorkExternalIdentities\""));
            Assert.AreEqual(6L, await host.ScalarAsync("SELECT count(*) FROM \"WorkUnits\" WHERE \"WorkUnitTypeId\" = 2"));
            Assert.AreEqual("Beispiel Serie", await host.ScalarAsync("SELECT \"Title\" FROM \"WorkTitles\" WHERE \"Title\" <> 'Example Show'"));

            // A fully aired season comes as a pack, the aired episode of the running season alone.
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId", "AcquisitionRequestStatusTypeId", "DecidedByAccountId", "DecidedAt")
                VALUES (500, {work.Id}, 202, 42, 1, 2, 41, now());
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (600, {work.Id});
                INSERT INTO "RequestTargets" ("AcquisitionRequestId", "WantedItemId") VALUES (500, 600);
                """);
            await RunAsync<SearchDueWantedV1>(host);
            CollectionAssert.AreEquivalent(new[] { "https://indexer.example.test/getnzb/s1", "https://indexer.example.test/getnzb/s2e1" }, usenet.Grabbed);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionReleases\" WHERE \"ParentUnitNumber\" = 1 AND \"UnitStart\" IS NULL"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionReleases\" WHERE \"ParentUnitNumber\" = 2 AND \"UnitStart\" = 1 AND \"UnitEnd\" = 1"));

            // Claimed episodes are not searched again.
            await host.Fixture.RunSqlAsync("UPDATE \"WantedItems\" SET \"NextSearchAt\" = NULL");
            await RunAsync<SearchDueWantedV1>(host);
            Assert.AreEqual(2, usenet.Grabbed.Count);

            // The pack imports into the existing TMDB episodes; the request waits for season 2.
            var pack = Path.Combine(downloads.Path, "Example.Show.S01.1080p.WEB-DL.H264-GRP");
            Directory.CreateDirectory(pack);
            foreach (var episode in new[] { 1, 2, 3 })
            {
                File.WriteAllText(Path.Combine(pack, $"Example.Show.S01E0{episode}.1080p.WEB-DL.H264-GRP.mkv"), new string('x', 1000 + episode));
            }

            usenet.History = """{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_1","name":"Pack","status":"Completed","storage":"/downloads/Example.Show.S01.1080p.WEB-DL.H264-GRP"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.IsTrue(File.Exists(Path.Combine(storage.Path, "1", "Movies", "Example Show", "Season 01", "Example Show - S01E03 [WEB-1080p].mkv")));
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT count(*) FROM \"MediaAssets\" WHERE \"WorkUnitId\" IS NOT NULL"));
            Assert.AreEqual(6L, await host.ScalarAsync("SELECT count(*) FROM \"WorkUnits\" WHERE \"WorkUnitTypeId\" = 2"));
            Assert.AreEqual((short)4, await host.ScalarAsync("SELECT \"AcquisitionRequestStatusTypeId\" FROM \"AcquisitionRequests\""));

            var single = Path.Combine(downloads.Path, "Example.Show.S02E01.1080p.WEB-DL.H264-GRP");
            Directory.CreateDirectory(single);
            File.WriteAllText(Path.Combine(single, "Example.Show.S02E01.1080p.WEB-DL.H264-GRP.mkv"), new string('x', 2000));
            usenet.History = """{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_2","name":"Episode","status":"Completed","storage":"/downloads/Example.Show.S02E01.1080p.WEB-DL.H264-GRP"}]}}""";
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.AreEqual((short)6, await host.ScalarAsync("SELECT \"AcquisitionRequestStatusTypeId\" FROM \"AcquisitionRequests\""));
            using var wantedRead = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/wanted/600");
            Assert.IsTrue((await wantedRead.Content.ReadFromJsonAsync<WantedDetailsV1>())!.HasMedia);

            // Manual search can target one episode.
            using var manual = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/600/search", new SearchWantedDataV1(2, 1));
            var candidates = (await manual.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.ToArray();
            Assert.AreEqual("Example.Show.S02E01.1080p.WEB-DL.H264-GRP", candidates.First(candidate => candidate.ReleaseDecisionTypeId == ReleaseDecisionType.Eligible).Title);
            Assert.AreEqual("WrongSeason", candidates.Single(candidate => candidate.Title == "Example.Show.S01E01.1080p.WEB-DL.H264-GRP").ReasonCode);
        }

        [TestMethod]
        public async Task Monitoring_SpecialsStartOff_ASeasonSwitchesItsEpisodes_NewUnitsFollowTheTarget()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var usenet = new FakeUsenet { Results = FakeUsenet.SeriesResults };
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, usenet, razor: true);
            await host.Fixture.RunSqlAsync("UPDATE \"LibraryRoots\" SET \"MediaTypeId\" = 2");
            var tmdb = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'tmdb'"))!;
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + tmdb + "/secret", new SaveProviderSecretDataV1(FakeUsenet.TmdbToken));
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Series, "1399"));
            var work = (await imported.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId", "AcquisitionRequestStatusTypeId", "DecidedByAccountId", "DecidedAt")
                VALUES (500, {work.Id}, 202, 42, 1, 2, 41, now());
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (600, {work.Id});
                INSERT INTO "RequestTargets" ("AcquisitionRequestId", "WantedItemId") VALUES (500, 600);
                """);

            // The list holds seasons with their counts; specials start unmonitored, every other episode monitored.
            async Task<WantedUnitV1[]> UnitsAsync(string query)
            {
                using var listed = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/wanted/600/units" + query);
                Assert.AreEqual(HttpStatusCode.OK, listed.StatusCode, await listed.Content.ReadAsStringAsync());
                return (await listed.Content.ReadFromJsonAsync<BaseDataList<WantedUnitV1>>())!.ToArray();
            }
            var units = await UnitsAsync("");
            Assert.IsTrue(units.All(unit => unit.WorkUnitTypeId == WorkUnitType.Season));
            var seasons = units.ToDictionary(unit => (int)unit.Number);
            Assert.AreEqual(0, seasons[0].MonitoredEpisodes);
            Assert.IsTrue(seasons[0].Episodes > 0);
            Assert.IsTrue(units.Where(unit => unit.Number > 0).All(unit => unit.MonitoredEpisodes == unit.Episodes));
            var episodes = await UnitsAsync("?seasonId=" + seasons[1].Id);
            Assert.IsTrue(episodes.Where(unit => unit.WorkUnitTypeId == WorkUnitType.Episode).All(unit => unit.ParentWorkUnitId == seasons[1].Id && unit.IsMonitored));

            // Season 1 off: only season 2's aired episode is searched.
            using var off = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/600/units/" + seasons[1].Id, new UpdateWantedUnitDataV1(false));
            Assert.AreEqual(HttpStatusCode.OK, off.StatusCode, await off.Content.ReadAsStringAsync());
            Assert.AreEqual(0L, await host.ScalarAsync($"SELECT count(*) FROM \"WorkUnits\" WHERE \"ParentWorkUnitId\" = {seasons[1].Id} AND \"IsMonitored\""));
            await RunAsync<SearchDueWantedV1>(host);
            CollectionAssert.AreEqual(new[] { "https://indexer.example.test/getnzb/s2e1" }, usenet.Grabbed);
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"NextSearchAt\" IS NULL FROM \"WantedItems\" WHERE \"Id\" = 600"));

            // The Monitoring tab shows the same and switches an episode back on.
            using var tab = await host.SendAsync(HttpMethod.Get, "/Admin/Wanted?PageNumber=1&WantedItemId=600&Tab=monitoring");
            var html = await tab.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, tab.StatusCode, html);
            StringAssert.Contains(html, "Season 1");
            StringAssert.Contains(html, "Not monitored");
            Assert.IsFalse(html.Contains("S01E02"), "Episodes appear only for the opened season.");
            using var opened = await host.SendAsync(HttpMethod.Get, "/Admin/Wanted?PageNumber=1&WantedItemId=600&Tab=monitoring&SeasonId=" + seasons[1].Id);
            html = await opened.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "S01E02");
            var episode = episodes.First(unit => unit.WorkUnitTypeId == WorkUnitType.Episode);
            var other = episodes.Last(unit => unit.WorkUnitTypeId == WorkUnitType.Episode);
            using var on = await host.SendAsync(HttpMethod.Post, "/Admin/Wanted?PageNumber=1&WantedItemId=600&Tab=monitoring&SeasonId=" + seasons[1].Id + "&handler=Unit&workUnitId=" + episode.Id,
                new Dictionary<string, string> { ["__RequestVerificationToken"] = LibraryTests.Token(html), ["IsMonitored"] = "True" });
            Assert.AreEqual(HttpStatusCode.Redirect, on.StatusCode, await on.Content.ReadAsStringAsync());
            StringAssert.Contains(on.Headers.Location!.OriginalString, "SeasonId=" + seasons[1].Id);
            Assert.AreEqual(true, await host.ScalarAsync($"SELECT \"IsMonitored\" FROM \"WorkUnits\" WHERE \"Id\" = {episode.Id}"));
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"NextSearchAt\" IS NULL FROM \"WantedItems\" WHERE \"Id\" = 600"), "Switching on searches in the next cycle.");
            using var partly = await host.SendAsync(HttpMethod.Get, "/Admin/Wanted?PageNumber=1&WantedItemId=600&Tab=monitoring");
            StringAssert.Contains(await partly.Content.ReadAsStringAsync(), "Partly monitored");

            // A unit of another Work is not reachable through this target and stays untouched.
            var foreignId = (long)(await host.ScalarAsync($"""
                WITH other AS (INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (2, 'Other') RETURNING "Id")
                INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number") SELECT "Id", 1, 1 FROM other RETURNING "Id";
                """))!;
            using var foreign = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/600/units/" + foreignId, new UpdateWantedUnitDataV1(false));
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
            Assert.AreEqual(true, await host.ScalarAsync($"SELECT \"IsMonitored\" FROM \"WorkUnits\" WHERE \"Id\" = {foreignId}"));

            // New units follow the target's choice: not monitored once it is switched off.
            var version = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"WantedItems\""))!;
            using var updated = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/wanted/600", new UpdateWantedDataV1(false, version, monitorsNewUnits: false));
            Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
            using var detail = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/wanted/600");
            Assert.IsFalse((await detail.Content.ReadFromJsonAsync<WantedDetailsV1>())!.MonitorsNewUnits);
            await host.Fixture.RunSqlAsync($"DELETE FROM \"WorkUnits\" WHERE \"ParentWorkUnitId\" = {seasons[2].Id} AND \"Number\" = 2");
            using var refreshed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Series, "1399"));
            Assert.AreEqual(HttpStatusCode.OK, refreshed.StatusCode, await refreshed.Content.ReadAsStringAsync());
            Assert.AreEqual(false, await host.ScalarAsync($"SELECT \"IsMonitored\" FROM \"WorkUnits\" WHERE \"ParentWorkUnitId\" = {seasons[2].Id} AND \"Number\" = 2"));
            Assert.AreEqual(true, await host.ScalarAsync($"SELECT \"IsMonitored\" FROM \"WorkUnits\" WHERE \"ParentWorkUnitId\" = {seasons[2].Id} AND \"Number\" = 1"));
            Assert.AreEqual(false, await host.ScalarAsync($"SELECT \"IsMonitored\" FROM \"WorkUnits\" WHERE \"Id\" = {other.Id}"), "A refresh keeps the owner's choice.");
        }

        [TestMethod]
        public async Task Indexers_AFailingOneBacksOffWhileOthersSearch_ScopeManualOnlyLimitsAndKeylessHold()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var usenet = new FakeUsenet();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, usenet, razor: true);
            foreach (var data in new[]
            {
                new SaveAcquisitionConnectionDataV1("Open", AcquisitionConnectionType.Newznab, "https://open.example.test/", null, true, 20, [], null, null, null),
                new SaveAcquisitionConnectionDataV1("Broken", AcquisitionConnectionType.Newznab, "https://broken.example.test/", FakeUsenet.IndexerKey, true, 20, [], null, null, null),
                new SaveAcquisitionConnectionDataV1("Books", AcquisitionConnectionType.Newznab, "https://books.example.test/", FakeUsenet.IndexerKey, true, 20, [], null, null, null,
                    mediaTypeIds: [(int)MediaType.Book]),
                new SaveAcquisitionConnectionDataV1("Manual", AcquisitionConnectionType.Newznab, "https://manual.example.test/", FakeUsenet.IndexerKey, true, 20, [], null, null, null,
                    searchesAutomatically: false)
            })
            {
                using var created = await host.SendAsync(HttpMethod.Post, Connections, data);
                Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            }

            // Limits and media scope belong to indexers only.
            using var refused = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("SAB 2", AcquisitionConnectionType.Sabnzbd, "https://sab2.example.test",
                FakeUsenet.ClientKey, true, 10, [], null, null, null, queryLimit: 5));
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);

            int Asked(string name)
            {
                lock (usenet.IndexerHosts) return usenet.IndexerHosts.Count(asked => asked == name + ".example.test");
            }

            async Task<object?> ColumnAsync(string name, string column) =>
                await host.ScalarAsync($"SELECT \"{column}\" FROM \"AcquisitionConnections\" WHERE \"Name\" = '{name}'");

            // A manual search asks every indexer serving films, also manual-only and failing ones; the failure starts a backoff.
            var wantedId = await CreateWantedMovieAsync(host);
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode, await searched.Content.ReadAsStringAsync());
            Assert.IsTrue(Asked("open") > 0 && Asked("manual") > 0 && Asked("broken") > 0);
            Assert.AreEqual(0, Asked("books"));
            Assert.AreEqual(1, await ColumnAsync("Broken", "FailureCount"));
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"RetryAt\" > now() + interval '4 minutes' FROM \"AcquisitionConnections\" WHERE \"Name\" = 'Broken'"));
            Assert.AreEqual(0, await ColumnAsync("Open", "FailureCount"));
            Assert.IsTrue((int)(await ColumnAsync("Open", "QueryCount"))! > 0);

            // The automatic search leaves out the backed-off and the manual-only indexer and still grabs.
            var (broken, manual) = (Asked("broken"), Asked("manual"));
            await RunAsync<SearchDueWantedV1>(host);
            Assert.AreEqual(1, usenet.Grabbed.Count);
            Assert.AreEqual((broken, manual), (Asked("broken"), Asked("manual")));

            // An exhausted query limit stops an indexer; an exhausted grab limit refuses the grab (once the first download has failed).
            await host.Fixture.RunSqlAsync("""
                UPDATE "Operations" SET "OperationStatusTypeId" = 4, "FinishedAt" = now(), "AttentionKey" = NULL, "AttentionUrl" = NULL, "SourceLinkUrl" = NULL WHERE "OperationStatusTypeId" IN (1, 2);
                UPDATE "AcquisitionConnections" SET "QueryLimit" = "QueryCount" WHERE "Name" = 'Open';
                UPDATE "AcquisitionConnections" SET "GrabLimit" = 1, "GrabCount" = 1, "CountedOn" = current_date WHERE "AcquisitionConnectionTypeId" = 1;
                """);
            var open = Asked("open");
            using var again = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            Assert.AreEqual(open, Asked("open"));
            var candidate = (await again.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.First();
            using var limited = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(candidate.CandidateId));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, limited.StatusCode, await limited.Content.ReadAsStringAsync());
            StringAssert.Contains(await limited.Content.ReadAsStringAsync(), "acquisition_limit_reached");

            // The administration shows the backoff and today's use.
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "Failing, paused until");
            StringAssert.Contains(html, "1 / 1 ");
        }

        [TestMethod]
        public async Task Grab_ConcurrentCallsStartOneDownload_AndArrPlusSwitchesOffSeparately()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var usenet = new FakeUsenet();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, usenet);
            var wantedId = await CreateWantedMovieAsync(host);
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            var eligible = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.Where(candidate => candidate.ReleaseDecisionTypeId == ReleaseDecisionType.Eligible).ToArray();
            Assert.AreEqual(2, eligible.Length);

            // The reservation lets exactly one grab reach the client.
            var responses = await Task.WhenAll(eligible.Select(candidate => host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(candidate.CandidateId))));
            CollectionAssert.AreEquivalent(new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest }, responses.Select(response => response.StatusCode).ToArray());
            Assert.AreEqual(1, usenet.Grabbed.Count);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));

            // Requests keep working without Arr+.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Acquisition'");
            using var blocked = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            Assert.AreEqual(HttpStatusCode.Forbidden, blocked.StatusCode);
            using var list = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/wanted");
            Assert.AreEqual(HttpStatusCode.OK, list.StatusCode);
        }

        [TestMethod]
        public void Selection_RequestedLanguageWinsAmongEqualQuality()
        {
            var profile = new QualityProfile("1", "Test", ["WEB-1080p"], ["WEB-1080p"], true, "WEB-1080p", null, null, [], [], [], [], []) { LanguageOrder = ["de"] };
            var offers = new[] { Offer("Example.Movie.2020.1080p.WEB-DL.H264-AAA", "english"), Offer("Example.Movie.2020.German.DL.1080p.WEB-DL.H264-ZZZ", "german") };
            var result = ReleaseSelectionEngine.Select(profile, offers.Select(offer =>
            {
                var judgement = VideoReleaseJudge.Judge(AcquisitionKind.Movie, "Example Movie", 2020, null, UnitSet.Empty, offer);
                return new SelectionCandidate(offer.Identity, judgement.Parsed, offer.SizeBytes, offer.ConnectionName, offer.ConnectionPriority, offer.PublishedAt, judgement.Evidence,
                    judgement.Coverage) { Languages = judgement.Languages ?? [] };
            }).ToArray());

            Assert.AreEqual("1:german", result.Winner!.Candidate.Id);
        }

        [TestMethod]
        public void Import_PathsStayBelowTheMappingAndUrlsStaySafe()
        {
            Assert.AreEqual("Movie/file.mkv", ImportPlacementLogic.MapRemotePath("/downloads/Movie/file.mkv", "/downloads/"));
            Assert.IsNull(ImportPlacementLogic.MapRemotePath("/downloads/../etc/passwd", "/downloads"));
            Assert.IsNull(ImportPlacementLogic.MapRemotePath("/downloadsX/file", "/downloads"));
            Assert.IsNull(ImportPlacementLogic.MapRemotePath("/elsewhere/file", "/downloads"));
            if (OperatingSystem.IsLinux())
            {
                Assert.IsTrue(StorageFileSystem.IsMountPoint("/"));
                Assert.IsFalse(StorageFileSystem.IsMountPoint(Path.GetTempPath()));
            }

            Assert.Throws<ArgumentException>(() => new A().Attribute("href", "\u0001javascript:alert(1)"));
            Assert.Throws<ArgumentException>(() => new A().Attribute("href", "java\tscript:alert(1)"));
            new A().Attribute("href", "/Admin/Wanted?Tab=search");
            new A().Attribute("href", "https://www.themoviedb.org/");
        }

        [TestMethod]
        public async Task Connections_ValidateInputAndStayOwnerOnly()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);

            // Indexers may be keyless, download clients may not.
            using var missingKey = await host.SendAsync(HttpMethod.Post, Connections,
                new SaveAcquisitionConnectionDataV1("SAB", AcquisitionConnectionType.Sabnzbd, "https://sab.example.test", null, true, 10, [], null, null, null));
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
                var judgement = VideoReleaseJudge.Judge(AcquisitionKind.Movie, "Example Movie", 2020, null, UnitSet.Empty, offer);
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
                services.AddHttpClient<TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => usenet);
                services.AddHttpClient<Jularr.Integrations.Logic.Plex.PlexServerClient>().ConfigurePrimaryHttpMessageHandler(() => usenet);
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

        private static async Task<IReadOnlyList<AcquisitionProfileDetailsV1>> ReadProfilesAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/acquisition/profiles");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<BaseDataList<AcquisitionProfileDetailsV1>>())!;
        }

        private static async Task<IReadOnlyList<WantedReleaseV1>> ReadReleasesAsync(Host host, long wantedId)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/wanted/" + wantedId + "/releases");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseV1>>())!;
        }

        private static async Task<IReadOnlyList<ActivityItemV1>> ReadActivityAsync(Host host, ActivityTabType tab)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/activity?activityTabTypeId=" + tab);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<PageResult<ActivityItemV1>>())!.Items;
        }

        private static async Task<long> CreateWantedMovieAsync(Host host)
        {
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1(MediaType.Movie, "Example Movie", 2020));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var work = (await created.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            Assert.AreEqual(2020, work.ReleaseYear);
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId", "AcquisitionRequestStatusTypeId", "DecidedByAccountId", "DecidedAt")
                VALUES (500, {work.Id}, 202, 42, 1, 2, 41, now());
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
            public List<string> IndexerHosts { get; } = [];
            public string Queue { get; set; } = EmptyQueue;
            public string History { get; set; } = """{"history":{"slots":[]}}""";

            public const string PlexToken = "plex-server-token";
            public List<string> PlexRequests { get; } = [];
            public string PlexMovies { get; set; } = """{"MediaContainer":{"totalSize":3,"Metadata":[{"ratingKey":"11","title":"Example Movie","year":2020,"Guid":[{"id":"tmdb://603"}]},{"ratingKey":"12","title":"Plex Only","year":2021,"Guid":[{"id":"imdb://tt0000012"}]},{"ratingKey":"13","title":"Home Video"}]}}""";

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri!.Host == "plex.example.test")
                {
                    PlexRequests.Add(request.RequestUri.PathAndQuery);
                    if (!request.Headers.TryGetValues("X-Plex-Token", out var tokens) || tokens.Single() != PlexToken) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    return request.RequestUri.AbsolutePath switch
                    {
                        "/identity" => Json("""{"MediaContainer":{"machineIdentifier":"plexmachine1","version":"1.41.0"}}"""),
                        "/library/sections" => Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie","title":"Movies"},{"key":"2","type":"show","title":"Shows"},{"key":"3","type":"artist","title":"Music"}]}}"""),
                        "/library/sections/1/all" => Json(PlexMovies),
                        "/library/sections/2/all" => Json("""{"MediaContainer":{"totalSize":1,"Metadata":[{"ratingKey":"21","title":"Example Show","year":2020,"Guid":[{"id":"tvdb://81189"},{"id":"tmdb://1399"}]}]}}"""),
                        _ => Json("{}")
                    };
                }

                if (request.RequestUri!.Host == "api.themoviedb.org")
                {
                    return Tmdb(request);
                }

                // The open indexer takes no key, the broken one always fails.
                if (request.RequestUri!.Host is "indexer.example.test" or "open.example.test" or "broken.example.test" or "books.example.test" or "manual.example.test")
                {
                    lock (IndexerHosts) IndexerHosts.Add(request.RequestUri.Host);
                    if (request.RequestUri.Host == "broken.example.test") return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                    var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                    if (query["apikey"] != (request.RequestUri.Host == "open.example.test" ? null : IndexerKey)) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    return Xml(query["t"] == "caps" ? Capabilities : Results ?? MovieResults);
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

            public string? Results { get; set; }

            private static string MovieResults => $"""
                <rss><channel>
                  {Item(Lower, "lower", 1_000_000_000)}
                  {Item(Feature, "feature", 2_000_000_000)}
                  {Item("Other.Movie.2020.1080p.WEB-DL.H264-GRP", "other", 2_000_000_000)}
                  {Item("Example.Movie.1999.1080p.WEB-DL.H264-GRP", "old", 2_000_000_000)}
                </channel></rss>
                """;

            // One series: two seasons, the last episode still unaired.
            private static HttpResponseMessage Tmdb(HttpRequestMessage request)
            {
                if (request.Headers.Authorization?.Parameter != TmdbToken) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                var path = request.RequestUri!.AbsolutePath;
                var future = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                return path switch
                {
                    "/3/search/tv" => Json("""{"results":[{"id":1399,"name":"Example Show","original_name":"Beispiel Serie","first_air_date":"2020-04-01","overview":"A show."}]}"""),
                    "/3/tv/1399" => Json("""{"id":1399,"name":"Example Show","original_name":"Beispiel Serie","first_air_date":"2020-04-01","external_ids":{"tvdb_id":81189,"imdb_id":"tt0903747"},"seasons":[{"season_number":0,"name":"Specials","episode_count":1},{"season_number":1,"name":"Season 1","air_date":"2020-04-01","episode_count":3},{"season_number":2,"name":"Season 2","air_date":"2021-04-01","episode_count":2}]}"""),
                    "/3/tv/1399/season/0" => Json("""{"episodes":[{"season_number":0,"episode_number":1,"name":"Pilot extra","air_date":"2020-01-01"}]}"""),
                    "/3/tv/1399/season/1" => Json("""{"episodes":[{"season_number":1,"episode_number":1,"name":"One","air_date":"2020-04-01"},{"season_number":1,"episode_number":2,"name":"Two","air_date":"2020-04-08"},{"season_number":1,"episode_number":3,"name":"Three","air_date":"2020-04-15"}]}"""),
                    "/3/tv/1399/season/2" => Json($$$"""{"episodes":[{"season_number":2,"episode_number":1,"name":"Four","air_date":"2021-04-01"},{"season_number":2,"episode_number":2,"name":"Five","air_date":"{{{future}}}"}]}"""),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            }

            public const string TmdbToken = "eyJ-test-read-token";

            public const string SeriesResults = """
                <rss><channel>
                  <item><title>Example.Show.S01.1080p.WEB-DL.H264-GRP</title><guid>s1</guid><enclosure url="/getnzb/s1" length="6000000000" /></item>
                  <item><title>Example.Show.S01E01.1080p.WEB-DL.H264-GRP</title><guid>s1e1</guid><enclosure url="/getnzb/s1e1" length="2000000000" /></item>
                  <item><title>Example.Show.S02E01.1080p.WEB-DL.H264-GRP</title><guid>s2e1</guid><enclosure url="/getnzb/s2e1" length="2000000000" /></item>
                  <item><title>Example.Show.S02.1080p.WEB-DL.H264-GRP</title><guid>s2</guid><enclosure url="/getnzb/s2" length="4000000000" /></item>
                </channel></rss>
                """;

            private static string Item(string title, string guid, long size) =>
                $"""<item><title>{title}</title><guid>{guid}</guid><pubDate>Mon, 05 Oct 2026 10:00:00 +0000</pubDate><enclosure url="/getnzb/{guid}" length="{size}" /></item>""";

            private static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

            private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
