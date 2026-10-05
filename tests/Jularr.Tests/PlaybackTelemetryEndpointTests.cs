using System.Net;
using System.Text.Json;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;
using Jularr.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Jularr.Tests.PlaybackTestPlans;

namespace Jularr.Tests;

/// <summary>PUT /stream-sessions/{id}/telemetry behind real routing: ownership, validation, idempotency and its transport limits.</summary>
[TestClass]
public sealed class PlaybackTelemetryEndpointTests
{
    private const string Owner = "telemetry-profile";
    private static readonly string s_path = "/api/client/v1/stream-sessions/";

    private static PlaybackStreamSession NewSession(VideoDetailPageTestHost host) =>
        host.Services.GetRequiredService<PlaybackStreamSessionStore>().Create(
            Owner,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "/media/episode.mkv",
            1400,
            Transcode(Video()),
            new PlaybackStreamSelections(null, null, false, PlaybackQualityPreset.Auto, PlaybackModePreference.Auto, "web"));

    private static object Body(long sequence = 1, string state = "playing", double buffer = 12.5, int? throughput = 24_000, int stalls = 1, long stallMs = 1800, double position = 61.5) =>
        new { sequence, state, bufferAheadSeconds = buffer, throughputKbps = throughput, stallCount = stalls, stallTotalMs = stallMs, positionSeconds = position };

    [TestMethod]
    public async Task OnlyTheOwningProfileCanReportAndTheReportIsStoredInMemory()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var session = NewSession(host);

        var own = await host.SendAsync(HttpMethod.Put, s_path + session.Id + "/telemetry", Body(), profile: Owner);
        var other = await host.SendAsync(HttpMethod.Put, s_path + session.Id + "/telemetry", Body(), profile: "someone-else");
        var missing = await host.SendAsync(HttpMethod.Put, s_path + Guid.NewGuid() + "/telemetry", Body(), profile: Owner);

