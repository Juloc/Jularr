using System.Net;
using Jularr.Web.Features.Playback.Decision;
using Microsoft.Extensions.DependencyInjection;
using static Jularr.Tests.PlaybackTestPlans;

namespace Jularr.Tests;

/// <summary>GET /stream-sessions/{id}: the status the player asks after a broken stream, behind real routing.</summary>
[TestClass]
public sealed class StreamSessionStatusEndpointTests
{
    private const string Owner = "status-profile";
    private static readonly string s_path = "/api/client/v1/stream-sessions/";

    [TestMethod]
    public async Task OnlyTheOwningProfileSeesTheStatusAndPollingNeverKeepsTheSessionAlive()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var store = host.Services.GetRequiredService<PlaybackStreamSessionStore>();
        var session = store.Create(Owner, Guid.NewGuid(), Guid.NewGuid(), "/media/episode.mkv", 1400, Transcode(Video()), new PlaybackStreamSelections(null, null, false, PlaybackQualityPreset.Auto, PlaybackModePreference.Auto, "web"));
        var lastSeen = session.LastSeenUtc;

        var own = await host.SendAsync(HttpMethod.Get, s_path + session.Id, profile: Owner);
        var other = await host.SendAsync(HttpMethod.Get, s_path + session.Id, profile: "someone-else");

        Assert.AreEqual(HttpStatusCode.OK, own.Status, own.Body);
        StringAssert.Contains(own.Body, "\"state\":\"active\"");
        Assert.AreEqual(HttpStatusCode.NotFound, other.Status, "Another profile's session does not exist for this caller.");
        StringAssert.Contains(other.Body, "stream_session_not_found");
        Assert.AreEqual(lastSeen, session.LastSeenUtc, "A status poll is not activity.");
    }

    [TestMethod]
    public async Task AnHlsStreamTheServerNoLongerRunsIsReportedAsEnded()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var store = host.Services.GetRequiredService<PlaybackStreamSessionStore>();
        var session = store.Create(Owner, Guid.NewGuid(), Guid.NewGuid(), "/media/episode.mkv", 1400, Transcode(Video()), new PlaybackStreamSelections(null, null, false, PlaybackQualityPreset.Auto, PlaybackModePreference.Auto, "web"));
        session.ReplaceHlsSession(Guid.NewGuid(), 0);

        var status = await host.SendAsync(HttpMethod.Get, s_path + session.Id, profile: Owner);

        Assert.AreEqual(HttpStatusCode.OK, status.Status);
        StringAssert.Contains(status.Body, "\"state\":\"ended\"");
        StringAssert.Contains(status.Body, "\"recoverable\":false", "An unknown ending is never assumed to be harmless: the ordinary fallback decides.");

        var missing = await host.SendAsync(HttpMethod.Get, s_path + Guid.NewGuid(), profile: Owner);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.Status);
    }
}
