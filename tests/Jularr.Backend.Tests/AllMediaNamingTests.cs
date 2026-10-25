using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Integrations.Data.Media;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class AllMediaNamingTests
    {
        [TestMethod]
        [DataRow(MediaSubtype.Manga, "anilist", "12345", "{Title} {anilist-{AnilistId}}/{Title} {Volume Label:00} {anilist-{AnilistId}}", "Title v01.cbz", "Title {anilist-12345}/Title v01 {anilist-12345}")]
        [DataRow(MediaSubtype.LightNovel, "anilist", "54321", "{Author}/{Title} {anilist-{AnilistId}}/{Title} {anilist-{AnilistId}}", "Title.epub", "Writer/Title {anilist-54321}/Title {anilist-54321}")]
        public void BooksAndManga_ImportAndRenameUseTheirConfiguredIdentities(MediaSubtype subtype, string provider, string id, string pattern, string source, string expected)
        {
            var patterns = new NamingSet(new Dictionary<(MediaType, MediaSubtype?), string> { [(MediaType.Book, null)] = pattern, [(MediaType.Book, subtype)] = pattern });
            var known = Values(MediaType.Book) with { MediaSubtypeId = subtype, Author = "Writer", ProviderIds = new Dictionary<string, string> { [provider] = id } };
            var imported = ImportPlacementLogic.Plan(MediaType.Book, subtype, "Title", 2020, null, [File(source)], patterns, null, author: "Writer", known: known).Single();
            Assert.AreEqual(expected, imported.TargetStem);
            var renamed = LibraryRenameLogic.Plan([new LibraryRenameFileData(1, 1, "Books", MediaType.Book, subtype, "old/" + source, null, 1, "Title", 2020, "Writer",
                subtype == MediaSubtype.Manga ? WorkUnitType.Volume : null, subtype == MediaSubtype.Manga ? 1 : null, null, null, null, null, null, 1, null,
                providerIds: "{\"" + provider + "\":\"" + id + "\"}")], patterns).Single();
            Assert.AreEqual(imported.TargetStem + imported.Extension, renamed.TargetPath);
        }

        [TestMethod]
        public void OpenLibraryDefault_DropsMissingIdAndLanguageWithTheirDecoration()
        {
            const string pattern = "{Author}/{Title} ({Year}) {olid-{OpenLibraryId}}/{Title} {olid-{OpenLibraryId}} [{Language}]";
            var values = Values(MediaType.Book) with { Author = "Writer" };
            NamingPattern.Validate(MediaType.Book, pattern, "Pattern");
            Assert.AreEqual("Writer/Title (2020)/Title", NamingPattern.Apply(pattern, values));
            Assert.AreEqual("Writer/Title (2020) {olid-OL123W}/Title {olid-OL123W} [de]", NamingPattern.Apply(pattern, values with
            {
                Language = "de", ProviderIds = new Dictionary<string, string> { ["openlibrary"] = "OL123W" }
            }));
        }

        [TestMethod]
        public void KnownIdentities_AreAvailableToEveryPatternWithoutProviderCalls()
        {
            var target = new WantedTargetData(1, 1, MediaType.Book, MediaSubtype.LightNovel, "Title", 2020, ["Title"], null, null, null, [], false, false, false, 0,
                ["anilist:123", "openlibrary:OL456W", "imdb:tt789", "plex:metadata:12"], [], "Writer", null, null);
            var values = ImportPlacementLogic.KnownValues(target);
            Assert.AreEqual("123 OL456W tt789 metadata-12 123", NamingPattern.Apply("{AnilistId} {OpenLibraryId} {ImdbId} {ProviderId:plex} {Primary Id}", values));
            Assert.AreEqual("Title", NamingPattern.Apply("{Title} {id-{ProviderId:unknown}}", values));
            Assert.ThrowsExactly<Jularr.Backend.Data.Common.DataValidationException>(() => NamingPattern.Validate(MediaType.Book, "{Title} {ProviderId:../../bad}", "Pattern"));
        }

        [TestMethod]
        public void MultiDiscMusic_NamesTracksByTaggedDiscAndTrackAndIncludesMusicBrainz()
        {
            const string pattern = "{Artist}/{Album} ({Year}) {mbid-{MusicBrainzId}}/{Disc Prefix:00}{Track:00} - {Track Title}";
            var patterns = Set(MediaType.Music, pattern);
            var tags = new Dictionary<string, AudioTags> { ["first.flac"] = new("Opening", 1, 1, null), ["last.flac"] = new("Closing", 2, 1, null) };
            var known = Values(MediaType.Music) with { ProviderIds = new Dictionary<string, string> { ["musicbrainz"] = "album-id" } };
            var tracks = ImportPlacementLogic.Plan(MediaType.Music, null, "Title", 2020, null, [File("last.flac"), File("first.flac")], patterns, null,
                author: "Artist", trackCount: 2, tags: tags, known: known);
            CollectionAssert.AreEqual(new[] { "Artist/Title (2020) {mbid-album-id}/01-01 - Opening", "Artist/Title (2020) {mbid-album-id}/02-01 - Closing" }, tracks.Select(track => track.TargetStem).ToArray());
            Assert.AreEqual("01", NamingPattern.Apply("{Disc Prefix:00}{Track:00}", known with { Disc = 1, DiscCount = 1, Track = 1 }));
        }

        [TestMethod]
        public void GameNaming_RenamesOneRomButPreservesAllPathsInsideAReleaseBundle()
        {
            var patterns = Set(MediaType.Game, "{Platform}/{Title} {igdb-{IgdbId}}/{Title} {igdb-{IgdbId}}");
            var known = Values(MediaType.Game) with { ProviderIds = new Dictionary<string, string> { ["igdb"] = "42" } };
            var single = ImportPlacementLogic.Plan(MediaType.Game, null, "Title", 2020, null, [File("old.sfc")], patterns, null, platform: "SNES", known: known).Single();
            Assert.AreEqual("SNES/Title {igdb-42}/Title {igdb-42}", single.TargetStem);
            var bundle = ImportPlacementLogic.Plan(MediaType.Game, null, "Title", 2020, null, [File("Disc/old.bin"), File("old.cue")], patterns, null, platform: "PS", known: known);
            CollectionAssert.AreEquivalent(new[] { "PS/Title {igdb-42}/Disc/old", "PS/Title {igdb-42}/old" }, bundle.Select(file => file.TargetStem).ToArray());
            var upper = ImportPlacementLogic.Plan(MediaType.Game, null, "Title", 2020, null, [File("Disc/Track.BIN"), File("Disc.CUE")], patterns, null, platform: "PS", known: known);
            CollectionAssert.AreEquivalent(new[] { "PS/Title {igdb-42}/Disc/Track.BIN", "PS/Title {igdb-42}/Disc.CUE" }, upper.Select(file => file.TargetStem + file.Extension).ToArray());
            Assert.HasCount(0, ImportPlacementLogic.Plan(MediaType.Game, null, "Title", 2020, null, [File("Track:01.bin"), File("disc.cue")], patterns, null, known: known));
        }

        [TestMethod]
        public void GameRename_UsesIdsForALoneRomAndLeavesCueBundlesIntact()
        {
            var patterns = Set(MediaType.Game, "{Platform}/{Title} {igdb-{IgdbId}}/{Title} {igdb-{IgdbId}} [{Region}]");
            var file = new LibraryRenameFileData(1, 1, "Games", MediaType.Game, null, "old/old.sfc", null, 1, "Title", 2020, null, null, null, null, null, null, null, null, 1, null,
                providerIds: "{\"igdb\":\"42\"}", platform: "SNES", releaseName: "old (USA).sfc");
            Assert.AreEqual("SNES/Title {igdb-42}/Title {igdb-42} [USA].sfc", LibraryRenameLogic.Plan([file], patterns).Single().TargetPath);
            var cue = new LibraryRenameFileData(2, 1, "Games", MediaType.Game, null, "old/game.cue", null, null, null, null, null, null, null, null, null, null, null, null, 1, null);
            Assert.HasCount(0, LibraryRenameLogic.Plan([file, cue], patterns));
        }

        [TestMethod]
        [DataRow(MultiEpisodeStyle.Extend, "S01E01-02-03")]
        [DataRow(MultiEpisodeStyle.Duplicate, "S01E01 - S01E02 - S01E03")]
        [DataRow(MultiEpisodeStyle.Repeat, "S01E01E02E03")]
        [DataRow(MultiEpisodeStyle.Scene, "S01E01-E02-E03")]
        [DataRow(MultiEpisodeStyle.Range, "S01E01-03")]
        [DataRow(MultiEpisodeStyle.PrefixedRange, "S01E01-E03")]
        public void SonarrMultiEpisodeStyles_RenderAllNumbers(MultiEpisodeStyle style, string expected)
        {
            var values = Values(MediaType.Series) with { Season = 1, Episode = 1, EpisodeNumbers = [1, 2, 3], Rules = NamingRules.Default with { MultiEpisodeStyle = style } };
            Assert.AreEqual("Title - " + expected, NamingPattern.Apply("{Title} - S{season:00}E{episode:00}", values));
        }

        [TestMethod]
        [DataRow(MultiEpisodeStyle.Extend, "012-013-014")]
        [DataRow(MultiEpisodeStyle.Duplicate, "012 - 013 - 014")]
        [DataRow(MultiEpisodeStyle.Repeat, "012-013-014")]
        [DataRow(MultiEpisodeStyle.Scene, "012-013-014")]
        [DataRow(MultiEpisodeStyle.Range, "012-014")]
        [DataRow(MultiEpisodeStyle.PrefixedRange, "012-014")]
        public void AnimeMultiEpisodeStyles_RenderAbsoluteNumbers(MultiEpisodeStyle style, string expected)
        {
            var values = Values(MediaType.Series) with { MediaSubtypeId = MediaSubtype.Anime, Absolute = 12, AbsoluteNumbers = [12, 13, 14], Rules = NamingRules.Default with { MultiEpisodeStyle = style } };
            Assert.AreEqual("Title - " + expected, NamingPattern.Apply("{Title} - {absolute:000}", values));
        }

        [TestMethod]
        public void MultipleEpisodeImport_RetainsCoverageAndUsesTheSelectedStyle()
        {
            var patterns = Set(MediaType.Series, "{Title}/Season {Season:00}/{Title} - S{Season:00}E{Episode:00} - {Episode Title}");
            var plan = ImportPlacementLogic.Plan(MediaType.Series, null, "Title", 2020, null, [File("Title.S01E01-E03.mkv")], patterns, null,
                episodes: [new TargetUnit(1, 1, 1, false, title: "Pilot")]).Single();
            Assert.AreEqual("Title/Season 01/Title - S01E01-E03 - Pilot", plan.TargetStem);
            CollectionAssert.AreEqual(new decimal[] { 2, 3 }, plan.FurtherEpisodes.ToArray());
        }

        [TestMethod]
        public void DailyFormats_ApplyOnlyToDatedReleasesAndKeepTheEpisodeTitle()
        {
            var patterns = new NamingSet(new Dictionary<(MediaType, MediaSubtype?), string> { [(MediaType.Series, null)] = "{Title}/{Title} S{Season:00}E{Episode:00}" },
                daily: new Dictionary<(MediaType, MediaSubtype?), string> { [(MediaType.Series, null)] = "{Series Title} - {Air-Date} - {Episode Title}" });
            var episodes = new TargetUnit[] { new(1, 1, 1, false, releasedOn: new DateOnly(2020, 1, 2), title: "News") };
            Assert.AreEqual("Title/Title - 2020-01-02 - News", ImportPlacementLogic.Plan(MediaType.Series, null, "Title", 2020, null, [File("Title.2020.01.02.mkv")], patterns, null, episodes: episodes).Single().TargetStem);
            Assert.AreEqual("Title/Title S01E01", ImportPlacementLogic.Plan(MediaType.Series, null, "Title", 2020, null, [File("Title.S01E01.mkv")], patterns, null, episodes: episodes).Single().TargetStem);
        }

        [TestMethod]
        public void RenameDisabled_StillUsesConfiguredFoldersAndKeepsTheOriginalFileStem()
        {
            var values = Values(MediaType.Book) with { OriginalFilename = "Original.Release", Rules = NamingRules.Default with { RenamesFiles = false } };
            Assert.AreEqual("Title/Original.Release", NamingPattern.Apply("{Title}/{Book Title}", values));
        }

        private static NamingValues Values(MediaType type) => new("Title", 2020, null, null, null, null, null, null, 1) { MediaTypeId = type };
        private static StorageFile File(string path) => new(path, 1000, DateTime.UnixEpoch);
        private static NamingSet Set(MediaType type, string pattern) => new(new Dictionary<(MediaType, MediaSubtype?), string> { [(type, null)] = pattern });
    }
}
