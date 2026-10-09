using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

[TestClass]
public sealed class AnimeManualGrabTests
{
    private const string Best = "Frieren.S01E02.1080p.WEB-DL.AAC.H.264-GRP";
    private const string Rejected = "Frieren.S01E02.480p.WEB-DL.AAC.H.264-GRP";

    private static async Task<AnimeAcquisitionEnvironment> CreateAsync(AnimeManagementMode? mode = AnimeManagementMode.JularrManaged)
    {
        var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(mode: mode);
        environment.Prowlarr.Releases.AddRange([AnimeAcquisitionEnvironment.Release(Best, "g1080"), AnimeAcquisitionEnvironment.Release(Rejected, "g480")]);
        return environment;
    }

    private static async Task<string> IdentityOfAsync(AnimeAcquisitionEnvironment environment, string title)
    {
        var search = await environment.WithScopeAsync(services => services.GetRequiredService<AnimeAcquisitionPipeline>().SearchInteractiveAsync(AnimeAcquisitionEnvironment.AnimeKey, 1, 2, ProwlarrAnimeSearchMode.Episode, CancellationToken.None));
        return search!.Candidates.Single(candidate => candidate.Release.Title == title).Release.Identity;
    }

    private static Task<AnimeGrabResult> GrabAsync(AnimeAcquisitionEnvironment environment, string identity) =>
        environment.WithScopeAsync(services => services.GetRequiredService<AnimeManualGrabService>().GrabAsync(AnimeAcquisitionEnvironment.AnimeKey, 1, 2, ProwlarrAnimeSearchMode.Episode, identity, CancellationToken.None));

    [TestMethod]
    public async Task TheOwnersGrabRunsOnTheRequestSoTheWantedPassFollowsAndImportsIt()
    {
        await using var environment = await CreateAsync();

        var result = await GrabAsync(environment, await IdentityOfAsync(environment, Best));

        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual(Best, environment.Sabnzbd.Grabs.Single().NzbName);
        var request = (await environment.AnimeRequestAsync())!;
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, request.StatusMessage);
        Assert.AreEqual(result.OperationId, request.OperationId);
        Assert.AreEqual(new AnimeEpisodeKey(AnimeAcquisitionEnvironment.AnimeKey, 1, 2, 2), AnimeRequestPayload.Of(request).Episodes!.Single());
        Assert.AreEqual(AcquisitionOwner.Jularr, (await environment.Ownership.LoadAsync()).Jobs[request.OperationId!.Value.ToString()].Owner);

        await environment.CompleteLatestDownloadAsync(environment.AddCompletedDownload(Best, $"{Best}.mkv"));
        await environment.RequestPassAsync();
        Assert.IsNotNull(await environment.MediaFileAsync(1, 2), "The Wanted pass imported the owner's download.");
    }

    [TestMethod]
    public async Task AnOwnerGrabIsRefusedWhileADownloadOfTheAnimeRuns()
    {
        await using var environment = await CreateAsync();
        Assert.IsTrue((await GrabAsync(environment, await IdentityOfAsync(environment, Best))).Success);

        var second = await GrabAsync(environment, await IdentityOfAsync(environment, Rejected));

        Assert.IsFalse(second.Success);
        StringAssert.Contains(second.Message, "already running");
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count);
    }

    [TestMethod]
    public async Task TheOwnerMayChooseAReleaseTheProfileRejected()
    {
        await using var environment = await CreateAsync();

        var result = await GrabAsync(environment, await IdentityOfAsync(environment, Rejected));

        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual(Rejected, environment.Sabnzbd.Grabs.Single().NzbName);
    }

    [TestMethod]
    public async Task ASeriesSonarrManagesIsNeverGrabbedByTheOwnerEither()
    {
        await using var environment = await CreateAsync(mode: null);

        var result = await GrabAsync(environment, await IdentityOfAsync(environment, Best));

        Assert.IsFalse(result.Success);
        StringAssert.StartsWith(result.Message, "Ownership:");
        Assert.AreEqual(0, environment.Sabnzbd.Grabs.Count);
    }
}
