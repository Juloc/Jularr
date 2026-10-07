using Jularr.Web.Features.Acquisition.Access;

namespace Jularr.Tests;

/// <summary>A list of titles asks the request store once for the newest request of each, however many requests exist.</summary>
[TestClass]
public sealed class AcquisitionRequestLatestTests
{
    [TestMethod]
    public async Task TheLatestRequestOfEachGivenTitleComesBackInOneQueryAndOtherTitlesAreNotRead()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var first = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Music, "musicbrainz", "x", "Old", null, null), "owner", AcquisitionRequestStatus.Failed, "owner", CancellationToken.None);
        await Task.Delay(20);
        var second = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Music, "musicbrainz", "x", "New", null, null), "owner", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        var other = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Music, "musicbrainz", "y", "Other", null, null), "owner", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Music, "musicbrainz", "unasked", "Unasked", null, null), "owner", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);

        var latest = await fixture.Store.ListLatestAsync(MediaAcquisitionKind.Music, "musicbrainz", ["x", "y", "missing"], CancellationToken.None);

        Assert.AreEqual(2, latest.Count);
        Assert.AreEqual(second.Id, latest["x"].Id, "The newer request of a title wins over an older one that failed.");
        Assert.AreEqual(other.Id, latest["y"].Id);
        Assert.AreNotEqual(first.Id, latest["x"].Id);
        Assert.AreEqual(0, (await fixture.Store.ListLatestAsync(MediaAcquisitionKind.Music, "musicbrainz", [], CancellationToken.None)).Count);
    }
}
