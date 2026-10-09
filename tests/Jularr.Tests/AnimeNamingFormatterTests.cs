using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Naming;

namespace Jularr.Tests;

[TestClass]
public sealed class AnimeNamingFormatterTests
{
    private static readonly AnimeNamingProfile Default = AnimeNamingPresets.SonarrDefault();

    [TestMethod]
    public void SonarrMediaInfoPresetReproducesTheCurrentSonarrExamples()
    {
        var profile = AnimeNamingPresets.SonarrMediaInfo();

        Assert.AreEqual(
            "The Series Title's! - S01E01 - Episode Title (1) WEBDL-1080p Proper AVC DTS[DE] [EN+DE] RlsGrp tt12345",
            AnimeNamingFormatter.BuildEpisodeFileName(profile, AnimeNamingSamples.Single(AnimeSeriesType.Standard)));
        Assert.AreEqual(
            "The Series Title's! - S01E01-E03 - Episode Title WEBDL-1080p Proper AVC DTS[DE] [EN+DE] RlsGrp tt12345",
            AnimeNamingFormatter.BuildEpisodeFileName(profile, AnimeNamingSamples.Multi(AnimeSeriesType.Standard)));
        Assert.AreEqual(AnimeNamingPresets.SonarrMediaInfoId, AnimeNamingPresets.CreateDefaultState().DefaultProfileId);
    }

    [TestMethod]
    public void SonarrDefaultPresetMatchesSonarrSamplePreview()
    {
        var preview = AnimeNamingSamples.Preview(Default).ToDictionary(line => line.Label, line => line.Value);

        Assert.AreEqual("The Series Title's!", preview["Series folder"]);
        Assert.AreEqual("Season 1", preview["Season folder"]);
        Assert.AreEqual("Specials", preview["Specials folder"]);
        Assert.AreEqual("The Series Title's! - S01E01 - Episode Title (1) WEBDL-1080p Proper.mkv", preview["Standard episode"]);
        Assert.AreEqual("The Series Title's! - S01E01-E03 - Episode Title WEBDL-1080p Proper.mkv", preview["Multi-episode"]);
        Assert.AreEqual("The Series Title's! - 2013-10-30 - Episode Title (1) WEBDL-1080p Proper.mkv", preview["Daily episode"]);
        Assert.AreEqual("The Series Title's! - S01E01 - Episode Title (1) WEBDL-1080p Proper.mkv", preview["Anime episode"]);
    }

    [TestMethod]
    public void ALeadingUnderscoreOfATemplateIsKeptWhereTheSharedEngineWouldTrimIt()
    {
        var rendered = AnimeNamingFormatter.Render("_{Series Title}", AnimeNamingScope.SeriesFolder, Default, AnimeNamingSamples.Single(AnimeSeriesType.Standard));

        Assert.AreEqual("_The Series Title's!", rendered);
    }

    [TestMethod]
    public void RendersSeriesTitleVariantsAndProviderIds()
    {
        var request = AnimeNamingSamples.Single(AnimeSeriesType.Standard);

        Assert.AreEqual("The Series Title's!", Render("{Series Title}", request));
        Assert.AreEqual("The Series Titles!", Render("{Series CleanTitle}", request));
        Assert.AreEqual("The Series Title's! (2010)", Render("{Series TitleYear}", request));
        Assert.AreEqual("The Series Titles! 2010", Render("{Series CleanTitleYear}", request));
        Assert.AreEqual("Series Title's!, The", Render("{Series TitleThe}", request));
        Assert.AreEqual("Series Titles! The", Render("{Series CleanTitleThe}", request));
        Assert.AreEqual("S", Render("{Series TitleFirstCharacter}", request));
        Assert.AreEqual("2010", Render("{Series Year}", request));
        Assert.AreEqual("98765 54321 12345 11223 tt12345", Render("{AniListId} {MalId} {TvdbId} {TmdbId} {ImdbId}", request));

        var withYear = request with { Series = request.Series with { Title = "Frieren (2023)", Year = 2023 } };
        Assert.AreEqual("Frieren (2023)", Render("{Series TitleYear}", withYear));
        Assert.AreEqual("Frieren", Render("{Series TitleWithoutYear}", withYear));
        Assert.AreEqual("Frieren", Render("{Series CleanTitleWithoutYear}", withYear));
        Assert.AreEqual("Tom and Jerry", AnimeNamingFormatter.CleanTitle("Tom & Jerry"));
    }

