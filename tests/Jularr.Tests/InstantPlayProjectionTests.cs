using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Operations;

namespace Jularr.Tests;

/// <summary>The consumer acquisition projection: canonical request and download in, consumer vocabulary out, nothing technical.</summary>
[TestClass]
public sealed class InstantPlayProjectionTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private static AcquisitionRequest Request(AcquisitionRequestStatus status, MediaAcquisitionKind kind = MediaAcquisitionKind.Tv, string? message = null) =>
        new(Guid.NewGuid(), kind, "tmdb", "95396", "Severance", null, null, null, "bob", status, message, Guid.NewGuid(), "/Library/Series/1", Now, Now, null, null);

    private static OperationSnapshot Download(OperationStatus status, long? total = null, long? completed = null, int? percent = null) =>
        new(
            Guid.NewGuid(), "video-usenet-download", "External downloads", OperationLane.Normal, status, "bob", "Download TV", "Severance", percent, "SABnzbd: Downloading.", null, true, total, completed, 1_000, null, 1, true,
            "sabnzbd", "SABnzbd_nzo_secret", Now, Now, null, Now);

    private static VideoRequestPayload Payload(DateTime? nextSearch = null, int searches = 0) =>
        new(Guid.NewGuid(), "Severance", 2022) { NextSearchUtc = nextSearch, Searches = searches };

    private static ConsumerAcquisitionView Project(
        AcquisitionRequest request, VideoRequestPayload? payload = null, OperationSnapshot? download = null, bool local = false, bool episode = true, bool playback = true, Guid? target = null, bool future = false) =>
        ConsumerAcquisitionProjector.Project(request, payload, future, download, local, target ?? (episode ? Guid.NewGuid() : null), playback, Now);

    [TestMethod]
    public void EveryCanonicalStatusProjectsToTheConsumerVocabulary()
    {
        Assert.AreEqual(ConsumerAcquisitionState.WaitingForApproval, Project(Request(AcquisitionRequestStatus.Pending)).State);
        Assert.AreEqual(ConsumerAcquisitionState.LookingForMedia, Project(Request(AcquisitionRequestStatus.Approved)).State);
        Assert.AreEqual(ConsumerAcquisitionState.LookingForMedia, Project(Request(AcquisitionRequestStatus.Searching)).State);
        Assert.AreEqual(ConsumerAcquisitionState.GettingMedia, Project(Request(AcquisitionRequestStatus.Downloading), download: Download(OperationStatus.Running)).State);
        Assert.AreEqual(ConsumerAcquisitionState.Preparing, Project(Request(AcquisitionRequestStatus.Importing)).State);
        Assert.AreEqual(ConsumerAcquisitionState.NeedsAttention, Project(Request(AcquisitionRequestStatus.Failed)).State);
        Assert.AreEqual(ConsumerAcquisitionState.Rejected, Project(Request(AcquisitionRequestStatus.Rejected)).State);
        Assert.AreEqual(ConsumerAcquisitionState.NotAvailable, Project(Request(AcquisitionRequestStatus.Completed)).State, "A completed request whose target is not local has nothing to offer and keeps no search going.");
    }

    [TestMethod]
    public void AnApprovedRequestSaysWhetherItIsLookingWaitingForAReleaseOrMonitoring()
    {
        var approved = Request(AcquisitionRequestStatus.Approved);

        Assert.AreEqual(ConsumerAcquisitionState.LookingForMedia, Project(approved, Payload(nextSearch: Now.AddHours(-1))).State, "A due search is looking.");
        Assert.AreEqual(ConsumerAcquisitionState.NotAvailableYet, Project(approved, Payload(Now.AddHours(6), searches: 2)).State, "No acceptable release yet; the request keeps looking.");
        var monitoring = Project(approved, Payload(Now.AddHours(24), searches: 0), future: true);
        Assert.AreEqual((ConsumerAcquisitionState.MonitoringFutureReleases, true), (monitoring.State, monitoring.IsMonitoring));
    }

    [TestMethod]
    public void ATransferShowsProgressOnlyWhenTheDownloadReportsATrustworthyTotal()
    {
        var downloading = Request(AcquisitionRequestStatus.Downloading);

        Assert.AreEqual(42, Project(downloading, download: Download(OperationStatus.Running, 1_000, 420, 90)).ProgressPercent, "The percentage comes from the transferred and total size, not from a reported number.");
        Assert.AreEqual(99, Project(downloading, download: Download(OperationStatus.Running, 1_000, 1_000, 100)).ProgressPercent, "Only finished work is 100.");
        foreach (var unreliable in new[]
                 {
                     Download(OperationStatus.Running, percent: 55),
                     Download(OperationStatus.Running, total: 0, completed: 0, percent: 10),
                     Download(OperationStatus.Running, total: 1_000, completed: 2_000),
                     Download(OperationStatus.Running, total: 1_000, completed: null),
                     Download(OperationStatus.Queued, total: 1_000, completed: 0)
                 })
        {
            var view = Project(downloading, download: unreliable);
            Assert.AreEqual(ConsumerAcquisitionState.GettingMedia, view.State);
            Assert.IsNull(view.ProgressPercent, "Never invent progress.");
        }

        Assert.IsNull(Project(downloading).ProgressPercent);
        Assert.IsNull(Project(Request(AcquisitionRequestStatus.Searching), download: Download(OperationStatus.Running, 1_000, 500)).ProgressPercent, "Progress belongs to the transfer state only.");
    }

    [TestMethod]
    public void AFinishedOrFailedTransferIsPreparingOrLookingAgain()
    {
        var downloading = Request(AcquisitionRequestStatus.Downloading);

        Assert.AreEqual(ConsumerAcquisitionState.Preparing, Project(downloading, download: Download(OperationStatus.Succeeded)).State);
        foreach (var ended in new[] { OperationStatus.Failed, OperationStatus.Interrupted, OperationStatus.Cancelled })
        {
            Assert.AreEqual(ConsumerAcquisitionState.LookingForMedia, Project(downloading, download: Download(ended)).State, ended.ToString());
        }
    }

    [TestMethod]
    public void ALocalTargetIsReadyToWatchOrAvailableAndMonitoringCoexists()
    {
        var open = Request(AcquisitionRequestStatus.Approved);
        var monitored = Payload(Now.AddHours(24));

        var ready = Project(open, monitored, local: true, future: true);
        Assert.AreEqual((ConsumerAcquisitionState.ReadyToWatch, true, null), (ready.State, ready.IsMonitoring, ready.ProgressPercent));
        Assert.AreEqual(ConsumerAcquisitionState.Available, Project(open, monitored, local: true, playback: false, future: true).State, "A manager-only instance never says Ready to watch.");
        Assert.AreEqual(ConsumerAcquisitionState.ReadyToWatch, Project(Request(AcquisitionRequestStatus.Completed), local: true).State);
        Assert.IsFalse(Project(Request(AcquisitionRequestStatus.Completed), monitored, local: true).IsMonitoring, "A finished request monitors nothing.");
    }

    [TestMethod]
    public void TheMediaUnitNamesWhatIsBeingGot()
    {
        Assert.AreEqual(ConsumerMediaUnit.Movie, Project(Request(AcquisitionRequestStatus.Searching, MediaAcquisitionKind.Movie), episode: false).MediaUnit);
        Assert.AreEqual(ConsumerMediaUnit.Episode, Project(Request(AcquisitionRequestStatus.Searching), episode: true).MediaUnit);
        Assert.AreEqual(ConsumerMediaUnit.Media, Project(Request(AcquisitionRequestStatus.Searching), episode: false).MediaUnit);

        var catalog = new[] { ConsumerMediaUnit.Episode, ConsumerMediaUnit.Movie, ConsumerMediaUnit.Media }
            .Select(unit => UiTranslationResources.Get(ConsumerAcquisitionLabels.StateKey(new ConsumerAcquisitionView(ConsumerAcquisitionState.GettingMedia, unit, null, false))).DefaultText)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "Getting episode", "Getting movie", "Getting media" }, catalog);
    }

    [TestMethod]
    public void ConsumerWordsNeverSayDownloadingImportingOrSearchingWhileAdminKeepsItsTerms()
    {
        var consumer = Enum.GetValues<ConsumerAcquisitionState>()
            .SelectMany(state => Enum.GetValues<ConsumerMediaUnit>().Select(unit => ConsumerAcquisitionLabels.StateKey(new ConsumerAcquisitionView(state, unit, null, false))))
            .Concat(Enum.GetValues<AcquisitionRequestStatus>().Select(ConsumerAcquisitionLabels.StatusKey))
            .Concat(["acquisition.state.notAvailableYetHint", "acquisition.playback.starting", "acquisition.playback.limitReached"])
            .Distinct()
            .ToArray();

        foreach (var key in consumer)
        {
            Assert.IsTrue(UiTranslationResources.TryGet(key, out var entry), $"{key} is missing from the catalog.");
            foreach (var technical in new[] { "download", "import", "search", "indexer", "usenet", "nzb" })
            {
                Assert.IsFalse(entry.DefaultText.Contains(technical, StringComparison.OrdinalIgnoreCase), $"{key} says '{technical}': {entry.DefaultText}");
            }
        }

        UiTranslationResources.TryGet("requests.status.downloading", out var admin);
        Assert.AreEqual("Downloading", admin.DefaultText, "Downloading and Importing stay Admin terms.");
        UiTranslationResources.TryGet("requests.status.importing", out var adminImporting);
        Assert.AreEqual("Importing", adminImporting.DefaultText);
    }

    [TestMethod]
    public void TheClientDtoNeverCarriesProvidersReleasesScoresClientsJobsPathsOrErrors()
    {
        var request = Request(AcquisitionRequestStatus.Downloading, message: "Dune.2021.2160p.UHD.BluRay.x265-SECRETGROUP via NZBgeek, score 87, importer MovieImporter");
        var download = Download(OperationStatus.Running, 2_000, 500) with
        {
            Error = @"ECONNRESET reading C:\downloads\Dune\file.nzb",
            Details = "{\"entry\":\"client-1\",\"path\":\"/downloads/movies/Dune\",\"extractor\":\"7z\"}"
        };
        var view = Project(request, Payload(), download);
        var status = new ClientRequestStatusResponse(request.Id, new ClientVideoTarget(Guid.NewGuid(), Guid.NewGuid()), view);
        var intent = new ClientPlaybackIntentResponse(Jularr.Web.Features.InstantPlay.PlaybackIntentOutcome.Acquiring, new ClientVideoTarget(Guid.NewGuid(), null), request.Id, view);

        foreach (var json in new[] { JsonSerializer.Serialize(status, JsonSerializerOptions.Web), JsonSerializer.Serialize(intent, JsonSerializerOptions.Web) })
        {
            foreach (var forbidden in new[]
                     {
                         "tmdb", "95396", "NZBgeek", "SECRETGROUP", "Dune", "score", "importer", "MovieImporter", "SABnzbd", "sabnzbd", "nzo", "client-1", "operation", "ECONNRESET",
                         @"C:\\", "/downloads", "extractor", "7z", "video-usenet-download", "indexer", "message", "error", "provider", "Severance"
                     })
            {
                Assert.IsFalse(json.Contains(forbidden, StringComparison.Ordinal), $"'{forbidden}' leaked into {json}");
            }

            using var document = JsonDocument.Parse(json);
            var acquisition = document.RootElement.GetProperty("acquisition");
            CollectionAssert.AreEquivalent(new[] { "state", "mediaUnit", "progressPercent", "isMonitoring" }, acquisition.EnumerateObject().Select(x => x.Name).ToArray());
            Assert.AreEqual("getting_media", acquisition.GetProperty("state").GetString());
            Assert.AreEqual("episode", acquisition.GetProperty("mediaUnit").GetString());
            Assert.AreEqual(25, acquisition.GetProperty("progressPercent").GetInt32());
        }
    }

    [TestMethod]
    public void AnotherEpisodesTransferIsNeverShownForTheEpisodeAProfileWaitsFor()
    {
        var active = Guid.NewGuid();
        var waiting = Guid.NewGuid();
        var payload = Payload() with { ActiveWorkEpisodeId = active };
        var transfer = Download(OperationStatus.Running, 1_000, 400);

        var other = Project(Request(AcquisitionRequestStatus.Downloading), payload, transfer, target: waiting);
        var same = Project(Request(AcquisitionRequestStatus.Downloading), payload, transfer, target: active);
        var preparing = Project(Request(AcquisitionRequestStatus.Importing), payload, target: waiting);

        Assert.AreEqual((ConsumerAcquisitionState.LookingForMedia, null), (other.State, other.ProgressPercent), "Episode 1 downloading is not episode 2 getting.");
        Assert.AreEqual((ConsumerAcquisitionState.GettingMedia, 40), (same.State, same.ProgressPercent));
        Assert.AreEqual(ConsumerAcquisitionState.LookingForMedia, preparing.State);
        Assert.AreEqual(ConsumerAcquisitionState.GettingMedia, Project(Request(AcquisitionRequestStatus.Downloading), payload, transfer, episode: false).State, "A request-level view has no waiting episode to mismatch.");
    }
}
