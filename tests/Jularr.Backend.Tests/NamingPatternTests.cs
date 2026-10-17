using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.System.Library.V1;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Service.System.Library.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class NamingPatternTests
    {
        // The seeded patterns.
        internal static readonly Dictionary<MediaType, string> Defaults = new()
        {
            [MediaType.Movie] = "{Title} ({Year})/{Title} ({Year}) [{Quality}]",
            [MediaType.Series] = "{Title}/Season {Season:00}/{Title} - S{Season:00}E{Episode:00} [{Quality}]"
        };

        [TestMethod]
        public void NamingPattern_EmptyPlaceholders_DropWithTheirBrackets()
        {
            var episode = new NamingValues("Show: Part", 2020, 1, 2.5m, "Pilot", "WEBDL-1080p", "GRP", null, 2);
            Assert.AreEqual("Show Part/Season 01/Show Part - S01E02.5 [WEBDL-1080p]", NamingPattern.Apply(Defaults[MediaType.Series], episode));
            Assert.AreEqual("Show Part (2020)/S01E02.5 - Pilot [WEBDL-1080p Proper] - GRP",
                NamingPattern.Apply("{Title} ({Year})/S{Season:00}E{Episode:00} - {Episode Title} [{Quality} {Proper}] - {Release Group}", episode));

            // Nothing known leaves no empty brackets or separators.
            var bare = new NamingValues("Film", null, null, null, null, null, null, null, 1);
            Assert.AreEqual("Film/Film", NamingPattern.Apply(Defaults[MediaType.Movie], bare));
            Assert.AreEqual("Film", NamingPattern.Apply("{Title} - {Edition} [{Release Group}]", bare));
            Assert.AreEqual("Film - GRP [WEBDL-1080p]", NamingPattern.Apply("{Title} - {Edition} - {Release Group} [{Quality} {Proper}]",
                new NamingValues("Film", null, null, null, null, "WEBDL-1080p", "GRP", null, 1)));
            Assert.AreEqual("Film", NamingPattern.Apply("{Edition}/{Title}", bare));

            // Titles keep what the cleanup would cut; values cannot open folders.
            Assert.AreEqual("Mr (2000)/Mr (2000)", NamingPattern.Apply(Defaults[MediaType.Movie], new NamingValues("Mr.", 2000, null, null, null, null, null, null, 1)));
            Assert.AreEqual("Foo () (2000)/Foo () (2000)", NamingPattern.Apply(Defaults[MediaType.Movie], new NamingValues("Foo ()", 2000, null, null, null, null, null, null, 1)));
            Assert.AreEqual("a b/a b", NamingPattern.Apply(Defaults[MediaType.Movie], new NamingValues("a/b", null, null, null, null, null, null, null, 1)));
        }

        [TestMethod]
        public void NamingPattern_RadarrSonarrAliasesAndProviderIds()
        {
            var episode = new NamingValues("The Series", 2020, 1, 7, "Midnight", "HDTV-1080p", "DKB", null, 1)
            {
                MediaTypeId = MediaType.Series,
                ProviderIds = new Dictionary<string, string> { ["imdb"] = "tt39551287", ["tmdb_tv"] = "123456", ["tvdb"] = "78910" }
            };
            const string series = "{Series Title} - {ImdbId}/Season {Season:00}/{Series CleanTitle} - S{Season:00}E{Episode:00} - {Episode Title} {[Quality Full]}{-Release Group}";
            NamingPattern.Validate(MediaType.Series, series, "Pattern");
            Assert.AreEqual("The Series - tt39551287/Season 01/The Series - S01E07 - Midnight [HDTV-1080p]-DKB", NamingPattern.Apply(series, episode));
            Assert.AreEqual("The.Series.(2020)/78910/123456", NamingPattern.Apply("{Series.TitleYear}/{TvdbId}/{ProviderId:tmdb_tv}", episode));

            var movie = new NamingValues("The Movie", 2010, null, null, null, "Bluray-1080p", "EVOLVE", "Extended", 2)
            {
                ProviderIds = new Dictionary<string, string> { ["imdb"] = "tt1234567", ["tmdb_movie"] = "9876" }
            };
            const string template = "{Movie Title} ({Release Year}) - {Edition Tags} {[Quality Full]}{-Release Group} - {ImdbId}";
            NamingPattern.Validate(MediaType.Movie, template, "Pattern");
            Assert.AreEqual("The Movie (2010) - Extended [Bluray-1080p Proper]-EVOLVE - tt1234567", NamingPattern.Apply(template, movie));
            Assert.AreEqual("The Movie/9876", NamingPattern.Apply("{Movie CleanTitle}/{TmdbId}", movie));
        }

        [TestMethod]
        public void NamingPattern_MediaInfoAndCustomFormatTokens_RenderOnlyKnownData()
        {
            var movie = new NamingValues("Film", 2010, null, null, null, "Bluray-1080p", "EVOLVE", null, 1)
            {
                MediaInfo = new Dictionary<string, string>
                {
                    ["MediaInfo VideoCodec"] = "hevc",
                    ["MediaInfo AudioCodec"] = "dts",
                    ["MediaInfo AudioLanguagesAll"] = "[JA+EN]"
                },
                CustomFormats = new Dictionary<string, string> { ["Surround Sound x264"] = "Surround Sound x264" }
            };
            const string template = "{Movie Title} {[Quality Full]} {[Custom Formats]} {Custom Format:Surround Sound x264} {MediaInfo VideoCodec}";
            NamingPattern.Validate(MediaType.Movie, template, "Pattern");
            Assert.AreEqual("Film [Bluray-1080p] [Surround Sound x264] Surround Sound x264 hevc", NamingPattern.Apply(template, movie));
        }

        [TestMethod]
        public void NamingPattern_LocalizedTitle_UsesStoredLocaleAndFallsBackToCanonical()
        {
            var values = new NamingValues("The Film", 2010, null, null, null, null, null, null, 1)
            {
                LocalizedTitles = new Dictionary<string, string> { ["de"] = "Der Film" }
            };
            NamingPattern.Validate(MediaType.Movie, "{Movie Title:DE}/{Movie Title:FR} - {ImdbId}", "Pattern");
            Assert.AreEqual("Der Film/The Film", NamingPattern.Apply("{Movie Title:DE}/{Movie Title:FR} - {ImdbId}", values));
        }

        [TestMethod]
        public void NamingPattern_UnknownOrIncomplete_IsRejected()
        {
            Assert.AreEqual("naming_unknown_token", Assert.ThrowsExactly<DataValidationException>(() => NamingPattern.Validate(MediaType.Movie, "{Title} {Studio}", "Pattern")).Code);
            Assert.AreEqual("naming_unknown_token", Assert.ThrowsExactly<DataValidationException>(() => NamingPattern.Validate(MediaType.Movie, "{Title} {", "Pattern")).Code);
            Assert.AreEqual("naming_incomplete", Assert.ThrowsExactly<DataValidationException>(() => NamingPattern.Validate(MediaType.Series, "{Title} E{Episode}", "Pattern")).Code);
            Assert.AreEqual("naming_invalid_path", Assert.ThrowsExactly<DataValidationException>(() => NamingPattern.Validate(MediaType.Movie, "{Title}//{Title}", "Pattern")).Code);
            Assert.AreEqual("naming_invalid_path", Assert.ThrowsExactly<DataValidationException>(() => NamingPattern.Validate(MediaType.Movie, "{Title}/{Year}", "Pattern")).Code);
            Assert.AreEqual("naming_unknown_token", Assert.ThrowsExactly<DataValidationException>(() => NamingPattern.Validate(MediaType.Movie, "{Title:00}", "Pattern")).Code);
            NamingPattern.Validate(MediaType.Series, Defaults[MediaType.Series], "Pattern");
        }

        [TestMethod]
        public async Task NamingPattern_AdminEdit_IsVersionChecked()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Library/Naming");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "Episode files");
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/library/naming");
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            var series = (await read.Content.ReadFromJsonAsync<BaseDataList<NamingPatternV1>>())!.Single(pattern => pattern.MediaTypeId == MediaType.Series);
            Assert.AreEqual(Defaults[MediaType.Series], series.Folder + "/" + series.File);
            Assert.AreEqual("Example Show/Season 01/Example Show - S01E02 [WEBDL-1080p].mkv", series.Example);
            StringAssert.Contains(html, "Example Show/Season 01/Example Show - S01E02 [WEBDL-1080p].mkv");

            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series", new UpdateNamingPatternDataV1("{Title}/S{Season:00}", "{Title} S{Season:00}E{Episode:00}", series.Version));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var stale = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series", new UpdateNamingPatternDataV1(null, "{Title} {Season} {Episode}", series.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            using var invalid = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series", new UpdateNamingPatternDataV1(null, "{Title}", series.Version + 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.AreEqual("{Title}/S{Season:00}/{Title} S{Season:00}E{Episode:00}", await host.ScalarAsync("SELECT \"Pattern\" FROM \"NamingPatterns\" WHERE \"MediaTypeId\" = 2"));
        }

        [TestMethod]
        public async Task Replacement_SeveralReleasesNeedTellingNamesAndRecycledFilesLeaveAfterTheirDays()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var mountId = await LibraryTests.CreateMountAsync(host);
            Directory.CreateDirectory(Path.Combine(storage.Path, mountId.ToString(), "Movies"));
            await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"IsReadOnly\" = FALSE");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"RecycleModeId\" = 2");
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Library/Versions");
            StringAssert.Matches(await page.Content.ReadAsStringAsync(), new System.Text.RegularExpressions.Regex("<option value=\"2\"[^>]*selected"));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"RecycleModeId\" = 1");
            const string route = "/api/v1/admin/library/replacement";
            using var read = await host.SendAsync(HttpMethod.Get, route);
            var settings = (await read.Content.ReadFromJsonAsync<ReplacementSettingsV1>())!;
            Assert.AreEqual((false, RecycleMode.Delete, 7), (settings.KeepsReleases, settings.RecycleModeId, settings.RecycleDays));

            // Several releases need names that tell them apart, now and in later pattern edits.
            await host.Fixture.RunSqlAsync("UPDATE \"NamingPatterns\" SET \"Pattern\" = '{Title}/S{Season:00}/{Title} S{Season:00}E{Episode:00}' WHERE \"MediaTypeId\" = 2");
            using var untold = await host.SendAsync(HttpMethod.Put, route, new UpdateReplacementSettingsDataV1(true, RecycleMode.Delete, null, null, 7, settings.Version));
            StringAssert.Contains(await untold.Content.ReadAsStringAsync(), "naming_needs_release_token");
            await host.Fixture.RunSqlAsync($"UPDATE \"NamingPatterns\" SET \"Pattern\" = '{Defaults[MediaType.Series]}' WHERE \"MediaTypeId\" = 2");
            using var kept = await host.SendAsync(HttpMethod.Put, route, new UpdateReplacementSettingsDataV1(true, RecycleMode.Delete, null, null, 7, settings.Version));
            Assert.AreEqual(HttpStatusCode.OK, kept.StatusCode, await kept.Content.ReadAsStringAsync());
            var version = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"NamingPatterns\" WHERE \"MediaTypeId\" = 2"))!;
            using var plain = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series", new UpdateNamingPatternDataV1("{Title}", "{Title} S{Season:00}E{Episode:00}", version));
            StringAssert.Contains(await plain.Content.ReadAsStringAsync(), "naming_needs_release_token");

            // An own recycle folder needs its place, outside every library.
            using var nowhere = await host.SendAsync(HttpMethod.Put, route, new UpdateReplacementSettingsDataV1(false, RecycleMode.OwnFolder, null, null, 3, settings.Version + 1));
            StringAssert.Contains(await nowhere.Content.ReadAsStringAsync(), "required");
            using var inside = await host.SendAsync(HttpMethod.Put, route, new UpdateReplacementSettingsDataV1(false, RecycleMode.OwnFolder, mountId, "Movies/Old", 3, settings.Version + 1));
            StringAssert.Contains(await inside.Content.ReadAsStringAsync(), "invalid_value");
            using var folder = await host.SendAsync(HttpMethod.Put, route, new UpdateReplacementSettingsDataV1(false, RecycleMode.OwnFolder, mountId, "Recycle", 3, settings.Version + 1));
            Assert.AreEqual(HttpStatusCode.OK, folder.StatusCode, await folder.Content.ReadAsStringAsync());

            // Only files Jularr recycled and registered leave after their days; anything else in the folder stays.
            var old = Path.Combine(storage.Path, mountId.ToString(), "Recycle", "Movies", "Old Film", "Old Film.mkv");
            var fresh = Path.Combine(storage.Path, mountId.ToString(), "Recycle", "Movies", "New Film.mkv");
            var foreign = Path.Combine(storage.Path, mountId.ToString(), "Recycle", "Notes", "kept.txt");
            foreach (var file in new[] { old, fresh, foreign })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "x");
            }

            File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-30));
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "RecycledFiles" ("StorageMountId", "FolderPath", "RelativePath", "RecycledAt") VALUES
                    ({mountId}, 'Recycle', 'Movies/Old Film/Old Film.mkv', now() - interval '4 days'),
                    ({mountId}, 'Recycle', 'Movies/New Film.mkv', now());
                """);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<CleanRecycleBinsV1>().ExecuteAsync<UpdateResult>(new CleanRecycleBinsParametersV1(), default);
            }

            Assert.IsFalse(File.Exists(old));
            Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(old)));
            Assert.IsTrue(File.Exists(fresh));
            Assert.IsTrue(File.Exists(foreign));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"RecycledFiles\""));
        }
    }
}