    [TestMethod]
    public void AppliesSeparatorCaseAndOptionalPrefixSuffix()
    {
        var request = AnimeNamingSamples.Single(AnimeSeriesType.Standard);
        var noGroup = request with { Release = AnimeReleaseParser.Parse("Show - S01E01 - Title [1080p]") };

        Assert.AreEqual("The.Series.Title's!", Render("{Series.Title}", request));
        Assert.AreEqual("The_Series_Title's!", Render("{Series_Title}", request));
        Assert.AreEqual("the series title's!", Render("{series title}", request));
        Assert.AreEqual("THE SERIES TITLE'S!", Render("{SERIES TITLE}", request));
        Assert.AreEqual("2013-10-30", Render("{Air-Date}", request));
        Assert.AreEqual("2013 10 30", Render("{Air Date}", request));
        Assert.AreEqual("Show [RlsGrp]", Render("Show {[Release Group]}", request));
        Assert.AreEqual("Show-RlsGrp", Render("Show{-Release Group}", request));
        Assert.AreEqual("Show", Render("Show {[Release Group]}", noGroup));
        Assert.AreEqual("Show", Render("Show{-Release Group}", noGroup));
    }

    [TestMethod]
    public void RendersReleaseDerivedTokensFromTheSharedParser()
    {
        var release = AnimeReleaseParser.Parse(
            "[SubsPlease] Frieren - S01E05v2 (1080p WEB-DL x265 10bit HDR10 FLAC 2.0) [JA+EN]");
        var request = new AnimeNamingRequest(
            new AnimeNamingSeries("Frieren", 2023),
            [new AnimeNamingEpisode(1, 5, 5, "Phantoms of the Dead")],
            release);

        Assert.AreEqual("SubsPlease", Render("{Release Group}", request));
        Assert.AreEqual("WEBDL-1080p v2", Render("{Quality Full}", request));
        Assert.AreEqual("WEBDL-1080p", Render("{Quality Title}", request));
        Assert.AreEqual("v2", Render("{Quality Proper}", request));
        Assert.AreEqual("WEB-1080p", Render("{Quality Key}", request));
        Assert.AreEqual("x265", Render("{MediaInfo VideoCodec}", request));
        Assert.AreEqual("10", Render("{MediaInfo VideoBitDepth}", request));
        Assert.AreEqual("HDR10", Render("{MediaInfo VideoDynamicRangeType}", request));
        Assert.AreEqual("FLAC", Render("{MediaInfo AudioCodec}", request));
        Assert.AreEqual("2.0", Render("{MediaInfo AudioChannels}", request));
        Assert.AreEqual("005", Render("{absolute:000}", request));
        Assert.AreEqual("Phantoms of the Dead", Render("{Episode Title}", request));

        var standard = request with { Series = request.Series with { SeriesType = AnimeSeriesType.Standard } };
        Assert.AreEqual("Proper", Render("{Quality Proper}", standard));

        var repack = request with { Release = AnimeReleaseParser.Parse("Show.S01E01.REPACK.720p.HDTV.x264-GRP"), Series = standard.Series };
        Assert.AreEqual("HDTV-720p Repack", Render("{Quality Full}", repack));
    }

    [TestMethod]
    public void AudioLanguagesOmitEnglishOnlyLikeSonarr()
    {
        var english = AnimeNamingSamples.Single(AnimeSeriesType.Standard) with
        {
            Release = AnimeNamingSamples.Single(AnimeSeriesType.Standard).Release! with { AudioLanguages = ["EN"] }
        };
        var dual = english with { Release = english.Release! with { AudioLanguages = ["JA", "EN"] } };

        Assert.AreEqual("", Render("{MediaInfo AudioLanguages}", english));
        Assert.AreEqual("[EN]", Render("{MediaInfo AudioLanguagesAll}", english));
        Assert.AreEqual("[JA+EN]", Render("{MediaInfo AudioLanguages}", dual));
        Assert.AreEqual("[EN+DE]", Render("{MediaInfo SubtitleLanguages}", dual));
    }

