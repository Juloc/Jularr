using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Backend.Tests
{
    // What a person sees of a folder imported by hand: each video with what its name says and the name it would get.
    [TestClass]
    public sealed class ManualImportLogicTests
    {
        private static StorageFile[] Files(params string[] paths) => [.. paths.Select((path, index) => new StorageFile(path, 1_000_000 + index, DateTime.UtcNow))];

        private static TargetUnit[] Season(int season, int count) =>
            [.. Enumerable.Range(1, count).Select(number => new TargetUnit(season * 100 + number, season, number, false, number, null))];

        [TestMethod]
        public void Episodes_TakeTheNumbersTheirNamesSay_AndALeftOutNameStaysBlank()
        {
            var proposals = ManualImportLogic.Propose(MediaType.Series, null, "Example Show", 2020,
                Files("Pack/Example.Show.S01E02.1080p.WEB-DL.H264-GRP.mkv", "Pack/Example.Show.S01E01.720p.WEB-DL.H264-GRP.mkv", "Pack/bonus.mkv", "Pack/Sample/sample.mkv", "Pack/Extras/making-of.mkv"),
                NamingPatternTests.Defaults, Season(1, 6));

            // Samples and extras never show; the rest is in the order of the names.
            CollectionAssert.AreEqual(new[] { "Pack/bonus.mkv", "Pack/Example.Show.S01E01.720p.WEB-DL.H264-GRP.mkv", "Pack/Example.Show.S01E02.1080p.WEB-DL.H264-GRP.mkv" }, proposals.Select(file => file.Path).ToArray());
            Assert.IsNull(proposals[0].Season);
            Assert.IsNull(proposals[0].TargetName);
            Assert.AreEqual((1, 1), (proposals[1].Season, proposals[1].Episode));
            Assert.AreEqual((1, 2), (proposals[2].Season, proposals[2].Episode));
            StringAssert.Contains(proposals[2].TargetName!, "Example Show");
            StringAssert.Contains(proposals[2].TargetName!, "S01E02");
            StringAssert.Contains(proposals[1].TargetName!, "720p");
            StringAssert.Contains(proposals[2].TargetName!, "1080p");
        }

        [TestMethod]
        public void Film_IsNamedFromItsOwnFileWithoutNumbers()
        {
            var proposals = ManualImportLogic.Propose(MediaType.Movie, null, "Example Movie", 2020, Files("Example.Movie.2020.1080p.WEB-DL.H264-GRP.mkv"), NamingPatternTests.Defaults, []);
            Assert.AreEqual(1, proposals.Count);
            Assert.IsNull(proposals[0].Season);
            StringAssert.Contains(proposals[0].TargetName!, "Example Movie (2020)");
            StringAssert.EndsWith(proposals[0].TargetName!, ".mkv");
        }

        [TestMethod]
        public void ChosenFiles_FollowTheNumbersAPersonSets_AndSubtitlesComeAlong()
        {
            var files = Files("Pack/a.mkv", "Pack/a.en.srt", "Pack/b.mkv");

            // The person says what the unnamed files are; a series file without numbers is left out.
            var placements = ImportPlacementLogic.PlanChosen(MediaType.Series, null, "Example Show", 2020, files, [("Pack/a.mkv", 2, 5), ("Pack/b.mkv", null, null)], NamingPatternTests.Defaults, Season(2, 6));
            Assert.AreEqual(2, placements.Count);
            Assert.IsTrue(placements[0].IsMedia);
            Assert.AreEqual((2m, 5m), (placements[0].ParentUnitNumber, placements[0].UnitNumber));
            Assert.AreEqual(".en.srt", placements[1].Extension);
            Assert.AreEqual(placements[0].TargetStem, placements[1].TargetStem);
        }

        [TestMethod]
        public void Download_FinishesWhenEveryVideoIsHandled_OrThePersonSaysSo()
        {
            var files = Files("Pack/a.mkv", "Pack/b.mkv", "Pack/Sample/sample.mkv", "Pack/Extras/making-of.mkv", "Pack/readme.nfo");

            // Samples, extras and other files do not hold a download back.
            Assert.IsFalse(ManualImportLogic.CompletesDownload(files, ["Pack/a.mkv"], false));
            Assert.IsTrue(ManualImportLogic.CompletesDownload(files, ["Pack/a.mkv", "Pack/b.mkv"], false));
            Assert.IsTrue(ManualImportLogic.CompletesDownload(files, [], true));

            // What a move took from the folder is gone from it and cannot hold the download back either.
            Assert.IsTrue(ManualImportLogic.CompletesDownload(Files("Pack/b.mkv"), ["Pack/a.mkv", "Pack/b.mkv"], false));
        }

        [TestMethod]
        public void ImportProposal_KeepsTheFilesHandledSoFar_AndTheRestOfItself()
        {
            var proposal = ImportProposal.Write(new ImportSourceRef(null, "/downloads/pack", ""), [new ImportFileProposal("a.mkv", null, null, null, null)]);
            Assert.AreEqual(0, ImportProposal.ReadHandled(proposal).Count);
            var kept = ImportProposal.WithHandled(proposal, ["b.mkv", "a.mkv", "a.mkv"]);
            CollectionAssert.AreEqual(new[] { "a.mkv", "b.mkv" }, ImportProposal.ReadHandled(kept).ToArray());
            Assert.AreEqual("/downloads/pack", ImportProposal.ReadSource(kept)!.LocalRoot);
        }

        [TestMethod]
        public void ChosenLanguages_AreCheckedAgainstTheCatalog_AndKeptInItsOrder()
        {
            Assert.IsNull(FileLanguageChoice.Read("field", null, []));
            var chosen = FileLanguageChoice.Read("field", [" ENG ", "ger", "eng"], ["fre"])!;
            CollectionAssert.AreEqual(new[] { "eng", "ger" }, chosen.Audio);
            CollectionAssert.AreEqual(new[] { "fre" }, chosen.Subtitles);
            Assert.ThrowsExactly<DataValidationException>(() => FileLanguageChoice.Read("field", ["xxx"], []));
        }

        [TestMethod]
        public void ChosenLanguages_OverrideTheStreams_OneForEveryTrackSeveralBesideTheNamedOnes()
        {
            // One language is the language of every track; none leaves the streams alone.
            CollectionAssert.AreEqual(new[] { "ger", "ger" }, FileLanguageChoice.Apply(["eng", null], ["ger"]));
            CollectionAssert.AreEqual(new[] { "eng", null }, FileLanguageChoice.Apply(["eng", null], []));

            // Several: a track that already names one of them keeps it, the others take the languages no track names.
            CollectionAssert.AreEqual(new[] { "eng", "ger" }, FileLanguageChoice.Apply(["eng", null], ["eng", "ger"]));
            CollectionAssert.AreEqual(new[] { "ger", "eng" }, FileLanguageChoice.Apply(["deu", "und"], ["eng", "ger"]));
            CollectionAssert.AreEqual(new[] { "eng", "ger", "jpn" }, FileLanguageChoice.Apply(["eng", "jpn", "jpn"], ["eng", "ger"]));

            // The tracks of a probe: the audio ones follow the choice, subtitles and video stay unless chosen for.
            const string probe = """[{"StreamIndex":0,"MediaTrackTypeId":1},{"StreamIndex":1,"MediaTrackTypeId":2,"Language":"eng"},{"StreamIndex":2,"MediaTrackTypeId":3,"Language":"eng"}]""";
            var tracks = System.Text.Json.JsonDocument.Parse(FileLanguageChoice.ApplyToTracks(probe, new ChosenLanguages(["ger"], []))).RootElement;
            Assert.AreEqual("ger", tracks[1].GetProperty("Language").GetString());
            Assert.AreEqual("eng", tracks[2].GetProperty("Language").GetString());
            Assert.IsFalse(tracks[0].TryGetProperty("Language", out _));
        }

        [TestMethod]
        public void NameChosen_UsesTheQualityAPersonGave_AndShowsTheLanguagesItsName_States()
        {
            var names = ManualImportLogic.NameChosen(MediaType.Movie, null, "Example Movie", 2020, [("a.1080p.WEB-DL.H264-GRP.mkv", null, null), ("b.mkv", null, null)],
                new Dictionary<string, string> { ["a.1080p.WEB-DL.H264-GRP.mkv"] = "Bluray-720p", ["b.mkv"] = "DVD" }, NamingPatternTests.Defaults, []);
            StringAssert.Contains(names["a.1080p.WEB-DL.H264-GRP.mkv"], "720p");
            StringAssert.Contains(names["b.mkv"], "DVD");

            var proposals = ManualImportLogic.Propose(MediaType.Movie, null, "Example Movie", 2020, Files("Example.Movie.2020.GERMAN.1080p.WEB-DL.H264-GRP.mkv", "plain.mkv"), NamingPatternTests.Defaults, []);
            Assert.AreEqual("DE+EN", proposals.Single(file => file.Path.StartsWith("Example", StringComparison.Ordinal)).Languages);
            Assert.AreEqual("EN", proposals.Single(file => file.Path == "plain.mkv").Languages);
        }
    }
}
