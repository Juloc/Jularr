using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Logic.Playback;
using Jularr.Integrations.Logic.Media;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class MediaMarkerTests
    {
        [TestMethod]
        [DataRow("Intro", MediaMarkerKind.Intro)]
        [DataRow("Introduction", MediaMarkerKind.Intro)]
        [DataRow("Opening", MediaMarkerKind.Intro)]
        [DataRow("Opening Credits", MediaMarkerKind.Intro)]
        [DataRow("Opening Theme", MediaMarkerKind.Intro)]
        [DataRow("OP", MediaMarkerKind.Intro)]
        [DataRow("OP1", MediaMarkerKind.Intro)]
        [DataRow("op 2", MediaMarkerKind.Intro)]
        [DataRow("Title_Sequence", MediaMarkerKind.Intro)]
        [DataRow("Credits", MediaMarkerKind.Credits)]
        [DataRow("End Credits", MediaMarkerKind.Credits)]
        [DataRow("Closing credits", MediaMarkerKind.Credits)]
        [DataRow("Ending", MediaMarkerKind.Credits)]
        [DataRow("ED", MediaMarkerKind.Credits)]
        [DataRow("ED2", MediaMarkerKind.Credits)]
        [DataRow("Outro", MediaMarkerKind.Credits)]
        [DataRow("  RECAP ", MediaMarkerKind.Recap)]
        [DataRow("Previously on", MediaMarkerKind.Recap)]
        [DataRow("Preview", MediaMarkerKind.Preview)]
        [DataRow("Next Episode", MediaMarkerKind.Preview)]
        [DataRow("Coming up", MediaMarkerKind.Preview)]
        [DataRow("Chapter 1", null)]
        [DataRow("Cold open", null)]
        [DataRow("Opera", null)]
        [DataRow("Operation", null)]
        [DataRow("Edward", null)]
        [DataRow("The Ending of the World", null)]
        [DataRow("", null)]
        [DataRow(null, null)]
        public void ChapterTitle_NamesItsKind_OrNone(string? title, MediaMarkerKind? expected)
        {
            Assert.AreEqual(expected, MediaMarkers.KindOfChapter(title));
        }

        [TestMethod]
        public void Chapters_GiveOneMarkerPerKind_FromTheFirstRun_FittedToTheFile()
        {
            var chapters = new MediaChapterRow[]
            {
                new(0, 0, 20_000, "Recap"), new(1, 20_000, 50_000, "Opening"), new(2, 50_000, 90_000, "OP2"), new(3, 90_000, 1_200_000, "Part A"),
                new(4, 1_200_000, 1_300_000, "Ending"), new(5, 1_300_000, 1_500_000, "End Credits"), new(6, 1_500_000, 1_600_000, "Preview"), new(7, 1_600_000, 1_700_000, "Opening")
            };
            var markers = MediaMarkers.FromChapters(chapters, 1_450_000);

            // Consecutive chapters of a kind are one marker, a later run of the same kind adds none, and the end of the file cuts the last.
            CollectionAssert.AreEqual(new[] { (MediaMarkerKind.Recap, 0L, 20_000L), (MediaMarkerKind.Intro, 20_000L, 90_000L), (MediaMarkerKind.Credits, 1_200_000L, 1_450_000L) },
                markers.Select(marker => (marker.MediaMarkerKindTypeId, marker.StartMs, marker.EndMs)).ToArray());
            Assert.IsTrue(markers.All(marker => marker is { MediaMarkerSourceTypeId: MediaMarkerSource.Chapter, Confidence: 1m }));

            // A chapter wholly beyond the file is no marker; an unknown length keeps the chapter as it is.
            Assert.AreEqual(0, MediaMarkers.FromChapters([new(0, 2_000_000, 2_100_000, "Credits")], 1_450_000).Count);
            Assert.AreEqual(1_600_000L, MediaMarkers.FromChapters([new(0, 1_500_000, 1_600_000, "Preview")], null).Single().EndMs);
        }

        [TestMethod]
        public void Chapters_WithImplausibleStretch_GiveNoMarker()
        {
            // The file of #1596: one chapter "Credits" over the whole episode and a three-second rest.
            Assert.AreEqual(0, MediaMarkers.FromChapters([new(0, 0, 1_416_999, "Credits"), new(1, 1_416_999, 1_419_961, "Chapter")], 1_420_031).Count);

            // Credits over more than half of the file, an intro of ten minutes: no marker; a later fitting run of the kind still counts.
            Assert.AreEqual(0, MediaMarkers.FromChapters([new(0, 100_000, 900_000, "Ending")], 1_420_000).Count);
            Assert.AreEqual(0, MediaMarkers.FromChapters([new(0, 10_000, 610_000, "Opening")], 1_420_000).Count);
            var later = MediaMarkers.FromChapters([new(0, 0, 900_000, "Credits"), new(1, 900_000, 1_000_000, "Part B"), new(2, 1_300_000, 1_420_000, "Ending")], 1_420_000).Single();
            Assert.AreEqual((MediaMarkerKind.Credits, 1_300_000L, 1_420_000L), (later.MediaMarkerKindTypeId, later.StartMs, later.EndMs));
        }

        [TestMethod]
        public void SidecarFile_Edl_ReadsKnownActionsWithinItsLimits()
        {
            var text = "# markers\r\n0 0 0\r\n12.5\t95  4\r\n1300 1440 5\n\n  3.5 4.5 6  \n10 20 7\n10 20 3\n999 1000 4\nbad line\n5 3 4\n-1 5 4\n1 2 3 4\n1e3 2e3 4\n";
            var markers = MediaMarkers.ParseEdl(text, 1_440_000);
            CollectionAssert.AreEqual(new[] { (MediaMarkerKind.Intro, 12_500L, 95_000L), (MediaMarkerKind.Credits, 1_300_000L, 1_440_000L), (MediaMarkerKind.Recap, 3_500L, 4_500L), (MediaMarkerKind.Preview, 10_000L, 20_000L) },
                markers.Select(marker => (marker.MediaMarkerKindTypeId, marker.StartMs, marker.EndMs)).ToArray());
            Assert.IsTrue(markers.All(marker => marker.MediaMarkerSourceTypeId == MediaMarkerSource.Sidecar));

            // Nothing past the file's end, no numbers beyond a day, and only the lines before the limit count.
            Assert.AreEqual(0, MediaMarkers.ParseEdl("2000 2100 4", 1_440_000).Count);
            Assert.AreEqual(0, MediaMarkers.ParseEdl("0 90000 4", null).Count);
            Assert.AreEqual(0, MediaMarkers.ParseEdl(string.Concat(Enumerable.Repeat("1 1 9\n", MediaMarkers.MaximumSidecarLines)) + "10 20 4\n", null).Count);
            Assert.AreEqual(1, MediaMarkers.ParseEdl(string.Concat(Enumerable.Repeat("1 1 9\n", MediaMarkers.MaximumSidecarLines - 1)) + "10 20 4\n", null).Count);
        }

        [TestMethod]
        [DataRow("Show/Season 1/Show - S01E01.mkv", "Show/Season 1/Show - S01E01.edl")]
        [DataRow("Film.v2.mp4", "Film.v2.edl")]
        [DataRow("Movies/Name", null)]
        [DataRow("Movies.old/Name", null)]
        [DataRow(".hidden", null)]
        public void SidecarPath_ReplacesTheExtension_StayingBesideTheVideo(string video, string? expected)
        {
            Assert.AreEqual(expected, MediaMarkers.SidecarPath(video));
        }

        [TestMethod]
        public void Probe_FfprobeJson_ReadsChaptersInOrder_BoundedAndSkippingBrokenOnes()
        {
            var probe = MediaProbeClient.Parse("""
                {"streams":[{"index":0,"codec_type":"video","codec_name":"h264"}],
                "chapters":[{"id":1,"start_time":"0.000000","end_time":"90.500000","tags":{"title":"Intro"}},
                {"id":2,"start_time":"90.500000","end_time":"600.000000"},
                {"id":3,"start_time":"600","end_time":"500"},
                {"id":4,"start_time":"x","end_time":"700"},
                {"id":5,"start_time":"700.1","end_time":"800.0","tags":{"title":"Credits"}}],
                "format":{"format_name":"matroska,webm","duration":"800"}}
                """);
            CollectionAssert.AreEqual(new[] { (0L, 90_500L, "Intro"), (90_500L, 600_000L, null), (700_100L, 800_000L, "Credits") },
                probe.Chapters.Select(chapter => (chapter.StartMs, chapter.EndMs, chapter.Title)).ToArray());
            Assert.AreEqual(0, MediaProbeClient.Parse("{\"streams\":[]}").Chapters.Count);

            var many = "{\"chapters\":[" + string.Join(',', Enumerable.Range(0, 600).Select(index => $"{{\"start_time\":\"{index}\",\"end_time\":\"{index + 1}\"}}")) + "]}";
            Assert.AreEqual(500, MediaProbeClient.Parse(many).Chapters.Count);
        }

        [TestMethod]
        public async Task Markers_PlayerReadsTheMostTrustedSource_AdminSetsCopiesAndDeletes_AndPermissionsApply()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var (workId, units, assets) = await CreateEpisodesAsync(host, storage.Path);

            // The first episode has chapters, a chapter marker, a sidecar marker for the same intro and credits from its chapters.
            await host.Fixture.RunSqlAsync($"""
                UPDATE "StoredFiles" SET "ProbedAt" = now(), "DurationMs" = 1440000;
                INSERT INTO "MediaChapters" ("StoredFileId", "ChapterIndex", "StartMs", "EndMs", "Title")
                SELECT file."Id", chapter."Index", chapter."Start", chapter."End", chapter."Title"
                FROM "StoredFiles" AS file CROSS JOIN (VALUES (0, 0, 30000, 'Cold open'), (1, 30000, 105000, 'Intro'), (2, 105000, 1300000, NULL), (3, 1300000, 1440000, 'Credits'))
                    AS chapter ("Index", "Start", "End", "Title")
                WHERE file."MediaAssetId" = {assets[0]};
                INSERT INTO "MediaMarkers" ("StoredFileId", "MediaMarkerKindTypeId", "MediaMarkerSourceTypeId", "StartMs", "EndMs")
                SELECT file."Id", marker."Kind", marker."Source", marker."Start", marker."End"
                FROM "StoredFiles" AS file CROSS JOIN (VALUES (1, 3, 30000, 105000), (2, 3, 1300000, 1440000), (1, 2, 28000, 100000), (1, 4, 1000, 2000)) AS marker ("Kind", "Source", "Start", "End")
                WHERE file."MediaAssetId" = {assets[0]};
                """);

            var markers = await ReadMarkersAsync(host, assets[0]);
            CollectionAssert.AreEqual(new[] { (MediaMarkerKind.Intro, 28_000L, 100_000L), (MediaMarkerKind.Credits, 1_300_000L, 1_440_000L) },
                markers.Select(marker => (marker.MediaMarkerKindTypeId, marker.StartMs, marker.EndMs)).ToArray());
            Assert.AreEqual(0, (await ReadMarkersAsync(host, assets[1])).Count);

            using var chaptersResponse = await host.SendAsync(HttpMethod.Get, "/api/v1/playback/assets/" + assets[0] + "/chapters");
            Assert.AreEqual(HttpStatusCode.OK, chaptersResponse.StatusCode, await chaptersResponse.Content.ReadAsStringAsync());
            var chapters = (await chaptersResponse.Content.ReadFromJsonAsync<BaseDataList<PlaybackChapterV1>>())!;
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, chapters.Select(chapter => chapter.ChapterIndex).ToArray());
            CollectionAssert.AreEqual(new string?[] { "Cold open", "Intro", null, "Credits" }, chapters.Select(chapter => chapter.Title).ToArray());

            // The player page carries them: markers for the skip button, one button per chapter for the menu.
            using var player = await host.SendAsync(HttpMethod.Get, "/Player/" + assets[0]);
            var html = await player.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, player.StatusCode, html);
            StringAssert.Contains(html, "data-markers=");
            StringAssert.Contains(html, "&quot;kind&quot;:&quot;Intro&quot;,&quot;start&quot;:28");
            Assert.AreEqual(4, System.Text.RegularExpressions.Regex.Matches(html, "player__chapter\"[^>]*data-chapter=\"").Count);
            Assert.AreEqual(3, System.Text.RegularExpressions.Regex.Matches(html, "player__mark\" data-chapter=\"").Count, "A tick for every chapter after the first.");
            StringAssert.Contains(html, "data-skip-intro=\"Button\"");

            // A manual marker beats the sidecar on every file of the episode.
            var route = $"/api/v1/admin/works/{workId}/units/{units[0]}/markers";
            using var set = await host.SendAsync(HttpMethod.Put, route, new SaveWorkMarkerDataV1(MediaMarkerKind.Intro, 40m, 95.5m));
            Assert.AreEqual(HttpStatusCode.OK, set.StatusCode, await set.Content.ReadAsStringAsync());
            Assert.AreEqual((MediaMarkerKind.Intro, 40_000L, 95_500L), Pair((await ReadMarkersAsync(host, assets[0])).Single(marker => marker.MediaMarkerKindTypeId == MediaMarkerKind.Intro)));
            using var changed = await host.SendAsync(HttpMethod.Put, route, new SaveWorkMarkerDataV1(MediaMarkerKind.Intro, 41m, 96m));
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync($"SELECT count(*) FROM \"MediaMarkers\" WHERE \"MediaMarkerSourceTypeId\" = 1 AND \"StoredFileId\" IN (SELECT \"Id\" FROM \"StoredFiles\" WHERE \"MediaAssetId\" = {assets[0]})"));

            // The end must follow the start and the start must lie inside the file; neither changes anything.
            using var backwards = await host.SendAsync(HttpMethod.Put, route, new SaveWorkMarkerDataV1(MediaMarkerKind.Credits, 90m, 80m));
            Assert.AreEqual(HttpStatusCode.BadRequest, backwards.StatusCode);
            using var beyond = await host.SendAsync(HttpMethod.Put, route, new SaveWorkMarkerDataV1(MediaMarkerKind.Credits, 2000m, 2100m));
            Assert.AreEqual(HttpStatusCode.NotFound, beyond.StatusCode, await beyond.Content.ReadAsStringAsync());
            using var tooLong = await host.SendAsync(HttpMethod.Put, route, new SaveWorkMarkerDataV1(MediaMarkerKind.Credits, 0m, 90000m));
            Assert.AreEqual(HttpStatusCode.BadRequest, tooLong.StatusCode);
            using var elsewhere = await host.SendAsync(HttpMethod.Put, $"/api/v1/admin/works/{workId + 1}/units/{units[0]}/markers", new SaveWorkMarkerDataV1(MediaMarkerKind.Intro, 1m, 2m));
            Assert.AreEqual(HttpStatusCode.NotFound, elsewhere.StatusCode);

            // Copying shifts the episode's marker onto the rest of the season, fitted to each file; the other season is left alone.
            await host.Fixture.RunSqlAsync($"UPDATE \"StoredFiles\" SET \"DurationMs\" = 60000 WHERE \"MediaAssetId\" = {assets[2]}");
            using var copied = await host.SendAsync(HttpMethod.Post, route + "/copy", new CopyWorkMarkersDataV1(MediaMarkerKind.Intro, 2.5m));
            Assert.AreEqual(HttpStatusCode.OK, copied.StatusCode, await copied.Content.ReadAsStringAsync());
            Assert.AreEqual(2, (await copied.Content.ReadFromJsonAsync<CopyWorkMarkersResultV1>())!.CopiedCount);
            Assert.AreEqual((MediaMarkerKind.Intro, 43_500L, 98_500L), Pair((await ReadMarkersAsync(host, assets[1])).Single()));
            Assert.AreEqual((MediaMarkerKind.Intro, 43_500L, 60_000L), Pair((await ReadMarkersAsync(host, assets[2])).Single()));
            Assert.AreEqual(0, (await ReadMarkersAsync(host, assets[3])).Count);

            // The page lists the season's episodes with their markers and the editor of the chosen one.
            using var page = await host.SendAsync(HttpMethod.Get, $"/Admin/Works/{workId}/Markers?Season=1&Unit={units[0]}");
            html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "0:41 &#x2013; 1:36");
            StringAssert.Contains(html, "name=\"StartSeconds\"");
            StringAssert.Contains(html, "name=\"OffsetSeconds\"");
            StringAssert.Contains(html, "handler=Delete");

            // The posted form saves through the same service.
            using var posted = await host.SendAsync(HttpMethod.Post, $"/Admin/Works/{workId}/Markers?Season=1&Unit={units[1]}&handler=Save", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = LibraryTests.Token(html),
                ["mediaMarkerKindTypeId"] = "2",
                ["__Invariant"] = "StartSeconds",
                ["startSeconds"] = "1300.5",
                ["endSeconds"] = "1440"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, posted.StatusCode, await posted.Content.ReadAsStringAsync());
            Assert.AreEqual((MediaMarkerKind.Credits, 1_300_500L, 1_440_000L), Pair((await ReadMarkersAsync(host, assets[1])).Single(marker => marker.MediaMarkerKindTypeId == MediaMarkerKind.Credits)));

            // Deleting takes only the manual marker; what the sidecar says shows again.
            using var deleted = await host.SendAsync(HttpMethod.Delete, route + "/1");
            Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
            Assert.AreEqual((MediaMarkerKind.Intro, 28_000L, 100_000L), Pair((await ReadMarkersAsync(host, assets[0])).Single(marker => marker.MediaMarkerKindTypeId == MediaMarkerKind.Intro)));
            using var again = await host.SendAsync(HttpMethod.Delete, route + "/1");
            Assert.AreEqual(HttpStatusCode.NotFound, again.StatusCode);

            // A user cannot edit; a media manager can; a profile only reads what its own library shows.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 2 WHERE \"Id\" = 41");
            using var denied = await host.SendAsync(HttpMethod.Put, route, new SaveWorkMarkerDataV1(MediaMarkerKind.Intro, 1m, 2m));
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using var deniedCopy = await host.SendAsync(HttpMethod.Post, route + "/copy", new CopyWorkMarkersDataV1(MediaMarkerKind.Intro, 0m));
            Assert.AreEqual(HttpStatusCode.Unauthorized, deniedCopy.StatusCode);
            using var deniedRead = await host.SendAsync(HttpMethod.Get, $"/api/v1/admin/works/{workId}/markers");
            Assert.AreEqual(HttpStatusCode.Unauthorized, deniedRead.StatusCode);
            Assert.AreEqual(2, (await ReadMarkersAsync(host, assets[0])).Count);
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var allowed = await host.SendAsync(HttpMethod.Put, route, new SaveWorkMarkerDataV1(MediaMarkerKind.Intro, 1m, 2m));
            Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode, await allowed.Content.ReadAsStringAsync());

            // Without the playback module nothing is offered or edited.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Playback'");
            using var off = await host.SendAsync(HttpMethod.Get, "/api/v1/playback/assets/" + assets[0] + "/markers");
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
            using var offAdmin = await host.SendAsync(HttpMethod.Put, route, new SaveWorkMarkerDataV1(MediaMarkerKind.Intro, 3m, 4m));
            Assert.AreEqual(HttpStatusCode.Forbidden, offAdmin.StatusCode);
        }

        [TestMethod]
        public async Task Scan_ReadsChaptersAndSidecar_FromARealFile()
        {
            // Opt-in: JULARR_TEST_FFMPEG names an ffmpeg executable; ffprobe sits beside it.
            if (Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG") is not { Length: > 0 } ffmpeg)
            {
                Assert.Inconclusive("JULARR_TEST_FFMPEG is not configured.");
                return;
            }

            using var storage = new LibraryTests.TemporaryDirectory();
            var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.Ordinal));
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, settings: new Dictionary<string, string> { ["Media:FfprobePath"] = ffprobe });
            var mountId = await LibraryTests.CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Series", "Show (2020)", "Season 1");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "chapters.txt"), """
                ;FFMETADATA1
                [CHAPTER]
                TIMEBASE=1/1000
                START=0
                END=2000
                title=Cold open
                [CHAPTER]
                TIMEBASE=1/1000
                START=2000
                END=5000
                title=Opening
                [CHAPTER]
                TIMEBASE=1/1000
                START=5000
                END=15000
                title=Part A
                [CHAPTER]
                TIMEBASE=1/1000
                START=15000
                END=20000
                title=End Credits
                """);
            await RunAsync(ffmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=64x48:r=5:d=20", "-i", Path.Combine(folder, "chapters.txt"), "-map", "0:v", "-map_metadata", "1", "-map_chapters", "1",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", Path.Combine(folder, "Show - S01E01.mkv"));
            await RunAsync(ffmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=64x48:r=5:d=20", "-c:v", "libx264", "-pix_fmt", "yuv420p", Path.Combine(folder, "Show - S01E02.mkv"));

            // The second episode has no chapters, only a sidecar; a sidecar of a bad kind or too large is not read.
            await File.WriteAllTextAsync(Path.Combine(folder, "Show - S01E02.edl"), "1 4.5 4\n18 20 5\n");
            File.Delete(Path.Combine(folder, "chapters.txt"));
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Series", MediaType.Series);
            await LibraryTests.ScanAsync(host, rootId);

            Assert.AreEqual(4L, await host.ScalarAsync("SELECT count(*) FROM \"MediaChapters\" AS chapter JOIN \"StoredFiles\" AS file ON file.\"Id\" = chapter.\"StoredFileId\" WHERE file.\"RelativePath\" LIKE '%E01.mkv'"));
            Assert.AreEqual("2000:5000:3|15000:20000:3", string.Join('|', await Rows(host, "E01.mkv")));
            Assert.AreEqual("1000:4500:2|18000:20000:2", string.Join('|', await Rows(host, "E02.mkv")));

            // A manual marker survives the next probe of the file.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "MediaMarkers" ("StoredFileId", "MediaMarkerKindTypeId", "MediaMarkerSourceTypeId", "StartMs", "EndMs") SELECT "Id", 1, 1, 100, 900 FROM "StoredFiles" WHERE "RelativePath" LIKE '%E01.mkv';
                UPDATE "StoredFiles" SET "ProbedAt" = NULL;
                """);
            await LibraryTests.ScanAsync(host, rootId);
            Assert.AreEqual("100:900:1|2000:5000:3|15000:20000:3", string.Join('|', await Rows(host, "E01.mkv")));
        }

        private static async Task<string[]> Rows(Host host, string file)
        {
            // Marker rows of one file as start:end:source, in time order.
            var text = (string?)await host.ScalarAsync($"""
                SELECT string_agg(marker."StartMs" || ':' || marker."EndMs" || ':' || marker."MediaMarkerSourceTypeId", '|' ORDER BY marker."StartMs")
                FROM "MediaMarkers" AS marker JOIN "StoredFiles" AS file ON file."Id" = marker."StoredFileId" WHERE file."RelativePath" LIKE '%{file}'
                """);
            return text is null ? [] : text.Split('|');
        }

        private static async Task RunAsync(string executable, params string[] arguments)
        {
            var start = new ProcessStartInfo(executable) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(0, process.ExitCode, errors);
        }

        private static (MediaMarkerKind, long, long) Pair(PlaybackMarkerV1 marker) => (marker.MediaMarkerKindTypeId, marker.StartMs, marker.EndMs);

        private static async Task<BaseDataList<PlaybackMarkerV1>> ReadMarkersAsync(Host host, long assetId)
        {
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/playback/assets/" + assetId + "/markers");
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            return (await read.Content.ReadFromJsonAsync<BaseDataList<PlaybackMarkerV1>>())!;
        }

        // Three episodes of season one and one of season two, each with a file, as their own assets.
        private static async Task<(long WorkId, long[] Units, long[] Assets)> CreateEpisodesAsync(Host host, string storagePath)
        {
            var mountId = await LibraryTests.CreateMountAsync(host);
            var one = Path.Combine(storagePath, mountId.ToString(), "Series", "Show (2020)", "Season 1");
            var two = Path.Combine(storagePath, mountId.ToString(), "Series", "Show (2020)", "Season 2");
            Directory.CreateDirectory(one);
            Directory.CreateDirectory(two);
            foreach (var number in new[] { 1, 2, 3 })
            {
                await File.WriteAllTextAsync(Path.Combine(one, $"Show - S01E0{number}.mkv"), "episode " + number);
            }

            await File.WriteAllTextAsync(Path.Combine(two, "Show - S02E01.mkv"), "episode four");
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Series", MediaType.Series);
            await LibraryTests.ScanAsync(host, rootId);

            long? workId = null;
            var units = new List<long>();
            var assets = new List<long>();
            foreach (var (season, number) in new[] { (1, 1), (1, 2), (1, 3), (2, 1) })
            {
                var fileId = (long)(await host.ScalarAsync($"SELECT \"Id\" FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '%S0{season}E0{number}.mkv'"))!;
                using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment",
                    new AssignLibraryFileDataV1(workId, workId is null ? "Show" : null, WorkUnitType.Episode, number, season));
                Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
                workId ??= (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!;
                assets.Add((await assigned.Content.ReadFromJsonAsync<AssignLibraryFileResultV1>())!.MediaAssetId);
                units.Add((long)(await host.ScalarAsync($"SELECT \"WorkUnitId\" FROM \"MediaAssets\" WHERE \"Id\" = {assets[^1]}"))!);
            }

            return (workId!.Value, [.. units], [.. assets]);
        }
    }
}
