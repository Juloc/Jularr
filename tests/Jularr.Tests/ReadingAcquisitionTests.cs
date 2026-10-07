using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.ReadingAcquisition;

namespace Jularr.Tests;

[TestClass]
public sealed class ReadingAcquisitionTests
{
    [TestMethod]
    public void ParserUnderstandsMangaVolumeChapterRangeFormatAndLanguage()
    {
        var parsed = ReadingReleaseParser.Parse(
            "Frieren Vol. 12 Ch. 111-118 German Digital.cbz");

        Assert.AreEqual(ReadingReleaseFormat.Cbz, parsed.Format);
        Assert.AreEqual(12, parsed.VolumeNumber);
        Assert.AreEqual(111d, parsed.ChapterStart);
        Assert.AreEqual(118d, parsed.ChapterEnd);
        Assert.AreEqual("de", parsed.Language);
    }

    [TestMethod]
    public void ParserUnderstandsLightNovelEpubAndBatch()
    {
        var parsed = ReadingReleaseParser.Parse(
            "Mushoku Tensei Vol 01-26 Complete English EPUB");

        Assert.AreEqual(ReadingReleaseFormat.Epub, parsed.Format);
        Assert.AreEqual(1, parsed.VolumeNumber);
        Assert.AreEqual("en", parsed.Language);
        Assert.IsTrue(parsed.IsCompleteOrBatch);
    }

    [TestMethod]
    public void MangaRejectsWrongTitleAndUnsupportedFormats()
    {
        var target = new ReadingAcquisitionTarget(
            MediaAcquisitionKind.Manga,
            "Frieren Beyond Journey's End",
            ["Sousou no Frieren"]);

        var wrong = ReadingReleaseSelector.Judge(
            Candidate("Another Manga Vol 1.cbz"),
            target);
        var epub = ReadingReleaseSelector.Judge(
            Candidate("Frieren Beyond Journey's End Vol 1.epub"),
            target);

        Assert.AreEqual(0, wrong.Score);
        Assert.AreEqual("title does not match", wrong.RejectedBecause);
        Assert.AreEqual(0, epub.Score);
        Assert.IsTrue(epub.RejectedBecause?.Contains("format", StringComparison.OrdinalIgnoreCase) == true);
    }

    [TestMethod]
    public void MangaAcceptsAliasCbzAndChapterRange()
    {
        var target = new ReadingAcquisitionTarget(
            MediaAcquisitionKind.Manga,
            "Frieren Beyond Journey's End",
            ["Sousou no Frieren"],
            RequestedChapterStart: 117);

        var ranked = ReadingReleaseSelector.Judge(
            Candidate("Sousou no Frieren Ch 111-118 [Digital] CBZ"),
            target);

        Assert.IsTrue(ranked.Score > 0);
        Assert.AreEqual(111d, ranked.Parsed.ChapterStart);
        Assert.AreEqual(118d, ranked.Parsed.ChapterEnd);
    }

    [TestMethod]
    public void RequestedMangaChapterRejectsRangeThatDoesNotContainIt()
    {
        var target = new ReadingAcquisitionTarget(
            MediaAcquisitionKind.Manga,
            "Frieren",
            [],
            RequestedChapterStart: 50);

        var ranked = ReadingReleaseSelector.Judge(
            Candidate("Frieren Ch 20-30 CBZ"),
            target);

        Assert.AreEqual(0, ranked.Score);
        Assert.IsTrue(ranked.RejectedBecause?.Contains("chapter", StringComparison.OrdinalIgnoreCase) == true);
    }

    [TestMethod]
    public void LightNovelPrefersEpubAndRejectsPdf()
    {
        var target = new ReadingAcquisitionTarget(
            MediaAcquisitionKind.LightNovel,
            "Ascendance of a Bookworm",
            [],
            "Miya Kazuki");

        var epub = ReadingReleaseSelector.Judge(
            Candidate("Ascendance of a Bookworm Volume 1 English EPUB"),
            target);
        var unknown = ReadingReleaseSelector.Judge(
            Candidate("Ascendance of a Bookworm Volume 1 English"),
            target);
        var pdf = ReadingReleaseSelector.Judge(
            Candidate("Ascendance of a Bookworm Volume 1 English PDF"),
            target);

        Assert.IsTrue(epub.Score > unknown.Score);
        Assert.AreEqual(0, pdf.Score);
    }