        Assert.AreEqual(HttpStatusCode.OK, own.Status, own.Body);
        Assert.AreEqual(HttpStatusCode.NotFound, other.Status, "Another profile's session does not exist for this caller.");
        StringAssert.Contains(other.Body, "stream_session_not_found");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.Status);
        var stored = session.Telemetry.Latest!;
        Assert.AreEqual(PlaybackClientState.Playing, stored.State);
        Assert.AreEqual(12.5, stored.BufferAheadSeconds);
        Assert.AreEqual(24_000, stored.ThroughputKbps);
        Assert.AreEqual(1, stored.StallCount);
        Assert.AreEqual(1800, stored.StallTotalMs);
    }

    [TestMethod]
    public async Task ARepeatedOrOlderReportAnswersLikeANewOneAndChangesNothing()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var session = NewSession(host);
        var url = s_path + session.Id + "/telemetry";

        var first = await host.SendAsync(HttpMethod.Put, url, Body(sequence: 5, buffer: 20), profile: Owner);
        var again = await host.SendAsync(HttpMethod.Put, url, Body(sequence: 5, buffer: 20), profile: Owner);
        var older = await host.SendAsync(HttpMethod.Put, url, Body(sequence: 4, buffer: 1), profile: Owner);

        Assert.AreEqual(HttpStatusCode.OK, first.Status);
        Assert.AreEqual(HttpStatusCode.OK, again.Status);
        Assert.AreEqual(HttpStatusCode.OK, older.Status);
        Assert.AreEqual(first.Body, again.Body, "The answer is a reading of the session, so a repeat answers exactly like the first.");
        Assert.AreEqual(5, session.Telemetry.Latest!.Sequence);
        Assert.AreEqual(20, session.Telemetry.Latest.BufferAheadSeconds);
    }

    [TestMethod]
    public async Task ValuesNoPlayerCouldObserveAreRefusedBeforeAnythingIsStored()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var session = NewSession(host);
        var url = s_path + session.Id + "/telemetry";
        var invalid = new object[]
        {
            Body(buffer: -1),
            Body(buffer: 100_000),
            Body(throughput: -5),
            Body(throughput: 2_000_000_000),
            Body(stalls: -1),
            Body(stalls: 1_000_000),
            Body(stallMs: -1),
            Body(position: -1),
            Body(position: 100_000_000),
            Body(state: "dancing"),
            JsonDocument.Parse("""{"sequence":1,"state":"playing","bufferAheadSeconds":1e999,"stallCount":0,"stallTotalMs":0,"positionSeconds":1}""").RootElement,
            JsonDocument.Parse("""{"sequence":1,"state":"playing","stallCount":0,"stallTotalMs":0,"positionSeconds":1}""").RootElement,
            JsonDocument.Parse("""{"state":"playing","bufferAheadSeconds":1,"stallCount":0,"stallTotalMs":0,"positionSeconds":1}""").RootElement,
            JsonDocument.Parse("{}").RootElement
        };

        foreach (var body in invalid)
        {
            var response = await host.SendAsync(HttpMethod.Put, url, body, profile: Owner);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.Status, $"{JsonSerializer.Serialize(body)} was accepted: {response.Body}");
        }

        Assert.IsNull(session.Telemetry.Latest);
    }

    [TestMethod]
    public async Task ABodyLargerThanAReportIsRefusedWith413AndStoresNothing()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var session = NewSession(host);
        var oversized = new { sequence = 1, state = "playing", bufferAheadSeconds = 1, stallCount = 0, stallTotalMs = 0, positionSeconds = 1, padding = new string('x', PlaybackTelemetryRules.MaxBodyBytes * 4) };

        var response = await host.SendAsync(HttpMethod.Put, s_path + session.Id + "/telemetry", oversized, profile: Owner);

        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.Status, response.Body);
        Assert.IsNull(session.Telemetry.Latest);
    }

    [TestMethod]
    public async Task ANullRateAndZeroValuesAreValidMeasurements()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var session = NewSession(host);

        var response = await host.SendAsync(HttpMethod.Put, s_path + session.Id + "/telemetry", Body(buffer: 0, throughput: null, stalls: 0, stallMs: 0, state: "buffering"), profile: Owner);

        Assert.AreEqual(HttpStatusCode.OK, response.Status, response.Body);
        Assert.IsNull(session.Telemetry.Latest!.ThroughputKbps);
        Assert.AreEqual(PlaybackClientState.Buffering, session.Telemetry.Latest.State);
    }

    [TestMethod]
    public async Task TheAnswerCarriesTheAdviceAndWhatTheServerMeasuredWhileConverting()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var session = NewSession(host);
        var url = s_path + session.Id + "/telemetry";

        var plain = await host.SendAsync(HttpMethod.Put, url, Body(sequence: 1), profile: Owner);
        session.BeginTranscodeRun(PlaybackHardwareBackend.Software)!(new PlaybackTranscodeSample(1.94, 48.5, 30));
        var measured = await host.SendAsync(HttpMethod.Put, url, Body(sequence: 2), profile: Owner);

        using var first = JsonDocument.Parse(plain.Body);
        Assert.AreEqual("none", first.RootElement.GetProperty("advice").GetString());
        Assert.AreEqual(JsonValueKind.Null, first.RootElement.GetProperty("reason").ValueKind, "No advice has no reason.");
        Assert.AreEqual(JsonValueKind.Null, first.RootElement.GetProperty("transcodeSpeed").ValueKind, "Nothing was measured yet.");
        using var second = JsonDocument.Parse(measured.Body);
        Assert.AreEqual(1.94, second.RootElement.GetProperty("transcodeSpeed").GetDouble());
        Assert.AreEqual(48.5, second.RootElement.GetProperty("transcodeFps").GetDouble());
    }

    [TestMethod]
    public void AdviceAndReasonAreSnakeCaseCodesOnTheWire()
    {
        var json = JsonSerializer.Serialize(
            new ClientTelemetryAnswer(PlaybackAdaptationAdvice.StepDown, PlaybackAdaptationReason.TranscodeTooSlow, 0.62, 12.5),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.AreEqual("""{"advice":"step_down","reason":"transcode_too_slow","transcodeSpeed":0.62,"transcodeFps":12.5}""", json);
        foreach (var reason in Enum.GetValues<PlaybackAdaptationReason>())
        {
            Assert.IsFalse(string.IsNullOrEmpty(JsonSerializer.Serialize(reason, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Trim('"')));
        }
    }

    [TestMethod]
    public async Task TheEndpointIsRateLimitedSeparatelyAndNeverTouchesTheDatabase()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var endpoint = host.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x => x.RoutePattern.RawText!.EndsWith("/telemetry", StringComparison.Ordinal));

        Assert.AreEqual("PUT", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single());
        Assert.AreEqual(PlaybackDecisionRegistration.TelemetryRateLimitPolicy, endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()!.PolicyName);

        var handler = endpoint.Metadata.GetMetadata<System.Reflection.MethodInfo>()!;
        Assert.IsFalse(
            handler.GetParameters().Any(x => typeof(DbContext).IsAssignableFrom(x.ParameterType)),
            "Telemetry is ephemeral session state: the handler has no database access.");
    }

    [TestMethod]
    public void TheTelemetryPolicyRegistersNextToThePlanPolicyAndIsAdvertised()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IMediaProcessRunner>(new FakeMediaProcessRunner(_ => null))
            .AddPlaybackDecision()
            .BuildServiceProvider();

        // Materializing the options runs both registrations; a duplicate name would throw.
        Assert.IsNotNull(provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value);
        Assert.IsTrue(ClientApiContract.Capabilities().Features.PlaybackTelemetry);
        Assert.AreEqual(
            "/api/client/v1/stream-sessions/00000000-0000-0000-0000-000000000000/telemetry",
            ClientApiRoutes.StreamSessionTelemetry(Guid.Empty));
    }
}
