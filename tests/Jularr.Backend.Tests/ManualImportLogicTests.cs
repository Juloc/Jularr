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
            Assert.IsFalse(ManualImportLogic.CompletesDownload(MediaType.Series, files, ["Pack/a.mkv"], false));
            Assert.IsTrue(ManualImportLogic.CompletesDownload(MediaType.Series, files, ["Pack/a.mkv", "Pack/b.mkv"], false));
            Assert.IsTrue(ManualImportLogic.CompletesDownload(MediaType.Series, files, [], true));

            // What a move took from the folder is gone from it and cannot hold the download back either.
            Assert.IsTrue(ManualImportLogic.CompletesDownload(MediaType.Series, Files("Pack/b.mkv"), ["Pack/a.mkv", "Pack/b.mkv"], false));
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

        // A manga's files are proposed as the import recognises them, the rest is left for a person, who sets a volume, a chapter or the whole Work; names follow the import's own rules.
        [TestMethod]
        public void Book_ProposesWhatTheImportRecognises_AndPlacesTheVolumeChapterOrWorkAPersonSets()
        {
            TargetUnit[] volumes = [new(1, null, 1, false), new(2, null, 2, false)];
            TargetUnit[] chapters = [new(12, 2, 12, false)];
            var files = Files("Pack/Example Manga v01.cbz", "Pack/Example Manga Chapter 12.cbz", "Pack/Example Manga Extras German.cbz", "Pack/cover.jpg");
            var proposals = ManualImportLogic.Propose(MediaType.Book, MediaSubtype.Manga, "Example Manga", 2019, files, NamingPatternTests.DefaultSet, volumes, null, chapters);

            // The cover is no document; the volume and the chapter (with its volume) are recognised and named, the extra is not, and its name says German.
            CollectionAssert.AreEqual(new[] { "Pack/Example Manga Chapter 12.cbz", "Pack/Example Manga Extras German.cbz", "Pack/Example Manga v01.cbz" }, proposals.Select(file => file.Path).ToArray());
            var volume = proposals.Single(file => file.Path.EndsWith("v01.cbz", StringComparison.Ordinal));
            Assert.AreEqual((1, (int?)null, "CBZ", "Example Manga/Example Manga v01.cbz"), (volume.Season, volume.Episode, volume.QualityKey, volume.TargetName));
            var chapter = proposals.Single(file => file.Path.EndsWith("Chapter 12.cbz", StringComparison.Ordinal));
            Assert.AreEqual(((int?)2, (int?)12, "Example Manga/Example Manga v02 c012.cbz"), (chapter.Season, chapter.Episode, chapter.TargetName));
            var extra = proposals.Single(file => file.Path.Contains("Extras", StringComparison.Ordinal));
            Assert.AreEqual(((int?)null, (int?)null, (string?)null, "DE"), (extra.Season, extra.Episode, extra.TargetName, extra.Languages));

            // A person sets the extra as volume 2, a chapter without its volume takes the Work's, and nothing at all is the whole Work; a chapter of a book that is no manga is never placed.
            var placed = ImportPlacementLogic.PlanChosen(MediaType.Book, MediaSubtype.Manga, "Example Manga", 2019, files,
                [("Pack/Example Manga Extras German.cbz", 2, null), ("Pack/Example Manga Chapter 12.cbz", null, 12), ("Pack/Example Manga v01.cbz", null, null)], NamingPatternTests.DefaultSet, volumes, chapters: chapters);
            Assert.AreEqual("Volume 2 in : Example Manga/Example Manga v02 | Chapter 12 in 2: Example Manga/Example Manga v02 c012 |   in : Example Manga/Example Manga",
                string.Join(" | ", placed.Select(placement => placement.WorkUnitTypeId + " " + placement.UnitNumber + " in " + placement.ParentUnitNumber + ": " + placement.TargetStem)));
            Assert.HasCount(0, ImportPlacementLogic.PlanChosen(MediaType.Book, null, "Pride and Prejudice", 1813, Files("Book.epub"), [("Book.epub", null, 3)], NamingPatternTests.DefaultSet, []));

            // A plain book is named in its author's folder with its format as its quality; the download waits for every document, not for its cover.
            var book = ImportPlacementLogic.PlanChosen(MediaType.Book, null, "Pride and Prejudice", 1813, Files("In/Book.epub"), [("In/Book.epub", null, null)], NamingPatternTests.DefaultSet, [],
                new Dictionary<string, string> { ["In/Book.epub"] = "EPUB" }, new NamingValues("Pride and Prejudice", 1813, null, null, null, null, null, null, 1) { Author = "Jane Austen" }).Single();
            Assert.AreEqual(("Jane Austen/Pride and Prejudice (1813)/Pride and Prejudice", ".epub", (WorkUnitType?)null), (book.TargetStem, book.Extension, book.WorkUnitTypeId));
            Assert.AreEqual("EPUB", ManualImportLogic.QualityOf(MediaType.Book, null, "In/Book.epub"));
            Assert.IsFalse(ManualImportLogic.CompletesDownload(MediaType.Book, files, ["Pack/Example Manga v01.cbz", "Pack/Example Manga Chapter 12.cbz"], false));
            Assert.IsTrue(ManualImportLogic.CompletesDownload(MediaType.Book, files, ["Pack/Example Manga v01.cbz", "Pack/Example Manga Chapter 12.cbz", "Pack/Example Manga Extras German.cbz"], false));
        }

        [TestMethod]
        public void NameChosen_UsesTheQualityAPersonGave_AndShowsTheLanguagesItsName_States()
        {
            var names = ManualImportLogic.NameChosen(MediaType.Movie, null, "Example Movie", 2020, [("a.1080p.WEB-DL.H264-GRP.mkv", null, null), ("b.mkv", null, null)],
                new Dictionary<string, string> { ["a.1080p.WEB-DL.H264-GRP.mkv"] = "Bluray-720p", ["b.mkv"] = "DVD" }, NamingPatternTests.Defaults, []);
            StringAssert.Contains(names["a.1080p.WEB-DL.H264-GRP.mkv"], "720p");
            StringAssert.Contains(names["b.mkv"], "DVD");

            var proposals = ManualImportLogic.Propose(MediaType.Movie, null, "Example Movie", 2020, Files("Example.Movie.2020.GERMAN.1080p.WEB-DL.H264-GRP.mkv", "Example.Movie.2020.GERMAN.DL.1080p.WEB-DL.H264-GRP.mkv", "plain.mkv"), NamingPatternTests.Defaults, []);
            // The "DL" of WEB-DL is no second audio track; a dual-language name ("German.DL") also carries English.
            Assert.AreEqual("DE", proposals.Single(file => file.Path.StartsWith("Example.Movie.2020.GERMAN.1080p", StringComparison.Ordinal)).Languages);
            Assert.AreEqual("DE+EN", proposals.Single(file => file.Path.StartsWith("Example.Movie.2020.GERMAN.DL", StringComparison.Ordinal)).Languages);
            Assert.AreEqual("EN", proposals.Single(file => file.Path == "plain.mkv").Languages);
        }

        private static readonly ManualImportCandidate[] Library =
        [
            new(1, MediaType.Movie, null, 2020, ["Example Movie"]),
            new(2, MediaType.Series, null, 2019, ["Example Show"]),
            new(3, MediaType.Series, MediaSubtype.Anime, 2023, ["Hello, I am a Witch", "Majo to Watashi"]),
            new(4, MediaType.Book, MediaSubtype.Manga, 2019, ["Example Manga"]),
            new(5, MediaType.Book, MediaSubtype.LightNovel, 2015, ["Sky Novel"]),
            new(6, MediaType.Movie, null, 1984, ["Dune"]),
            new(7, MediaType.Movie, null, 2021, ["Dune"]),
            new(8, MediaType.Movie, null, 2011, ["Green Lantern"])
        ];

        private static (long? WantedItemId, string? Problem) Recognise(string path, string folder = "Incoming")
        {
            var recognised = ManualImportLogic.Recognise(path, ManualImportLogic.Names(path, folder), Library);
            return (recognised.Candidate?.WantedItemId, recognised.ProblemKey);
        }

        // Each kind is found by what its name says: a film, an episode, an anime episode by its other title, a manga volume and a light novel; a name that fits nothing or two titles says so.
        [TestMethod]
        [DataRow("Example.Movie.2020.1080p.WEB-DL.H264-GRP.mkv", 1L, null)]
        [DataRow("Example.Show.S01E02.1080p.WEB-DL.H264-GRP.mkv", 2L, null)]
        [DataRow("[SubGroup] Majo to Watashi - 02 [1080p].mkv", 3L, null)]
        [DataRow("Example Manga v03 (2020) (Digital).cbz", 4L, null)]
        [DataRow("Sky Novel Volume 2.epub", 5L, null)]
        [DataRow("Jane Writer - Sky Novel (2019) (retail) (epub).epub", 5L, null)]
        [DataRow("Dune.2021.2160p.WEB-DL-GRP.mkv", 7L, null)]
        [DataRow("Green.Lantern.2011.Extended.Cut.1080p.BluRay.x264-SECTOR7.mkv", 8L, null)]
        [DataRow("Dune.mkv", null, "work_ambiguous")]
        [DataRow("Holiday.at.the.lake.mkv", null, "work_unknown")]
        [DataRow("Example.Show.Spinoff.S01E01.1080p.mkv", null, "work_unknown")]
        public void Folder_FilesAreRecognisedByTheirNames(string path, long? wantedItemId, string? problem) =>
            Assert.AreEqual((wantedItemId, problem), Recognise(path));

        // A file whose name says nothing takes the title its folders name, the nearest first.
        [TestMethod]
        public void Folder_AnUnnamedFileTakesItsFoldersTitle()
        {
            Assert.AreEqual(((long?)2, (string?)null), Recognise("Example.Show.S01.1080p.WEB-DL-GRP/01.mkv"));
            Assert.AreEqual(((long?)1, (string?)null), Recognise("feature.mkv", "Example.Movie.2020.1080p.BluRay-GRP"));
            Assert.AreEqual(MediaType.Series, ManualImportLogic.KindOf("Show.S01E01.mkv"));
            Assert.AreEqual((2, 3), ManualImportLogic.FolderEpisode("Example Show/Season 2/03.mkv"));
            Assert.AreEqual((1, 12), ManualImportLogic.FolderEpisode("Staffel 1/Episode 12.mkv"));
            Assert.IsNull(ManualImportLogic.FolderEpisode("Example Show/03.mkv"));
            Assert.AreEqual(MediaType.Book, ManualImportLogic.KindOf("Some Book.epub"));
            Assert.AreEqual("JLR", ManualImportLogic.ReleaseGroupOf("Release/Big.Buck.Bunny.2008.1080p.WEB-DL.H264-JLR.webm", MediaType.Movie));
            CollectionAssert.Contains(ManualImportLogic.SearchWords([("Green.Lantern.2011.Extended.Cut.1080p.BluRay.x264-SECTOR7", false)]).ToArray(), "lantern");
        }

        // What a person must check of a recognised file: an episode without numbers, a book file the import would not place, a unit the library holds.
        [TestMethod]
        public void Problems_NameWhatAPersonMustCheck()
        {
            var units = new[] { new TargetUnit(1, 1, 1, true), new TargetUnit(2, 1, 2, false) };
            Assert.AreEqual("episode_unknown", ManualImportLogic.ProblemOf(MediaType.Series, null, new ManualImportProposal("a.mkv", 1, null, null, null, null, null), units, []));
            Assert.AreEqual("already_in_library", ManualImportLogic.ProblemOf(MediaType.Series, null, new ManualImportProposal("a.mkv", 1, 1, 1, null, "x", null), units, []));
            Assert.IsNull(ManualImportLogic.ProblemOf(MediaType.Series, null, new ManualImportProposal("a.mkv", 1, 1, 2, null, "x", null), units, []));
            Assert.AreEqual("volume_unknown", ManualImportLogic.ProblemOf(MediaType.Book, MediaSubtype.Manga, new ManualImportProposal("a.cbz", 1, null, null, null, null, null), [], []));
            Assert.IsNull(ManualImportLogic.ProblemOf(MediaType.Movie, null, new ManualImportProposal("a.mkv", 1, null, null, null, null, null), [], []));
            Assert.AreEqual("already_in_library", ManualImportLogic.ProblemOf(MediaType.Movie, null, new ManualImportProposal("a.mkv", 1, null, null, null, "x", null), [], [], workHasMedia: true));
            Assert.IsNull(ManualImportLogic.Seconds(MediaType.Series, [new("a.mkv", 9, 1, 3, null, "x", null), new("b.mkv", 4, 1, 3, null, "y", null)]).SingleOrDefault(path => path == "a.mkv"));
            Assert.AreEqual("b.mkv", ManualImportLogic.Seconds(MediaType.Series, [new("a.mkv", 9, 1, 3, null, "x", null), new("b.mkv", 4, 1, 3, null, "y", null)]).Single());
        }

        // A job folder still holds media until only notes, pictures, samples and subtitles are left.
        [TestMethod]
        public void Leftovers_HoldNoMedia() =>
            Assert.AreEqual((true, false), (ManualImportLogic.HoldsMedia(Files("a.nfo", "b.mkv")), ManualImportLogic.HoldsMedia(Files("a.nfo", "cover.jpg", "Sample/sample.mkv", "a.en.srt"))));
    }
}