    [TestMethod]
    public void RequestedLightNovelVolumeRejectsDifferentVolume()
    {
        var target = new ReadingAcquisitionTarget(
            MediaAcquisitionKind.LightNovel,
            "Re Zero",
            [],
            RequestedVolume: 8);

        var wrong = ReadingReleaseSelector.Judge(
            Candidate("Re Zero Vol 7 EPUB"),
            target);
        var right = ReadingReleaseSelector.Judge(
            Candidate("Re Zero Vol 8 EPUB"),
            target);

        Assert.AreEqual(0, wrong.Score);
        Assert.IsTrue(right.Score > 0);
    }

    [TestMethod]
    public void ExplicitLanguagePreferenceRejectsKnownOtherLanguage()
    {
        var target = new ReadingAcquisitionTarget(
            MediaAcquisitionKind.LightNovel,
            "Spice and Wolf",
            [],
            PreferredLanguages: ["de", "en"]);

        var japanese = ReadingReleaseSelector.Judge(
            Candidate("Spice and Wolf Vol 1 Japanese EPUB"),
            target);
        var german = ReadingReleaseSelector.Judge(
            Candidate("Spice and Wolf Vol 1 German EPUB"),
            target);

        Assert.AreEqual(0, japanese.Score);
        Assert.IsTrue(german.Score > 0);
    }

    [TestMethod]
    public void TorrentReleaseIsNeverAccepted()
    {
        var target = new ReadingAcquisitionTarget(
            MediaAcquisitionKind.Manga,
            "Berserk",
            []);

        var release = Candidate("Berserk Vol 1 CBZ") with
        {
            Protocol = "torrent"
        };

        var ranked = ReadingReleaseSelector.Judge(
            release,
            target);

        Assert.AreEqual(0, ranked.Score);
        Assert.AreEqual("not a Usenet release", ranked.RejectedBecause);
    }

    [TestMethod]
    public void QueriesUseAliasesAuthorAndRequestedVolume()
    {
        var target = new ReadingAcquisitionTarget(
            MediaAcquisitionKind.LightNovel,
            "Mushoku Tensei",
            ["Jobless Reincarnation", "無職転生"],
            "Rifujin na Magonote",
            RequestedVolume: 12);

        var queries = SearchPlannerTests.ReadingQueries(target);

        CollectionAssert.Contains(
            queries.ToList(),
            "Rifujin na Magonote Mushoku Tensei");
        CollectionAssert.Contains(
            queries.ToList(),
            "Mushoku Tensei vol 12");
        CollectionAssert.Contains(
            queries.ToList(),
            "Jobless Reincarnation volume 12");
        CollectionAssert.Contains(
            queries.ToList(),
            "無職転生");
    }

    [TestMethod]
    public void OnlyAcceptedReleasesBecomeCandidatesInRankOrder()
    {
        var first = Candidate("Frieren Vol 1 CBZ");
        var second = Candidate("Frieren Vol 1 Digital CBZ");
        var rejected = Candidate("Frieren Vol 2 CBZ");
        var search = new ReadingUsenetSearchResult(
            [],
            [
                new RankedReadingRelease(
                    first,
                    ReadingReleaseParser.Parse(first.Title),
                    150,
                    null),
                new RankedReadingRelease(
                    second,
                    ReadingReleaseParser.Parse(second.Title),
                    140,
                    null),
                new RankedReadingRelease(
                    rejected,
                    ReadingReleaseParser.Parse(rejected.Title),
                    0,
                    "wrong volume")
            ],
            [],
            UsedCategoryFallback: false);

        var candidates = ReadingAcquisitionEngine.Candidates(search);

        CollectionAssert.AreEqual(
            new[] { first.Identity, second.Identity },
            candidates.Select(candidate => candidate.Identity).ToArray(),
            "Rejected releases are never candidates; the shared tracker skips tried ones.");
    }

    [TestMethod]
    public void RetryBackoffMatchesBookStyleCadence()
    {
        Assert.AreEqual(TimeSpan.FromHours(6), ReleaseRequestTracker.SearchBackoff(1));
        Assert.AreEqual(TimeSpan.FromHours(12), ReleaseRequestTracker.SearchBackoff(2));
        Assert.AreEqual(TimeSpan.FromHours(24), ReleaseRequestTracker.SearchBackoff(3));
        Assert.AreEqual(TimeSpan.FromHours(24), ReleaseRequestTracker.SearchBackoff(12));
    }

    private static ProwlarrReleaseCandidate Candidate(
        string title,
        string protocol = "usenet") =>
        new(
            title,
            "Test indexer",
            1,
            protocol,
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
            new Uri(
                $"https://indexer.invalid/download/{Uri.EscapeDataString(title)}"),
            null);
}
