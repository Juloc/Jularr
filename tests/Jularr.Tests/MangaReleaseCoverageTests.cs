using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.ReadingAcquisition;

namespace Jularr.Tests;

[TestClass]
public sealed class MangaReleaseCoverageTests
{
    private static ReadingWant Want(int[] volumes, int[]? held = null, double[]? chapters = null, double[]? heldChapters = null) =>
        new(volumes, chapters ?? [], held ?? [], heldChapters ?? []);

    private static ReadingAcquisitionTarget Target(ReadingWant want, params string[] languages) =>
        new(MediaAcquisitionKind.Manga, "Frieren", ["Sousou no Frieren"], Want: want, PreferredLanguages: languages.Length == 0 ? null : languages);

    private static RankedReading Judge(string release, ReadingWant want, params string[] languages) =>
        ReadingRank.Judge(Candidate(release), Target(want, languages));

    [TestMethod]
    [DataRow("Frieren Vol 1-5 CBZ", 1, 5)]
    [DataRow("Frieren Volumes 1-5 CBZ", 1, 5)]
    [DataRow("Frieren v01-v05 CBZ", 1, 5)]
    [DataRow("Frieren Vol.03 CBZ", 3, null)]
    public void TheParserReadsVolumeRanges(string release, int first, int? last)
    {
        var parsed = ReadingReleaseParser.Parse(release);

        Assert.AreEqual(first, parsed.VolumeNumber);
        Assert.AreEqual(last, parsed.VolumeEnd);
        Assert.AreEqual(last is not null, parsed.IsCompleteOrBatch);
    }

    [TestMethod]
    public void AVolumeThatIsMissingIsTakenAndOneTheLibraryHoldsIsRefusedWithTheReason()
    {
        var want = Want([3], held: [1, 2]);

        var missing = Judge("Frieren v03 CBZ", want);
        var held = Judge("Frieren v02 CBZ", want);

        Assert.IsTrue(missing.Score > 0);
        Assert.AreEqual(0, held.Score);
        Assert.AreEqual("volume 2 is not wanted", held.RejectedBecause);
    }

    [TestMethod]
    public void ABatchThatCoversMoreOfWhatIsMissingWinsAndOneThatRepeatsHeldVolumesLoses()
    {
        var most = Judge("Frieren Vol 1-6 CBZ", Want([1, 2, 3, 4, 5, 6]));
        var one = Judge("Frieren v01 CBZ", Want([1, 2, 3, 4, 5, 6]));
        var repeats = Judge("Frieren Vol 1-6 CBZ", Want([5, 6], held: [1, 2, 3, 4]));
        var exact = Judge("Frieren Vol 5-6 CBZ", Want([5, 6], held: [1, 2, 3, 4]));

        Assert.IsTrue(most.Score > one.Score, "Covering six missing volumes beats covering one.");
        Assert.IsTrue(exact.Score > repeats.Score, "The same two missing volumes cost less without downloading four held ones again.");
    }

    [TestMethod]
    public void AReleaseThatNamesNoVolumeOrChapterIsAmbiguousAndNeverTakenAutomatically()
    {
        var ranked = ReadingRank.Rank([Candidate("Frieren CBZ")], Target(Want([1, 2, 3])))[0];

        Assert.AreEqual(0, ranked.Score);
    }

    [TestMethod]
    public void ACompleteSeriesCoversEveryMissingVolumeButNeverAHeldOneTwice()
    {
        var all = Judge("Frieren Complete CBZ", Want([1, 2, 3]));
        var none = Judge("Frieren Complete CBZ", Want([], held: [1, 2, 3], chapters: []));

        Assert.IsTrue(all.Score > 0);
        Assert.AreEqual(0, none.Score, "Nothing is missing, so nothing is worth downloading.");
        Assert.AreEqual("nothing is missing", none.RejectedBecause);
    }

    [TestMethod]
    public void ChaptersAreWantedOnlyWhereTheStructureNamesThemAndOneChapterNeverStandsForAVolume()
    {
        var volumesOnly = Want([2]);
        var chapterStructure = Want([], chapters: [5, 6, 7], heldChapters: [1, 2, 3, 4]);

        var chapterForVolumes = Judge("Frieren c005 CBZ", volumesOnly);
        var chaptersWanted = Judge("Frieren c005-007 CBZ", chapterStructure);
        var chaptersHeld = Judge("Frieren c001-004 CBZ", chapterStructure);
        var volumeForChapters = Judge("Frieren v02 CBZ", chapterStructure);

        Assert.AreEqual(0, chapterForVolumes.Score);
        Assert.IsTrue(chaptersWanted.Score > 0);
        Assert.AreEqual(0, chaptersHeld.Score);
        Assert.AreEqual(0, volumeForChapters.Score);
    }

