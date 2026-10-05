using System.Net;
using Jularr.Web.Features.Acquisition.Access;

namespace Jularr.Tests;

/// <summary>A stored Movie/TV payload that omits or nulls a collection reads with sane defaults everywhere instead of throwing (#814).</summary>
[TestClass]
public sealed class VideoRequestPayloadParseTests
{
    private const string Work = "00000000-0000-0000-0000-000000000abc";

    [TestMethod]
    [DataRow($$"""{"workId":"{{Work}}","title":"Severance"}""")]
    [DataRow($$"""{"workId":"{{Work}}","title":"Severance","selectedEpisodeIds":null,"selectedSeasonIds":null,"excludedEpisodeIds":null}""")]
    [DataRow($$"""{"workId":"{{Work}}","scope":3}""")]
    public void NullOrMissingCollectionsReadAsEmpty(string json)
    {
        var payload = VideoRequestPayload.Parse(json);

        Assert.IsNotNull(payload);
        Assert.AreEqual(0, payload.SelectedEpisodeIds.Length);
        Assert.IsNotNull(payload.Title);
        Assert.IsTrue(payload.Monitored, "A payload without the Admin fields is monitored.");
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("""{"workId":"not-a-guid"}""")]
    [DataRow("not json")]
    public void JsonThatIsNotAPayloadReadsAsNullSoTheDefaultScopeApplies(string json)
    {
        Assert.IsNull(VideoRequestPayload.Parse(json));
    }

    [TestMethod]
    public void ASelectionOfAPayloadWithoutCollectionsCoversNothingAndNeverThrows()
    {
        var payload = VideoRequestPayload.Parse($$"""{"workId":"{{Work}}","scope":3}""")!;

        var selection = new VideoRequestSelection(payload, DateTime.UtcNow.AddDays(-1));

        Assert.IsFalse(selection.Includes(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow.AddDays(-10)));
    }

    [TestMethod]
    public void TheFallbackTitleFillsAPayloadThatCarriedNone()
    {
        var request = new AcquisitionRequest(
            Guid.NewGuid(), MediaAcquisitionKind.Tv, "tmdb", "1", "Severance", null, null, $$"""{"workId":"{{Work}}"}""",
            "owner", AcquisitionRequestStatus.Approved, null, null, null, DateTime.UtcNow, DateTime.UtcNow, null, null);

        var payload = VideoRequestPayload.Of(request, Guid.Parse(Work), request.Title, null);

        Assert.AreEqual("Severance", payload.Title);
    }

    [TestMethod]
    public async Task OneMalformedPayloadNeverBreaksWantedRequestsOrTheMediaPageOrHidesOtherRows()
    {
        await using var series = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Tv, "Severance", 2022, "95396", "Severance.S01E01.1080p.WEB-DL.x264-GROUP");
        await series.AddEpisodeAsync(1, 1);
        var malformed = await series.CreateApprovedAsync();
        await series.Get<AcquisitionAccessStore>().PatchPayloadAsync(malformed.Id, _ => $$"""{"workId":"{{series.Work.Id}}","title":"Severance","selectedEpisodeIds":null,"selectedSeasonIds":null}""", CancellationToken.None);
        await series.Get<AcquisitionAccessStore>().CreateAsync(
            new AcquisitionRequestDraft(MediaAcquisitionKind.Tv, "tmdb", "other-show", "Other Show", null, null, "{}"),
            "owner",
            AcquisitionRequestStatus.Approved,
            "owner",
            CancellationToken.None);
        await using var host = await VideoAdminPageHost.CreateAsync(series);

        var wanted = await host.GetHtmlAsync("/Admin/Wanted");
        var requests = await host.GetHtmlAsync("/Admin/Requests");

        StringAssert.Contains(wanted, "Severance");
        StringAssert.Contains(wanted, "Other Show");
        StringAssert.Contains(requests, "Severance");
        StringAssert.Contains(requests, "Other Show");
        Assert.AreEqual(HttpStatusCode.OK, await host.GetStatusAsync($"/Admin/Media/series/{series.Work.Id:D}"));    }
}
