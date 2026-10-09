using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.ManualSearch;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.ReadingAcquisition;
using Microsoft.Extensions.DependencyInjection;
using BookIndexer = Jularr.Tests.BooksLifecycleTests.BookIndexer;
using Env = Jularr.Tests.BookPdfAcquisitionTests.BookAcquisitionEnvironment;
using Lifecycle = Jularr.Tests.MangaLifecycleTests;

namespace Jularr.Tests;

/// <summary>Manual Search of a Manga request ranks the releases exactly as automatic acquisition and re-validates a grab on the server.</summary>
[TestClass]
public sealed class MangaManualSearchTests
{
    private static readonly string[] Releases =
    [
        "Frieren v01 CBZ",
        "Frieren Vol 1-3 CBZ",
        "Frieren v04 CBZ",
        "Another Manga v02 CBZ",
        "Frieren v02 German CBZ"
    ];

    // The indexer has nothing when the request is made, so the request waits for a release and Manual Search has something to work on.
    private static async Task<(Env Environment, AcquisitionRequest Request, BookIndexer Indexer)> WaitingRequestAsync()
    {
        var indexer = new BookIndexer();
        var environment = await Lifecycle.StartAsync(indexer, new Lifecycle.FakeAniList { Volumes = 3 });
        var request = await Lifecycle.SubmitAsync(environment);
        Assert.IsEmpty(environment.Sabnzbd.Grabs);
        indexer.Titles.AddRange(Releases);
        return (environment, request, indexer);
    }

    [TestMethod]
    public async Task RankOneIsTheReleaseAutomaticAcquisitionTakesAndTheOthersAreRankedOrRefusedWithReasons()
    {
        var (environment, request, _) = await WaitingRequestAsync();
        await using var scope = environment;
        var manual = environment.Services.GetRequiredService<ReadingManualSearchService>();

        var result = (await manual.SearchAsync(request.Id, refresh: true, SearchDepth.Normal, CancellationToken.None))!;

        var ranked = result.Candidates.Where(candidate => candidate.Rank is not null).OrderBy(candidate => candidate.Rank).ToArray();
        Assert.IsNotEmpty(ranked);
        Assert.AreEqual(1, ranked[0].Rank);
        StringAssert.Contains(ranked[0].Title, "Vol 1-3", "The set that covers all three missing volumes is first.");
        CollectionAssert.AreEqual(Enumerable.Range(1, ranked.Length).ToArray(), ranked.Select(candidate => candidate.Rank!.Value).ToArray(), "Ranks are consecutive.");
        var rejected = result.Candidates.Where(candidate => candidate.Rank is null).ToArray();
        Assert.IsTrue(rejected.Any(candidate => candidate.Title.StartsWith("Another Manga", StringComparison.Ordinal) && candidate.RejectedBecause == "title does not match"));
        Assert.IsTrue(rejected.Any(candidate => candidate.Title.Contains("v04", StringComparison.Ordinal) && candidate.RejectedBecause == "volume 4 is not wanted"));
        Assert.IsTrue(rejected.All(candidate => !candidate.CanGrab));

        await environment.RecoverAsync(DateTime.UtcNow.AddDays(2));

        var grab = Assert.ContainsSingle(environment.Sabnzbd.Grabs);
        Assert.AreEqual(ranked[0].Title, grab.NzbName.Replace(".nzb", string.Empty), "Automatic acquisition grabs Rank 1.");
    }

    [TestMethod]
    public async Task AManualGrabTakesTheChosenReleaseAndRefusesOneThatTheServerRejects()
    {
        var (environment, request, _) = await WaitingRequestAsync();
        await using var scope = environment;
        var manual = environment.Services.GetRequiredService<ReadingManualSearchService>();
        var result = (await manual.SearchAsync(request.Id, refresh: true, SearchDepth.Normal, CancellationToken.None))!;
        var wrongVolume = result.Candidates.Single(candidate => candidate.Title.Contains("v04", StringComparison.Ordinal));
        var single = result.Candidates.Single(candidate => candidate.Title == "Frieren v01 CBZ");

        var refused = await manual.GrabAsync(request.Id, wrongVolume.Identity, CancellationToken.None);
        Assert.AreEqual(ManualGrabStatus.NotAvailable, refused.Status, "A release the identity gate rejects is never grabbed, whatever the browser sends.");
        Assert.IsEmpty(environment.Sabnzbd.Grabs);

        var taken = await manual.GrabAsync(request.Id, single.Identity, CancellationToken.None);
        Assert.AreEqual(ManualGrabStatus.Submitted, taken.Status);
        StringAssert.Contains(Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName, "Frieren v01");
        Assert.AreEqual(ManualGrabStatus.AlreadySubmitted, (await manual.GrabAsync(request.Id, single.Identity, CancellationToken.None)).Status);
    }
}
