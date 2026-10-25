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
            Assert.AreEqual("Show - Part/Season 01/Show - Part - S01E02.5 [WEBDL-1080p]", NamingPattern.Apply(Defaults[MediaType.Series], episode));
            Assert.AreEqual("Show - Part (2020)/S01E02.5 - Pilot [WEBDL-1080p Proper] - GRP",
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

        // The formats the *arr community publishes (TRaSH) run unchanged: prefixes and suffixes around a token, any case, literal groups and filters.
        [TestMethod]
        public void NamingPattern_CommunityFormatsOfSonarrAndRadarr_RenderAsTheArrsDo()
        {
            var episode = new NamingValues("The Series Name's Title!", 2010, 1, 7, "Midnight", "HDTV-1080p", "RLSGRP", null, 2)
            {
                MediaTypeId = MediaType.Series,
                CustomFormats = new Dictionary<string, string> { ["iNTERNAL"] = "iNTERNAL" },
                MediaInfo = new Dictionary<string, string>
                {
                    ["MediaInfo VideoDynamicRangeType"] = "DV HDR10", ["MediaInfo AudioCodec"] = "DTS", ["MediaInfo AudioChannels"] = "5.1",
                    ["MediaInfo AudioLanguages"] = "[EN+DE]", ["MediaInfo VideoCodec"] = "x265"
                }
            };
            const string series = "{Series TitleYear} - S{season:00}E{episode:00} - {Episode CleanTitle} [{Custom Formats }{Quality Full}]{[MediaInfo VideoDynamicRangeType]}"
                + "{[Mediainfo AudioCodec}{ Mediainfo AudioChannels]}{MediaInfo AudioLanguages}{[MediaInfo VideoCodec]}{-Release Group}";
            NamingPattern.Validate(MediaType.Series, series, "Pattern");
            Assert.AreEqual("The Series Name's Title! (2010) - S01E07 - Midnight [iNTERNAL HDTV-1080p Proper][DV HDR10][DTS 5.1][EN+DE][x265]-RLSGRP", NamingPattern.Apply(series, episode));
            Assert.AreEqual("The Series Name's Title! (2010) - S01E07 - Midnight [HDTV-1080p Proper]-RLSGRP", NamingPattern.Apply(series, new NamingValues("The Series Name's Title!", 2010, 1, 7, "Midnight", "HDTV-1080p", "RLSGRP", null, 2) { MediaTypeId = MediaType.Series }));

            var movie = new NamingValues("The Matrix", 1999, null, null, null, "Bluray-1080p", "BHDStudio", "IMAX", 1)
            {
                ProviderIds = new Dictionary<string, string> { ["imdb"] = "tt0133093" },
                CustomFormats = new Dictionary<string, string> { ["Remux"] = "Remux" },
                MediaInfo = new Dictionary<string, string>
                {
                    ["MediaInfo VideoDynamicRangeType"] = "HDR10", ["MediaInfo AudioCodec"] = "DTS", ["MediaInfo AudioChannels"] = "7.1", ["MediaInfo VideoCodec"] = "x265"
                }
            };
            const string film = "{Movie CleanTitle} {(Release Year)} {imdb-{ImdbId}} {edition-{Edition Tags}} {[Custom Formats]}{[Quality Full]}{[MediaInfo 3D]}"
                + "{[MediaInfo VideoDynamicRangeType]}{[Mediainfo AudioCodec}{ Mediainfo AudioChannels]}{[Mediainfo VideoCodec]}{-Release Group}";
            NamingPattern.Validate(MediaType.Movie, film, "Pattern");
            Assert.AreEqual("The Matrix (1999) {imdb-tt0133093} {edition-IMAX} [Remux][Bluray-1080p][HDR10][DTS 7.1][x265]-BHDStudio", NamingPattern.Apply(film, movie));

            // The literal groups leave with their empty token.
            Assert.AreEqual("The Matrix (1999) [Bluray-1080p]-BHDStudio", NamingPattern.Apply(film, new NamingValues("The Matrix", 1999, null, null, null, "Bluray-1080p", "BHDStudio", null, 1)));
            Assert.AreEqual("The Matrix (1999)", NamingPattern.Apply("{Movie CleanTitle} ({Release Year})", movie));
        }

        [TestMethod]
        public void NamingPattern_TokenCaseSeparatorEscapesAndFilters_FollowTheArrs()
        {
            var episode = new NamingValues("The Series", 2010, 1, 7, "Mid Night", "HDTV-1080p", "Rls Grp", null, 1)
            {
                MediaTypeId = MediaType.Series,
                AirDate = new DateOnly(2020, 9, 3),
                Absolute = 12,
                CustomFormats = new Dictionary<string, string> { ["AMZN"] = "AMZN", ["x265"] = "x265" },
                MediaInfo = new Dictionary<string, string> { ["MediaInfo AudioCodec"] = "DTS", ["MediaInfo VideoCodec"] = "x264", ["MediaInfo Simple"] = "x264 DTS" }
            };
            Assert.AreEqual("the series/THE SERIES/The.Series/The_Series/The-Series", NamingPattern.Apply("{series title}/{SERIES TITLE}/{Series.Title}/{Series_Title}/{Series-Title}", episode));
            Assert.AreEqual("2020-09-03 2020.09.03 2020 09 03", NamingPattern.Apply("{Air-Date} {Air.Date} {Air Date}", episode));
            Assert.AreEqual("S1E7 - 012 - Mid.Night", NamingPattern.Apply("S{season:0}E{episode:0} - {absolute:000} - {Episode.Title}", episode));
            Assert.AreEqual("The S... {x} The Series", NamingPattern.Apply("{Series Title:8} {{x}} {Series Title}", episode));
            Assert.AreEqual("AMZN x265;x265;AMZN;AMZN x265", NamingPattern.Apply("{Custom Formats};{Custom Formats:x265};{Custom Formats:-x265};{Custom Formats:AMZN,x265}", episode));
            Assert.AreEqual("x264 DTS;DTS;x264", NamingPattern.Apply("{MediaInfo Simple};{MediaInfo Audio};{MediaInfo Video}", episode));
            Assert.AreEqual("naming_unknown_token", Assert.ThrowsExactly<DataValidationException>(() => NamingPattern.Validate(MediaType.Series, "{Title} {Studio}/S{Season:00}E{Episode:00}", "Pattern")).Code);
            Assert.AreEqual("naming_unknown_token", Assert.ThrowsExactly<DataValidationException>(() => NamingPattern.Validate(MediaType.Movie, "{Title} {imdb-{Nothing}}", "Pattern")).Code);
            NamingPattern.Validate(MediaType.Movie, "{movie title} {{x}} {Release Year}", "Pattern");
        }

        [TestMethod]
        public void NamingPattern_ColonsIllegalCharactersAndSeason0_FollowTheRules()
        {
            NamingValues Show(NamingRules rules, decimal season = 1) => new("Show: The Part: 2", 2020, season, 3, "Who: Me", "WEBDL-1080p", "GRP", null, 1) { MediaTypeId = MediaType.Series, Rules = rules };
            const string pattern = "{Title}/Season {Season:00}/{Title} - S{Season:00}E{Episode:00} - {Episode Title}";
            string Name(ColonReplacement colon, string? custom = null, bool replaces = true) =>
                NamingPattern.Apply("{Title} - {Episode Title}", Show(new NamingRules(replaces, colon, custom, "Specials")));
            Assert.AreEqual("Show - The Part - 2 - Who - Me", Name(ColonReplacement.Smart));
            Assert.AreEqual("Show The Part 2 - Who Me", Name(ColonReplacement.Delete));
            Assert.AreEqual("Show- The Part- 2 - Who- Me", Name(ColonReplacement.Dash));
            Assert.AreEqual("Show - The Part - 2 - Who - Me", Name(ColonReplacement.SpaceDash));
            Assert.AreEqual("Show - The Part - 2 - Who - Me", Name(ColonReplacement.SpaceDashSpace));
            Assert.AreEqual("Show~ The Part~ 2 - Who~ Me", Name(ColonReplacement.Custom, "~"));

            // Not replacing illegal characters removes them without a space.
            Assert.AreEqual("Show The Part 2 - Who Me", Name(ColonReplacement.Smart, replaces: false));
            Assert.AreEqual("AB", NamingPattern.Apply("{Title}", new NamingValues("A*B", null, null, null, null, null, null, null, 1) { Rules = new NamingRules(false, ColonReplacement.Smart, null, null) }));
            Assert.AreEqual("A B", NamingPattern.Apply("{Title}", new NamingValues("A*B", null, null, null, null, null, null, null, 1)));

            // Season 0 goes to its own folder, any other season keeps the season folder.
            var rules = new NamingRules(true, ColonReplacement.Smart, null, "Specials");
            Assert.AreEqual("Show - The Part - 2/Specials/Show - The Part - 2 - S00E03 - Who - Me", NamingPattern.Apply(pattern, Show(rules, 0)));
            Assert.AreEqual("Show - The Part - 2/Season 01/Show - The Part - 2 - S01E03 - Who - Me", NamingPattern.Apply(pattern, Show(rules)));
            Assert.AreEqual("Show - The Part - 2/Season 00/Show - The Part - 2 - S00E03 - Who - Me", NamingPattern.Apply(pattern, Show(rules with { SpecialsFolder = null }, 0)));
        }

        [TestMethod]
        public void NamingPattern_SpecialsFolder_RendersTokensWithTheNamingRules()
        {
            var values = new NamingValues("Show: Part", 2020, 0, 3, "Pilot", "WEBDL-1080p", "GRP", null, 1)
            {
                MediaTypeId = MediaType.Series,
                ProviderIds = new Dictionary<string, string> { ["imdb"] = "tt12345" },
                Rules = new NamingRules(true, ColonReplacement.Custom, "~", "Specials - {ImdbId}")
            };
            const string pattern = "{Series Title}/Season {season} - {ImdbId}/{Series Title} - S{season:00}E{episode:00}";
            Assert.AreEqual("Show~ Part/Specials - tt12345/Show~ Part - S00E03", NamingPattern.Apply(pattern, values));
            Assert.AreEqual("Show~ Part/Specials/Show~ Part - S00E03", NamingPattern.Apply(pattern, values with { ProviderIds = null }));
            Assert.AreEqual("Show~ Part/show~ part - extras 00/Show~ Part - S00E03",
                NamingPattern.Apply(pattern, values with { Rules = values.Rules with { SpecialsFolder = "{series title} - extras {season:00}" } }));
            Assert.AreEqual("Show~ Part/Season 1 - tt12345/Show~ Part - S01E03", NamingPattern.Apply(pattern, values with { Season = 1 }));
        }

        [TestMethod]
        public void NamingPattern_OriginalFilename_ExcludesTheExtension()
        {
            var values = new NamingValues("Movie", 2020, null, null, null, null, null, null, 1)
            {
                OriginalFilename = "Movie.2020.1080p-GROUP"
            };
            Assert.AreEqual("Movie.2020.1080p-GROUP", NamingPattern.Apply("{Original Filename}", values));
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
            Assert.AreEqual("Film [EN]", NamingPattern.Apply("{Movie Title} {MediaInfo AudioLanguagesAll:EN+DE}", movie));
            Assert.AreEqual("Film [JA]", NamingPattern.Apply("{Movie Title} {MediaInfo AudioLanguagesAll:-EN}", movie));
            Assert.AreEqual("Film [EN+--]", NamingPattern.Apply("{Movie Title} {MediaInfo AudioLanguagesAll:EN+}", movie));
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
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Naming");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "Episode files");
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/library/naming");
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            var all = (await read.Content.ReadFromJsonAsync<BaseDataList<NamingPatternV1>>())!;
            var series = all.Single(pattern => pattern.MediaTypeId == MediaType.Series && pattern.MediaSubtypeId is null);
            Assert.AreEqual(Defaults[MediaType.Series], all.Single(pattern => pattern.MediaSubtypeId == MediaSubtype.Anime).Folder + "/" + all.Single(pattern => pattern.MediaSubtypeId == MediaSubtype.Anime).File);
            Assert.AreEqual(Defaults[MediaType.Series], series.Folder + "/" + series.File);
            Assert.AreEqual("Example Show/Season 01/Example Show - S01E02 [WEBDL-1080p].mkv", series.Example);
            StringAssert.Contains(html, "Example Show/Season 01/Example Show - S01E02 [WEBDL-1080p].mkv");

            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series", new UpdateNamingPatternDataV1("{Title}/S{Season:00}", "{Title} S{Season:00}E{Episode:00}", series.Version));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var stale = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series", new UpdateNamingPatternDataV1(null, "{Title} {Season} {Episode}", series.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            using var invalid = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series", new UpdateNamingPatternDataV1(null, "{Title}", series.Version + 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.AreEqual("{Title}/S{Season:00}/{Title} S{Season:00}E{Episode:00}", await host.ScalarAsync("SELECT \"Pattern\" FROM \"NamingPatterns\" WHERE \"MediaTypeId\" = 2 AND \"MediaSubtypeId\" IS NULL"));
        }

        [TestMethod]
        public async Task NamingPreview_TypedPattern_ShowsTheExampleOrWhyItCannotBeSaved()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            static string Address(string type, string? folder, string file) =>
                "/api/v1/admin/library/naming/preview?mediaTypeId=" + type + (folder is null ? "" : "&folder=" + Uri.EscapeDataString(folder)) + "&file=" + Uri.EscapeDataString(file);

            // What is typed, before it is saved, with the same rules as the save.
            using var good = await host.SendAsync(HttpMethod.Get, Address("Series", "{Title}/S{Season:00}", "{Series.Title} S{Season:00}E{Episode:00}"));
            Assert.AreEqual(HttpStatusCode.OK, good.StatusCode, await good.Content.ReadAsStringAsync());
            var preview = (await good.Content.ReadFromJsonAsync<NamingPreviewV1>())!;
            Assert.AreEqual("Example Show/S01/Example.Show S01E02.mkv", preview.Example);
            Assert.IsNull(preview.Problem);

            using var unknown = await host.SendAsync(HttpMethod.Get, Address("Movie", null, "{Title} {Nothing}"));
            Assert.AreEqual("naming_unknown_token", (await unknown.Content.ReadFromJsonAsync<NamingPreviewV1>())!.Problem);
            using var incomplete = await host.SendAsync(HttpMethod.Get, Address("Series", null, "{Title} {Season:00}"));
            var answer = (await incomplete.Content.ReadFromJsonAsync<NamingPreviewV1>())!;
            Assert.IsNull(answer.Example);
            Assert.AreEqual("naming_incomplete", answer.Problem);

            // The page names its tokens in a drawer and carries the preview's problems as texts for the script.
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Naming");
            var html = await page.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "data-naming-token=\"{Title}\"");
            StringAssert.Contains(html, "data-problem-naming_unknown_token=");
        }

        [TestMethod]
        public void NamingSet_SubtypePattern_NamesItsFilesAndTheTypeKeepsTheRest()
        {
            var set = new NamingSet(new Dictionary<(MediaType, MediaSubtype?), string>
            {
                [(MediaType.Movie, null)] = Defaults[MediaType.Movie],
                [(MediaType.Series, null)] = Defaults[MediaType.Series],
                [(MediaType.Series, MediaSubtype.Anime)] = "{Title}/S{Season:00}E{Episode:00} - {Absolute:000}"
            });
            Assert.AreEqual("{Title}/S{Season:00}E{Episode:00} - {Absolute:000}", set.Pattern(MediaType.Series, MediaSubtype.Anime));
            Assert.AreEqual(Defaults[MediaType.Series], set.Pattern(MediaType.Series, null));
            Assert.AreEqual(Defaults[MediaType.Movie], set.Pattern(MediaType.Movie, MediaSubtype.Anime));
            Assert.AreEqual(NamingRules.Default, set.Rules(MediaType.Series));
        }

        [TestMethod]
        public async Task NamingSettings_AdminEdit_IsVersionCheckedAndHeldPerType()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Naming");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "Anime episode files");
            StringAssert.Contains(html, "Replace illegal characters");
            StringAssert.Contains(html, "Specials folder format");
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/library/naming/settings");
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            var settings = (await read.Content.ReadFromJsonAsync<BaseDataList<NamingSettingsV1>>())!.ToArray();
            var series = settings.Single(item => item.MediaTypeId == MediaType.Series);
            Assert.AreEqual((true, ColonReplacement.Smart, "Specials"), (series.ReplacesIllegalCharacters, series.ColonReplacementId, series.SpecialsFolder));
            Assert.IsNull(settings.Single(item => item.MediaTypeId == MediaType.Movie).SpecialsFolder);

            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series/settings", new UpdateNamingSettingsDataV1(true, ColonReplacement.Custom, "~", "Extras", series.Version));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.AreEqual((short)6, await host.ScalarAsync("SELECT \"ColonReplacementId\" FROM \"NamingSettings\" WHERE \"MediaTypeId\" = 2"));
            Assert.AreEqual("Extras", await host.ScalarAsync("SELECT \"SpecialsFolder\" FROM \"NamingSettings\" WHERE \"MediaTypeId\" = 2"));

            // A stale edit, a custom choice without its text, a colon or slash as the replacement and a folder for films are refused; a choice other than custom forgets its text.
            using var stale = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series/settings", new UpdateNamingSettingsDataV1(true, ColonReplacement.Smart, null, null, series.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            using var none = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series/settings", new UpdateNamingSettingsDataV1(true, ColonReplacement.Custom, null, null, series.Version + 1));
            StringAssert.Contains(await none.Content.ReadAsStringAsync(), "required");
            using var slash = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series/settings", new UpdateNamingSettingsDataV1(true, ColonReplacement.Custom, "/", null, series.Version + 1));
            StringAssert.Contains(await slash.Content.ReadAsStringAsync(), "invalid_value");
            using var film = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Movie/settings", new UpdateNamingSettingsDataV1(true, ColonReplacement.Smart, null, "Extras", 1));
            StringAssert.Contains(await film.Content.ReadAsStringAsync(), "invalid_value");
            foreach (var invalid in new[] { ("Specials - {Unknown}", "naming_unknown_token"), ("..", "naming_invalid_folder"), ("Extras/Hidden", "naming_invalid_folder"), ("Extras\\Hidden", "naming_invalid_folder") })
            {
                using var refused = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series/settings",
                    new UpdateNamingSettingsDataV1(true, ColonReplacement.Smart, null, invalid.Item1, series.Version + 1));
                Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
                StringAssert.Contains(await refused.Content.ReadAsStringAsync(), invalid.Item2);
                Assert.AreEqual("Extras", await host.ScalarAsync("SELECT \"SpecialsFolder\" FROM \"NamingSettings\" WHERE \"MediaTypeId\" = 2"));
            }

            using var plain = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series/settings", new UpdateNamingSettingsDataV1(false, ColonReplacement.Dash, "~", null, series.Version + 1));
            Assert.AreEqual(HttpStatusCode.OK, plain.StatusCode, await plain.Content.ReadAsStringAsync());
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"CustomColonReplacement\" FROM \"NamingSettings\" WHERE \"MediaTypeId\" = 2"));

            // The anime pattern is its own: saved apart, the type's stays.
            using var all = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/library/naming");
            var anime = (await all.Content.ReadFromJsonAsync<BaseDataList<NamingPatternV1>>())!.Single(pattern => pattern.MediaSubtypeId == MediaSubtype.Anime);
            using var edited = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series/Anime", new UpdateNamingPatternDataV1("{Title}/S{Season:00}", "{Title} S{Season:00}E{Episode:00} {absolute:000}", anime.Version));
            Assert.AreEqual(HttpStatusCode.OK, edited.StatusCode, await edited.Content.ReadAsStringAsync());
            Assert.AreEqual(Defaults[MediaType.Series], await host.ScalarAsync("SELECT \"Pattern\" FROM \"NamingPatterns\" WHERE \"MediaTypeId\" = 2 AND \"MediaSubtypeId\" IS NULL"));
            Assert.AreEqual("{Title}/S{Season:00}/{Title} S{Season:00}E{Episode:00} {absolute:000}", await host.ScalarAsync("SELECT \"Pattern\" FROM \"NamingPatterns\" WHERE \"MediaSubtypeId\" = 1"));
        }

        [TestMethod]
        public async Task NamingSettings_SpecialsFormat_OnlyMovesFilesAfterTheConfirmedPreview()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var mount = await LibraryTests.CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mount.ToString(), "Shows");
            Directory.CreateDirectory(folder);
            var original = Path.Combine(folder, "Show.S00E03.mkv");
            File.WriteAllText(original, "special episode");
            var root = await LibraryTests.CreateRootAsync(host, mount, "Shows", MediaType.Series);
            await LibraryTests.ScanAsync(host, root);
            var file = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\" WHERE \"RelativePath\" = 'Show.S00E03.mkv'"))!;
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + file + "/assignment",
                new AssignLibraryFileDataV1(null, "Example Show", WorkUnitType.Episode, 3, 0));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            await host.Fixture.RunSqlAsync("INSERT INTO \"WorkExternalIdentities\" (\"WorkId\", \"Namespace\", \"Value\") SELECT \"Id\", 'imdb', 'tt12345' FROM \"Works\"");
            var asset = await host.ScalarAsync("SELECT \"MediaAssetId\" FROM \"StoredFiles\" WHERE \"Id\" = " + file);

            using var pattern = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series",
                new UpdateNamingPatternDataV1("{Series Title}/Season {season} - {ImdbId}", "{Series Title} - S{season:00}E{episode:00}", 1));
            Assert.AreEqual(HttpStatusCode.OK, pattern.StatusCode, await pattern.Content.ReadAsStringAsync());
            using var settings = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/naming/Series/settings",
                new UpdateNamingSettingsDataV1(true, ColonReplacement.Smart, null, "Specials - {ImdbId}", 1));
            Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode, await settings.Content.ReadAsStringAsync());
            Assert.IsTrue(File.Exists(original));

            using var preview = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/library/renames");
            Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode, await preview.Content.ReadAsStringAsync());
            var plan = (await preview.Content.ReadFromJsonAsync<LibraryRenamePlanV1>())!;
            const string target = "Example Show/Specials - tt12345/Example Show - S00E03.mkv";
            Assert.AreEqual(target, plan.Items.Single().TargetPath);
            Assert.IsTrue(File.Exists(original));
            using var applied = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/library/renames", new ApplyLibraryRenamesDataV1(plan.Fingerprint));
            Assert.AreEqual(HttpStatusCode.OK, applied.StatusCode, await applied.Content.ReadAsStringAsync());
            Assert.IsFalse(File.Exists(original));
            Assert.IsTrue(File.Exists(Path.Combine(folder, target.Replace('/', Path.DirectorySeparatorChar))));
            Assert.AreEqual(asset, await host.ScalarAsync("SELECT \"MediaAssetId\" FROM \"StoredFiles\" WHERE \"Id\" = " + file));
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
            var version = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"NamingPatterns\" WHERE \"MediaTypeId\" = 2 AND \"MediaSubtypeId\" IS NULL"))!;
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
