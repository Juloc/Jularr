using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Operations;

namespace Jularr.Tests;

/// <summary>Reliability is evidence from real download outcomes: bounded, secondary, explainable, and never held against a source for a local problem.</summary>
[TestClass]
public sealed class ReleaseReliabilityTests
{
    private static readonly QualityProfile s_profile = VideoQualityProfiles.CreateDefaultMovie1080p();

    private static async Task RecordAsync(OperationStore operations, string source, string group, OperationStatus outcome, string? externalProvider = "sabnzbd")
    {
        var details = new DownloadOperationDetails(Guid.NewGuid(), MediaAcquisitionKind.Movie, "movies", ReleaseSource: source, ReleaseGroup: group).Serialize();
        var id = await operations.CreateAsync(new OperationDescriptor("video-usenet-download", "Download", "Download Movie", IsDownload: true, ExternalProvider: externalProvider, ExternalId: externalProvider is null ? null : Guid.NewGuid().ToString("N"), Details: details));
        switch (outcome)
        {
            case OperationStatus.Succeeded:
                await operations.MarkSucceededAsync(id, "Downloaded.");
                break;
            case OperationStatus.Failed:
                await operations.MarkFailedAsync(id, "Download failed.");
                break;
            case OperationStatus.Cancelled:
                await operations.MarkCancelledAsync(id, "Cancelled by the owner.");
                break;
        }
    }

    private static SelectionCandidate Candidate(string id, string title, string indexer = "Indexer") =>
        new(id, ReleaseParser.Parse(title), 2_000_000_000, indexer, 0, DateTimeOffset.UtcNow.AddDays(-1), ReleaseIdentityEvidence.Strong("Matches", "ok"), SelectionCoverage.Single);

    [TestMethod]
    public async Task OnlyDownloadOutcomesTheClientReportedCountAndTooFewSamplesSayNothing()
    {
        await using var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
        var operations = new OperationStore(environment.Db);
        for (var index = 0; index < 6; index++)
        {
            await RecordAsync(operations, "Indexer A", "GOOD", OperationStatus.Succeeded);
            await RecordAsync(operations, "Indexer B", "BAD", OperationStatus.Failed);
        }

        // A cancel by the owner and a submission the client never accepted say nothing about the release.
        for (var index = 0; index < 10; index++)
        {
            await RecordAsync(operations, "Indexer C", "NEUTRAL", OperationStatus.Cancelled);
            await RecordAsync(operations, "Indexer C", "NEUTRAL", OperationStatus.Failed, externalProvider: null);
        }

        await RecordAsync(operations, "Indexer D", "NEW", OperationStatus.Failed);
        var lookup = await new ReleaseReliabilityService(environment.Db, TimeProvider.System).LoadAsync(CancellationToken.None);

        Assert.AreEqual(ReleaseReliability.MaximumPoints, lookup.For("Indexer A", "GOOD")!.Points);
        Assert.AreEqual(-ReleaseReliability.MaximumPoints, lookup.For("Indexer B", "BAD")!.Points);
        Assert.IsNull(lookup.For("Indexer C", "NEUTRAL"), "Cancelled and never-submitted downloads are not evidence.");
        Assert.AreEqual(0, lookup.For("Indexer D", "NEW")?.Points ?? 0, "One failure is too few samples to hold against a group.");
        Assert.AreEqual(ReleaseReliability.MaximumPoints, lookup.For("Indexer A", "UNSEEN GROUP")!.Points, "An unknown group falls back to what its indexer has shown.");
        Assert.IsNull(lookup.For("Unknown indexer", "UNSEEN GROUP"));
    }

    [TestMethod]
    public void AGoodTrackRecordBreaksATieButNeverOutranksQualityOrIdentity()
    {
        var good = new ReleaseReliabilityLookup(new Dictionary<string, ReleaseReliability>(), new Dictionary<string, ReleaseReliability> { ["good"] = new(10, 10), ["bad"] = new(10, 0) });
        var web = Candidate("bad-web1080", "Movie.2021.1080p.WEB-DL.x264-BAD");
        var otherWeb = Candidate("good-web1080", "Movie.2021.1080p.WEB-DL.x264-GOOD");
        var bluRay = Candidate("bad-bluray1080", "Movie.2021.1080p.BluRay.x264-BAD");
        var context = new SelectionContext(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var tie = ReleaseSelectionEngine.Select(s_profile, context, [web, otherWeb], good);
        var withQuality = ReleaseSelectionEngine.Select(s_profile, context, [otherWeb, bluRay], good);
        var withoutEvidence = ReleaseSelectionEngine.Select(s_profile, context, [web, otherWeb]);

        Assert.AreEqual("good-web1080", tie.Winner!.Candidate.Id);
        Assert.AreEqual("A better track record of its release group or indexer.", tie.WinnerReason);
        Assert.AreEqual("bad-bluray1080", withQuality.Winner!.Candidate.Id, "A better quality tier is decided before any track record.");
        Assert.AreEqual(ReleaseReliability.MaximumPoints, tie.Ranked.First(item => item.Candidate.Id == "good-web1080").ReliabilityPoints);
        Assert.AreEqual(0, withoutEvidence.Ranked.Max(item => Math.Abs(item.ReliabilityPoints)), "Without evidence nothing is held against a release.");
    }
}
