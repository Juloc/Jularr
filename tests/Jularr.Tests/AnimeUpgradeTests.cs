using Jularr.Web.Features.Acquisition.Access;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class AnimeUpgradeTests
{
    private const string Sd = "Frieren.S01E01.720p.WEB-DL.AAC.H.264-GRP.mkv";
    private const string Hd = "Frieren.S01E01.1080p.WEB-DL.AAC.H.264-GRP";

    [TestMethod]
    public async Task AnEpisodeBelowTheProfilesCutoffKeepsItsRequestOpenUntilTheBetterReleaseIsImported()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(episodeCount: 1, status: "FINISHED", firstEpisodeFile: Sd);
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Hd, "hd"));

        var request = await environment.SubmitRequestAsync("154587");

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, "The 720p file is below the 1080p cutoff, so the request is not done: it searches for a better release.");
        Assert.AreEqual(Hd, environment.Sabnzbd.Grabs.Single().NzbName);
        var now = DateTime.UtcNow;

        await environment.CompleteLatestDownloadAsync(environment.AddCompletedDownload(Hd, $"{Hd}.mkv"));
        await environment.RunWantedPassAsync(now);

        Assert.IsFalse(File.Exists(Path.Combine(environment.SeriesFolder, "Season 01", Sd)), "The better release replaced the 720p file.");
        Assert.AreEqual(1, await environment.Db.MediaFiles.CountAsync(), "One file: the upgrade replaced the old one.");
        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await environment.GetRequestAsync(request.Id)).Status);
        await environment.RunWantedPassAsync(now.AddHours(3));
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count, "A file that meets the cutoff is final: nothing is searched again.");
    }
}
