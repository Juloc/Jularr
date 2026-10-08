using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Metadata;

namespace Jularr.Tests;

[TestClass]
public sealed class AdminMediaDetailViewTests
{
    private static AdminMediaEpisode Episode(int season, int number, bool monitored = true, bool file = false, params string[] audio) =>
        new(
            season,
            number,
            Guid.NewGuid(),
            $"Episode {number}",
            monitored,
            file ? AdminMediaState.Available : AdminMediaState.Missing,
            false,
            0,
            null,
            audio,
            [],
            file
                ? [new AdminMediaFile(Guid.NewGuid(), "a.mkv", "Media", 10, DateTime.UtcNow, MediaAnalysisStatus.Succeeded, null, null, null, null, [], [])]
                : [],
            []);

    [TestMethod]
    public void SeasonsAreGroupedInOrderWithTheSpecialsLast()
    {
        var groups = AdminMediaDetailView.GroupBySeason(
            [Episode(0, 1), Episode(2, 2), Episode(1, 2), Episode(1, 1)]);

        CollectionAssert.AreEqual(new[] { "s1", "s2", "s0" }, groups.Select(group => group.Key).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2 }, groups[0].Episodes.Select(episode => episode.Number).ToArray());
    }

    [TestMethod]
    public void AniListGroupingFollowsTheExplicitRangesAndKeepsTheRestWithTheMatchedEntry()
    {
        var range = new AnimeEpisodeMetadataMapping(
            Guid.NewGuid(), Guid.NewGuid(), 1, 3, 4, 1, "anilist", "200", "Part Two", 2, DateTimeOffset.UtcNow);

        var groups = AdminMediaDetailView.GroupByAniList(
            [Episode(1, 1), Episode(1, 2), Episode(1, 3), Episode(1, 4)],
            [range],
            "Part One");

        Assert.AreEqual(2, groups.Count);
        Assert.AreEqual("Part One", groups[0].Title);
        Assert.AreEqual(2, groups[0].Episodes.Count);
        Assert.AreEqual("Part Two", groups[1].Title);
        Assert.AreEqual("a-anilist-200", groups[1].Key);
        Assert.IsNull(groups[1].Season);
        Assert.AreEqual(4, groups.Sum(group => group.Episodes.Count), "Grouping never adds or removes an episode.");
    }

    [TestMethod]
    public void MonitoringOfASeasonIsOnOffOrPartial()
    {
        Assert.AreEqual(AdminMediaMonitoring.On, AdminMediaDetailView.MonitoringOf([Episode(1, 1), Episode(1, 2)]));
        Assert.AreEqual(AdminMediaMonitoring.Off, AdminMediaDetailView.MonitoringOf([Episode(1, 1, monitored: false)]));
        Assert.AreEqual(
            AdminMediaMonitoring.Partial,
            AdminMediaDetailView.MonitoringOf([Episode(1, 1), Episode(1, 2, monitored: false)]));
    }

    [TestMethod]
    public void GroupCountsAvailableMissingAndAudioCoverage()
    {
        var group = new AdminMediaGroup(
            "s1",
            1,
            null,
            [Episode(1, 1, file: true, audio: ["ja", "de"]), Episode(1, 2, file: true, audio: ["ja"]), Episode(1, 3)]);

        Assert.AreEqual(2, group.Available);
        Assert.AreEqual(1, group.Missing);
        Assert.AreEqual(20, group.SizeBytes);
        CollectionAssert.AreEqual(new[] { ("ja", 2), ("de", 1) }, group.AudioCoverage.ToArray());
    }

    [TestMethod]
    public void AFileWinsOverTheAttemptAndTheAttemptNamesTheRest()
    {
        Assert.AreEqual(AdminMediaState.Available, AdminMediaDetailView.StateOf(true, AcquisitionAttemptStatus.Failed));
        Assert.AreEqual(AdminMediaState.Searching, AdminMediaDetailView.StateOf(false, AcquisitionAttemptStatus.Pending));
        Assert.AreEqual(AdminMediaState.Downloading, AdminMediaDetailView.StateOf(false, AcquisitionAttemptStatus.Grabbed));
        Assert.AreEqual(AdminMediaState.Failed, AdminMediaDetailView.StateOf(false, AcquisitionAttemptStatus.Failed));
        Assert.AreEqual(AdminMediaState.Missing, AdminMediaDetailView.StateOf(false, null));
        Assert.AreEqual(AdminMediaState.Missing, AdminMediaDetailView.StateOf(false, AcquisitionAttemptStatus.None));
    }

    [TestMethod]
    [DataRow(1920, 1080, null, "1080p")]
    [DataRow(1920, 800, "SDR", "1080p")]
    [DataRow(1280, 720, null, "720p")]
    [DataRow(3840, 2160, "HDR10", "2160p HDR10")]
    [DataRow(720, 480, null, "480p")]
    [DataRow(null, null, null, null)]
    public void QualityIsTheResolutionUsersKnowWithHdrWhenPresent(int? width, int? height, string? range, string? expected) =>
        Assert.AreEqual(expected, AdminMediaDetailView.QualityLabel(width, height, range));

    [TestMethod]
    public void TheBetterResolutionWins()
    {
        Assert.AreEqual("1080p", AdminMediaDetailView.BestQuality("720p", "1080p"));
        Assert.AreEqual("2160p HDR10", AdminMediaDetailView.BestQuality("2160p HDR10", "1080p"));
        Assert.AreEqual("720p", AdminMediaDetailView.BestQuality(null, "720p"));
        Assert.IsNull(AdminMediaDetailView.BestQuality(null, null));
    }

    [TestMethod]
    public void CodecChannelAndPathLabelsAreCompact()
    {
        Assert.AreEqual("H.264", AdminMediaDetailView.CodecLabel("h264"));
        Assert.AreEqual("HEVC", AdminMediaDetailView.CodecLabel("hevc"));
        Assert.AreEqual("AAC", AdminMediaDetailView.CodecLabel("aac"));
        Assert.AreEqual("5.1", AdminMediaDetailView.ChannelLabel(6));
        Assert.AreEqual("", AdminMediaDetailView.ChannelLabel(null));
        Assert.AreEqual("AAC 2.0", AdminMediaDetailView.TrackLabel(new AdminMediaTrack("ja", "aac", 2, true, false)));
        Assert.AreEqual("e1.mkv", AdminMediaDetailView.FileName("/media/a/e1.mkv"));
        Assert.AreEqual("e1.mkv", AdminMediaDetailView.FileName(@"D:\media\e1.mkv"));
        Assert.AreEqual("Frieren/Season 1", AdminMediaDetailView.Folder("/media/", "/media/Frieren/Season 1/e1.mkv"));
        Assert.AreEqual("Frieren", AdminMediaDetailView.Folder(@"D:\anime", @"D:\anime\Frieren\e1.mkv"));
    }

    [TestMethod]
    public void OnlyAniListAsksForTheAniListGrouping()
    {
        Assert.IsTrue(AdminMediaDetailView.IsAniList("anilist"));
        Assert.IsTrue(AdminMediaDetailView.IsAniList(" AniList "));
        Assert.IsFalse(AdminMediaDetailView.IsAniList(null));
        Assert.IsFalse(AdminMediaDetailView.IsAniList("absolute"));
    }
}