    [TestMethod]
    [DataRow(AnimeMultiEpisodeStyle.Extend, "S01E01-02-03", "001-002-003")]
    [DataRow(AnimeMultiEpisodeStyle.Duplicate, "S01E01 - S01E02 - S01E03", "001-002-003")]
    [DataRow(AnimeMultiEpisodeStyle.Repeat, "S01E01E02E03", "001-002-003")]
    [DataRow(AnimeMultiEpisodeStyle.Scene, "S01E01-E02-E03", "001-002-003")]
    [DataRow(AnimeMultiEpisodeStyle.Range, "S01E01-03", "001-003")]
    [DataRow(AnimeMultiEpisodeStyle.PrefixedRange, "S01E01-E03", "001-003")]
    public void RendersEveryMultiEpisodeStyle(AnimeMultiEpisodeStyle style, string seasonEpisode, string absolute)
    {
        var profile = Default with { MultiEpisodeStyle = style };
        var request = AnimeNamingSamples.Multi(AnimeSeriesType.Anime);

        Assert.AreEqual(
            $"Show - {seasonEpisode} - {absolute}",
            AnimeNamingFormatter.Render("Show - S{season:00}E{episode:00} - {absolute:000}", AnimeNamingScope.EpisodeFile, profile, request, null));
    }

    [TestMethod]
    public void MultiEpisodeTitlesJoinUnlessOnlyPartNumbersDiffer()
    {
        var request = new AnimeNamingRequest(
            new AnimeNamingSeries("Show"),
            [new AnimeNamingEpisode(1, 1, 1, "Beginning"), new AnimeNamingEpisode(1, 2, 2, "Ending")]);

        Assert.AreEqual("Beginning + Ending", Render("{Episode Title}", request));
        Assert.AreEqual("Episode Title", Render("{Episode Title}", AnimeNamingSamples.Multi(AnimeSeriesType.Standard)));
    }

    [TestMethod]
    public void ReplacesOrRemovesIllegalCharacters()
    {
        const string value = "A/B\\C<D>E?F*G|H\"I";

        Assert.AreEqual("A+B+CDE!F-GHI", AnimeNamingFormatter.CleanFileName(value, Default));
        Assert.AreEqual("ABCDEFGHI", AnimeNamingFormatter.CleanFileName(value, Default with { ReplaceIllegalCharacters = false }));
        Assert.AreEqual("Title", AnimeNamingFormatter.CleanFileName(" .Title ", Default));
    }

    [TestMethod]
    [DataRow(AnimeColonReplacement.Delete, "ReZero Starting Life", "1030")]
    [DataRow(AnimeColonReplacement.Dash, "Re-Zero- Starting Life", "10-30")]
    [DataRow(AnimeColonReplacement.SpaceDash, "Re -Zero - Starting Life", "10 -30")]
    [DataRow(AnimeColonReplacement.SpaceDashSpace, "Re - Zero -  Starting Life", "10 - 30")]
    [DataRow(AnimeColonReplacement.Smart, "Re-Zero - Starting Life", "10-30")]
    [DataRow(AnimeColonReplacement.Custom, "Re~Zero~ Starting Life", "10~30")]
    public void AppliesEachColonReplacementStrategy(AnimeColonReplacement strategy, string title, string time)
    {
        var profile = Default with { ColonReplacement = strategy, CustomColonReplacement = "~" };

        Assert.AreEqual(title, AnimeNamingFormatter.CleanFileName("Re:Zero: Starting Life", profile));
        Assert.AreEqual(time, AnimeNamingFormatter.CleanFileName("10:30", profile));
    }

    [TestMethod]
    public void ColonsAreRemovedWhenIllegalCharacterReplacementIsOff()
    {
        var profile = Default with { ReplaceIllegalCharacters = false, ColonReplacement = AnimeColonReplacement.Smart };

        Assert.AreEqual("ReZero Starting Life", AnimeNamingFormatter.CleanFileName("Re:Zero: Starting Life", profile));
    }

