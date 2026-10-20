using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Backend.Tests
{
    // A finished film or episode download is clear, a doubt for a person or no use.
    [TestClass]
    public sealed class ImportJudgementTests
    {
        private static StorageFile[] Files(params string[] paths) => [.. paths.Select(path => new StorageFile(path, 1_000_000, DateTime.UtcNow))];

        private static TargetUnit[] Season(int season, int count, int firstAbsolute = 1) =>
            [.. Enumerable.Range(1, count).Select(number => new TargetUnit(season * 100 + number, season, number, false, firstAbsolute + number - 1, null))];

        private static ImportVerdict Series(string[] paths, int? season = 1, int? start = 4, int? end = 4, TargetUnit[]? units = null, QualityProfile? profile = null, string release = "Example.Show.S01E04.1080p.WEB-DL.H264-GRP") =>
            ImportJudgement.Judge(MediaType.Series, null, "Example Show", 2020, release, season, start, end, Files(paths), units ?? Season(1, 6), profile);

        private static ImportVerdict Movie(params string[] paths) =>
            ImportJudgement.Judge(MediaType.Movie, null, "Example Movie", 2020, "Example.Movie.2020.1080p.WEB-DL.H264-GRP", null, null, null, Files(paths), [], null);

        [TestMethod]
        public void Series_TheWantedEpisode_IsClear_AndAPackBringsEveryEpisodeAlong()
        {
            Assert.AreEqual(ImportVerdictKind.Clear, Series(["Example.Show.S01E04.1080p.WEB-DL.H264-GRP.mkv"]).Kind);
            Assert.AreEqual(ImportVerdictKind.Clear, Series(["Pack/Example.Show.S01E01.1080p.WEB-DL.H264-GRP.mkv", "Pack/Example.Show.S01E02.1080p.WEB-DL.H264-GRP.mkv", "Pack/Example.Show.S01E03.1080p.WEB-DL.H264-GRP.mkv"],
                start: null, end: null).Kind);

            // A pack that holds the wanted episode among others, and a name without the series but with a season and episode, are no doubt.
            Assert.AreEqual(ImportVerdictKind.Clear, Series(["Pack/Example.Show.S01E03.1080p.WEB-DL.H264-GRP.mkv", "Pack/Example.Show.S01E04.1080p.WEB-DL.H264-GRP.mkv"]).Kind);
            Assert.AreEqual(ImportVerdictKind.Clear, Series(["S01E04.mkv"]).Kind);

            // An absolute release counts as the episode it maps to.
            Assert.AreEqual(ImportVerdictKind.Clear, ImportJudgement.Judge(MediaType.Series, MediaSubtype.Anime, "Example Show", 2020, "[Grp] Example Show - 04 [1080p]", 1, 4, 4,
                Files("[Grp] Example Show - 04 [1080p].mkv"), Season(1, 6), null).Kind);
        }

        [TestMethod]
        public void Series_AnotherSeriesSamplesAndExtras_AreNoUse()
        {
            var wrong = Series(["Other.Show.S01E04.1080p.WEB-DL.H264-GRP.mkv"]);
            Assert.AreEqual((ImportVerdictKind.Discard, ImportJudgement.WrongSeries), (wrong.Kind, wrong.ReasonKey));
            Assert.AreEqual(ImportJudgement.NoMedia, Series(["readme.txt.nfo"]).ReasonKey);
            Assert.AreEqual(ImportJudgement.OnlyExtras, Series(["Sample/example.show.s01e04.sample.mkv"]).ReasonKey);
            Assert.AreEqual(ImportJudgement.OnlyExtras, Series(["Extras/Behind The Scenes.mkv", "Featurettes/Making Of.mkv"]).ReasonKey);
            Assert.AreEqual(ImportJudgement.OnlyExtras, Series(["Example.Show.S01E04-trailer.mkv"]).ReasonKey);

            // The wrong files among right ones are left behind.
            Assert.AreEqual(ImportVerdictKind.Clear, Series(["Example.Show.S01E04.1080p.WEB-DL.H264-GRP.mkv", "Other.Show.S01E01.1080p.WEB-DL.H264-GRP.mkv", "Sample/sample.mkv"]).Kind);
        }

        [TestMethod]
        public void Series_AnotherEpisodeThanWanted_WaitsWithWhatTheNamesSay()
        {
            var verdict = Series(["Example.Show.S01E05.1080p.WEB-DL.H264-GRP.mkv"]);
            Assert.AreEqual((ImportVerdictKind.Doubt, ImportJudgement.WrongEpisode), (verdict.Kind, verdict.ReasonKey));
            Assert.AreEqual(new ImportFileProposal("Example.Show.S01E05.1080p.WEB-DL.H264-GRP.mkv", 1, 5, 1, 4), verdict.Proposal!.Single());
            StringAssert.Contains(verdict.ProposalJson!, "\"Episode\":5");

            // Another season is another episode as well.
            Assert.AreEqual(ImportJudgement.WrongEpisode, Series(["Example.Show.S02E04.1080p.WEB-DL.H264-GRP.mkv"], units: [.. Season(1, 6), .. Season(2, 6)]).ReasonKey);

            // Several files, none of them wanted: every one is proposed as its name says, and none as the wanted one.
            var many = Series(["A/Example.Show.S01E05.mkv", "A/Example.Show.S01E06.mkv"]);
            Assert.AreEqual(2, many.Proposal!.Count);
            Assert.IsTrue(many.Proposal.All(entry => entry.WantedEpisode is null));
        }

        [TestMethod]
        public void Series_NamesThatSayNothing_WaitAndFollowTheWantedEpisodesInOrder()
        {
            var single = Series(["abc123.mkv"]);
            Assert.AreEqual((ImportVerdictKind.Doubt, ImportJudgement.UnreadableNames), (single.Kind, single.ReasonKey));
            Assert.AreEqual(new ImportFileProposal("abc123.mkv", 1, 4, 1, 4), single.Proposal!.Single());

            // As many files as wanted episodes follow each other by their names; otherwise nothing is proposed.
            var two = Series(["x/part 2.mkv", "x/part 10.mkv"], start: 2, end: 3);
            CollectionAssert.AreEqual(new[] { ("x/part 2.mkv", 2), ("x/part 10.mkv", 3) }, two.Proposal!.Select(entry => (entry.Path, entry.Episode!.Value)).ToArray());
            Assert.IsTrue(Series(["a.mkv", "b.mkv", "c.mkv"], start: 2, end: 3).Proposal!.All(entry => entry.Episode is null));
        }

        [TestMethod]
        public void Series_AQualityTheProfileDoesNotAllow_Waits()
        {
            var profile = new QualityProfile("1", "Test", ["WEBDL-1080p"], ["WEBDL-1080p"], true, "WEBDL-1080p", null, null);
            Assert.AreEqual(ImportVerdictKind.Clear, Series(["Example.Show.S01E04.1080p.WEB-DL.H264-GRP.mkv"], profile: profile).Kind);
            var low = Series(["Example.Show.S01E04.720p.WEB-DL.H264-GRP.mkv"], profile: profile);
            Assert.AreEqual((ImportVerdictKind.Doubt, ImportJudgement.QualityNotAllowed), (low.Kind, low.ReasonKey));
            Assert.AreEqual(ImportVerdictKind.Clear, Series(["Example.Show.S01E04.720p.WEB-DL.H264-GRP.mkv"]).Kind, "Without a profile nothing is against it.");
        }

        [TestMethod]
        public void Movie_TheFilmIsClear_AnotherFilmIsNoUse_AUnclearTitleWaits()
        {
            Assert.AreEqual(ImportVerdictKind.Clear, Movie("Example.Movie.2020.1080p.WEB-DL.H264-GRP.mkv").Kind);
            Assert.AreEqual(ImportVerdictKind.Clear, Movie("movie.mkv").Kind, "A bare name says nothing against the film.");
            Assert.AreEqual(ImportVerdictKind.Clear, Movie("Example.Movie.2020.1080p.WEB-DL.H264-GRP.mkv", "Sample/sample.mkv", "Extras/Making Of.mkv").Kind);

            var other = Movie("Another.Film.2020.1080p.WEB-DL.H264-GRP.mkv");
            Assert.AreEqual((ImportVerdictKind.Discard, ImportJudgement.WrongTitle), (other.Kind, other.ReasonKey));

            // Another year, or a sequel that shares the words, is not for the machine to decide.
            var year = Movie("Example.Movie.2005.1080p.WEB-DL.H264-GRP.mkv");
            Assert.AreEqual((ImportVerdictKind.Doubt, ImportJudgement.AmbiguousTitle), (year.Kind, year.ReasonKey));
            Assert.AreEqual("Example.Movie.2005.1080p.WEB-DL.H264-GRP.mkv", year.Proposal!.Single().Path);
            Assert.AreEqual(ImportJudgement.AmbiguousTitle, Movie("Example.Movie.Returns.2020.1080p.WEB-DL.H264-GRP.mkv").ReasonKey);
            Assert.AreEqual(ImportJudgement.OnlyExtras, Movie("Sample/sample.mkv").ReasonKey);
        }

        [TestMethod]
        public void OtherMediaTypes_KeepTheirOwnRules()
        {
            Assert.AreEqual(ImportVerdictKind.Clear, ImportJudgement.Judge(MediaType.Music, null, "Album", 2020, "Album", null, null, null, Files("a.flac"), [], null).Kind);
            Assert.AreEqual(ImportVerdictKind.Clear, ImportJudgement.Judge(MediaType.Book, null, "Book", 2020, "Book", null, null, null, Files("b.epub"), [], null).Kind);
        }
    }
}
