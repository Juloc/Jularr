using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Logic.Library;

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
        public void Patterns_FillTokensAndDropEmptyOnes()
        {
            var episode = new NamingValues("Show: Part", 2020, 1, 2.5m, "Pilot", "WEB-1080p", "GRP", null, 2);
            Assert.AreEqual("Show Part/Season 01/Show Part - S01E02.5 [WEB-1080p]", NamingPattern.Apply(Defaults[MediaType.Series], episode));
            Assert.AreEqual("Show Part (2020)/S01E02.5 - Pilot [WEB-1080p Proper] - GRP",
                NamingPattern.Apply("{Title} ({Year})/S{Season:00}E{Episode:00} - {Episode Title} [{Quality} {Proper}] - {Release Group}", episode));

            // Nothing known leaves no empty brackets or separators.
            var bare = new NamingValues("Film", null, null, null, null, null, null, null, 1);
            Assert.AreEqual("Film/Film", NamingPattern.Apply(Defaults[MediaType.Movie], bare));
            Assert.AreEqual("Film", NamingPattern.Apply("{Title} - {Edition} [{Release Group}]", bare));
            Assert.AreEqual("Film - GRP [WEB-1080p]", NamingPattern.Apply("{Title} - {Edition} - {Release Group} [{Quality} {Proper}]",
                new NamingValues("Film", null, null, null, null, "WEB-1080p", "GRP", null, 1)));
            Assert.AreEqual("Film", NamingPattern.Apply("{Edition}/{Title}", bare));

            // Titles keep what the cleanup would cut; values cannot open folders.
            Assert.AreEqual("Mr (2000)/Mr (2000)", NamingPattern.Apply(Defaults[MediaType.Movie], new NamingValues("Mr.", 2000, null, null, null, null, null, null, 1)));
            Assert.AreEqual("Foo () (2000)/Foo () (2000)", NamingPattern.Apply(Defaults[MediaType.Movie], new NamingValues("Foo ()", 2000, null, null, null, null, null, null, 1)));
            Assert.AreEqual("a b/a b", NamingPattern.Apply(Defaults[MediaType.Movie], new NamingValues("a/b", null, null, null, null, null, null, null, 1)));
        }

        [TestMethod]
        public void Patterns_RejectUnknownIncompleteAndEmptyParts()
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
        public async Task Patterns_AdminEdits_AreVersionChecked()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Library?Tab=Naming");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "Episode files");
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/library/naming");
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            var series = (await read.Content.ReadFromJsonAsync<BaseDataList<NamingPatternV1>>())!.Single(pattern => pattern.MediaTypeId == MediaType.Series);
            Assert.AreEqual(Defaults[MediaType.Series], series.Pattern);

            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series", new UpdateNamingPatternDataV1("{Title}/S{Season:00}/{Title} S{Season:00}E{Episode:00}", series.Version));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var stale = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series", new UpdateNamingPatternDataV1("{Title} {Season} {Episode}", series.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            using var invalid = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series", new UpdateNamingPatternDataV1("{Title}", series.Version + 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.AreEqual("{Title}/S{Season:00}/{Title} S{Season:00}E{Episode:00}", await host.ScalarAsync("SELECT \"Pattern\" FROM \"NamingPatterns\" WHERE \"MediaTypeId\" = 2"));
        }
    }
}