    [TestMethod]
    public void AVolumeNamingItsChaptersIsStillTheVolumeButASingleChapterOfAVolumeIsNot()
    {
        var parsedPack = ReadingReleaseParser.Parse("Frieren Vol.03 Ch.017-024 CBZ");
        var parsedOne = ReadingReleaseParser.Parse("Frieren Vol.03 Ch.017 CBZ");

        Assert.AreEqual(3, parsedPack.VolumeNumber);
        Assert.IsTrue(parsedPack.ChapterEnd > parsedPack.ChapterStart);
        Assert.AreEqual(parsedOne.ChapterStart, parsedOne.ChapterEnd);
    }

    [TestMethod]
    public void AWrongTitleAndALanguageTheProfileDoesNotAllowAreRefusedBeforeAnyCoverageCounts()
    {
        var want = Want([1, 2]);

        var wrongTitle = Judge("Another Manga v01 CBZ", want);
        var german = Judge("Frieren v01 German CBZ", want, "en");
        var english = Judge("Frieren v01 English CBZ", want, "en");

        Assert.AreEqual("title does not match", wrongTitle.RejectedBecause);
        Assert.AreEqual(0, german.Score);
        StringAssert.Contains(german.RejectedBecause, "language");
        Assert.IsTrue(english.Score > 0);
    }

    [TestMethod]
    public void WithoutAStructureTheRequestedVolumeAloneDecidesAsBefore()
    {
        var target = new ReadingAcquisitionTarget(MediaAcquisitionKind.Manga, "Frieren", [], RequestedVolume: 4);

        Assert.IsTrue(ReadingRank.Judge(Candidate("Frieren v04 CBZ"), target).Score > 0);
        Assert.AreEqual(0, ReadingRank.Judge(Candidate("Frieren v05 CBZ"), target).Score);
        Assert.IsTrue(ReadingRank.Judge(Candidate("Frieren CBZ"), new ReadingAcquisitionTarget(MediaAcquisitionKind.Manga, "Frieren", [])).Score > 0);
    }

    private static ReadingWant Upgrade(int volume, string installed, int[]? missing = null, int[]? held = null) =>
        new(missing ?? [], [], held ?? [volume], [])
        {
            UpgradeVolumes = new Dictionary<int, string?> { [volume] = installed },
            Profile = ReadingQualityProfiles.CreateDefaultManga()
        };

    [TestMethod]
    public void AnInstalledZipVolumeIsWantedAgainOnlyByABetterFormat()
    {
        var want = Upgrade(2, "ZIP", held: [1, 2]);

        var cbz = ReadingRank.Judge(Candidate("Frieren v02 CBZ"), Target(want));
        var zip = ReadingRank.Judge(Candidate("Frieren v02 ZIP"), Target(want));
        var other = ReadingRank.Judge(Candidate("Frieren v01 CBZ"), Target(want));

        Assert.IsTrue(cbz.Score > 0);
        Assert.AreEqual(0, zip.Score);
        Assert.AreEqual("not better than the installed ZIP", zip.RejectedBecause, "An equal version is never downloaded again.");
        Assert.AreEqual(0, other.Score, "A held volume without an upgrade is not wanted.");
    }

    [TestMethod]
    public void ASetThatUpgradesOneVolumeAndAddsAMissingOneBeatsTheSingleVolumeAndCostsNothingForAnUpgradedOne()
    {
        var want = Upgrade(1, "ZIP", missing: [2], held: [1]);

        var pack = ReadingRank.Judge(Candidate("Frieren Vol 1-2 CBZ"), Target(want));
        var single = ReadingRank.Judge(Candidate("Frieren v02 CBZ"), Target(want));
        var inferiorPack = ReadingRank.Judge(Candidate("Frieren Vol 1-2 ZIP"), Target(want));

        Assert.IsTrue(pack.Score > single.Score, "Two units improved beat one.");
        Assert.IsTrue(inferiorPack.Score > 0, "The missing volume 2 is still wanted, from a ZIP too.");
        Assert.IsTrue(pack.Score > inferiorPack.Score, "Upgrading volume 1 as well makes the CBZ set better.");
    }

    private static AcquisitionCandidate Candidate(string title) =>
        new(
            title,
            "Test indexer",
            1,
            "usenet",
            100L * 1024 * 1024,
            null,
            null,
            DateTimeOffset.UtcNow,
            0,
            1,
            title,
            null,
            AnimeReleaseParser.Parse(title),
            [],
            new Uri($"https://indexer.invalid/download/{Uri.EscapeDataString(title)}"),
            null);
}