    [TestMethod]
    public void SelectsAnimeDailyOrStandardTemplateLikeSonarr()
    {
        var profile = Default with
        {
            StandardEpisodeFormat = "std S{season:00}E{episode:00}",
            AnimeEpisodeFormat = "anime {absolute:000}",
            DailyEpisodeFormat = "daily {Air-Date}"
        };
        var anime = AnimeNamingSamples.Single(AnimeSeriesType.Anime);
        var missingAbsolute = anime with { Episodes = [anime.Episodes[0] with { AbsoluteEpisodeNumber = null }] };

        Assert.AreEqual("anime 001", AnimeNamingFormatter.BuildEpisodeFileName(profile, anime));
        Assert.AreEqual("std S01E01", AnimeNamingFormatter.BuildEpisodeFileName(profile, missingAbsolute));
        Assert.AreEqual("daily 2013-10-30", AnimeNamingFormatter.BuildEpisodeFileName(profile, AnimeNamingSamples.Single(AnimeSeriesType.Daily)));
        Assert.AreEqual("std S01E01", AnimeNamingFormatter.BuildEpisodeFileName(profile, AnimeNamingSamples.Single(AnimeSeriesType.Standard)));
    }

    [TestMethod]
    public void SeasonFoldersUseSpecialsFormatAndCanBeDisabled()
    {
        var series = AnimeNamingSamples.Series(AnimeSeriesType.Anime);
        var padded = Default with { SeasonFolderFormat = "Season {season:00}" };

        Assert.AreEqual("Season 02", AnimeNamingFormatter.BuildSeasonFolderName(padded, series, 2));
        Assert.AreEqual("Specials", AnimeNamingFormatter.BuildSeasonFolderName(padded, series, 0));
        Assert.IsNull(AnimeNamingFormatter.BuildSeasonFolderName(padded with { UseSeasonFolders = false }, series, 1));
    }

    [TestMethod]
    public void CleansUpSeparatorsAndReservedDeviceNames()
    {
        var request = new AnimeNamingRequest(new AnimeNamingSeries("CON"), [new AnimeNamingEpisode(1, 1, null, "Title")]);

        Assert.AreEqual("CON_", AnimeNamingFormatter.Render("{Series Title}", AnimeNamingScope.SeriesFolder, Default, request, null));
        Assert.AreEqual("CON_S01E01", AnimeNamingFormatter.Render("{Series Title}.S{season:00}E{episode:00}", AnimeNamingScope.EpisodeFile, Default, request, null));
        Assert.AreEqual("CON - Title", AnimeNamingFormatter.Render("{Series Title} - {Episode Title} - {Release Group}", AnimeNamingScope.EpisodeFile, Default, request, null));
    }

    [TestMethod]
    public void ValidationRejectsBrokenTemplates()
    {
        Assert.AreEqual(0, AnimeNamingFormatter.Validate(Default).Count);
        foreach (var preset in AnimeNamingPresets.All)
        {
            Assert.AreEqual(0, AnimeNamingFormatter.Validate(preset).Count, preset.Id);
        }

        AssertInvalid(Default with { StandardEpisodeFormat = "{Series Title} - {Episode Title}" }, "must contain {season} and {episode}");
        AssertInvalid(Default with { StandardEpisodeFormat = "{Series Title} S{season:00}E{episode:00} {Custom Formats}" }, "not a supported token");
        AssertInvalid(Default with { SeriesFolderFormat = "{Series Title} {Episode Title}" }, "not available here");
        AssertInvalid(Default with { SeriesFolderFormat = "Anime/{Series Title}" }, "illegal");
        AssertInvalid(Default with { SeasonFolderFormat = "Season {season" }, "malformed");
        AssertInvalid(Default with { AnimeEpisodeFormat = "{Series Title} {Episode Title}" }, "{absolute}");
        AssertInvalid(Default with { Id = "Bad Id" }, "Profile ID");
        AssertInvalid(Default with { StandardEpisodeFormat = "S{season:ab}E{episode:00}" }, "zero padding");
    }

    private static void AssertInvalid(AnimeNamingProfile profile, string expected)
    {
        var errors = AnimeNamingFormatter.Validate(profile);
        Assert.IsTrue(
            errors.Any(error => error.Contains(expected, StringComparison.Ordinal)),
            $"Expected an error containing '{expected}', got: {string.Join(" | ", errors)}");
    }

    private static string Render(string template, AnimeNamingRequest request) =>
        AnimeNamingFormatter.Render(template, AnimeNamingScope.EpisodeFile, Default, request, null);
}
