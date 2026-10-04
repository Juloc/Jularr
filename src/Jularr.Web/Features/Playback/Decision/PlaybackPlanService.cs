using System.Net;
using System.Net.Sockets;
using Jularr.Web.Data;
using Jularr.Web.Features.Devices;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Storage;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Playback.Decision;

/// <summary>What a client asks for when it starts (or re-plans) playback.</summary>
public sealed record PlaybackPlanInput(
    ClientPlaybackCapabilities? Capabilities,
    string ClientKind,
    string? UserAgent,
    IPAddress? RemoteAddress,
    int? AudioStreamIndex = null,
    int? SubtitleStreamIndex = null,
    bool BurnInSubtitle = false,
    PlaybackQualityPreset? Quality = null,
    PlaybackModePreference ModePreference = PlaybackModePreference.Auto,
    PlaybackNetworkReport? Network = null,
    IReadOnlySet<PlaybackDeliveryMode>? FailedModes = null,
    Guid? ReplacesSessionId = null,
    bool Wake = true);

/// <summary>
/// The client's own view of its connection. Only measured values count as throughput;
/// save-data and cellular hints mark the connection as metered.
/// </summary>
public sealed record PlaybackNetworkReport(
    int? ThroughputKbps = null,
    double? BufferSeconds = null,
    int? RecentStalls = null,
    bool? SaveData = null,
    bool? Metered = null,
    string? ConnectionType = null);

public sealed record PlaybackPlanOutcome(
    PlaybackPlan Plan,
    PlaybackStreamSession? Session,
    Guid MediaFileId,
    MediaAvailabilitySnapshot? Availability,
    bool CapabilitiesInferred);

public static class PlaybackNetworkClassifier
{
    // Carrier-grade NAT (100.64/10) is also where overlay VPNs such as Tailscale live; such a
    // client may be anywhere, so it counts as remote.
    public static PlaybackNetworkClass Classify(IPAddress? remote, PlaybackNetworkReport? report)
    {
        if (report is { } hints &&
            (hints.SaveData == true ||
             hints.Metered == true ||
             string.Equals(hints.ConnectionType, "cellular", StringComparison.OrdinalIgnoreCase)))
        {
            return PlaybackNetworkClass.Metered;
        }

        if (remote is null)
        {
            return PlaybackNetworkClass.Unknown;
        }

        if (remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }

        if (IPAddress.IsLoopback(remote))
        {
            return PlaybackNetworkClass.Local;
        }

        if (remote.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = remote.GetAddressBytes();
            var local = bytes[0] == 10 ||
                        (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                        (bytes[0] == 192 && bytes[1] == 168) ||
                        (bytes[0] == 169 && bytes[1] == 254);
            return local ? PlaybackNetworkClass.Local : PlaybackNetworkClass.Remote;
        }

        if (remote.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = remote.GetAddressBytes();
            var uniqueLocal = (bytes[0] & 0xfe) == 0xfc;
            return uniqueLocal || remote.IsIPv6LinkLocal
                ? PlaybackNetworkClass.Local
                : PlaybackNetworkClass.Remote;
        }

        return PlaybackNetworkClass.Unknown;
    }
}

/// <summary>
/// The current server-side inputs of a decision: ffmpeg processing and free encode slots.
/// </summary>
public sealed class PlaybackServerCapabilityProvider(PlaybackTranscodeSlots slots)
{
    public PlaybackServerCapabilities Current() =>
        PlaybackServerCapabilities.Software(slots.Available);
}

/// <summary>
/// Resolves a playback plan for one episode and opens the bounded session that serves it.
/// The server resolves the media path and every selection against the canonical inventory;
/// the client only contributes capabilities, choices and measurements.
/// </summary>
public sealed class PlaybackPlanService(
    AppDbContext db,
    MediaInventoryService mediaInventory,
    PlaybackStreamSessionStore sessions,
    PlaybackServerCapabilityProvider serverCapabilities,
    MediaAvailabilityService? mediaAvailability = null,
    KnownDeviceRegistry? deviceRegistry = null,
    ActiveSessionService? activeSessions = null)
{
    public async Task<PlaybackPlanOutcome?> PlanAsync(
        Guid episodeId,
        string profileId,
        PlaybackPlanInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var row = await db.MediaFiles
            .AsNoTracking()
            .Where(x => x.EpisodeId == episodeId)
            .OrderBy(x => x.Path)
            .Select(x => new { x.Id, x.Path, x.SizeBytes })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var capabilities = input.Capabilities?.Normalize() ??
                           ClientPlaybackCapabilities.InferFromUserAgent(input.UserAgent, input.ClientKind);
        var networkClass = PlaybackNetworkClassifier.Classify(input.RemoteAddress, input.Network);
        var network = new PlaybackNetworkConditions(
            networkClass,
            input.Network?.ThroughputKbps is > 0 and <= 10_000_000 ? input.Network.ThroughputKbps : null,
            input.Network?.BufferSeconds is >= 0 and <= 3600 ? input.Network.BufferSeconds : null,
            Math.Clamp(input.Network?.RecentStalls ?? 0, 0, 100));
        var quality = input.Quality ?? PlaybackQualityPresets.DefaultFor(networkClass);
        var previous = input.ReplacesSessionId is { } replaced ? sessions.Get(replaced, profileId) : null;

        // Playing is what wakes sleeping Wake-on-LAN storage (#411): a plan requested to play
        // starts the NAS through the coalesced start attempt; a plan requested only to
        // decide (a page opening) never does. Unreadable storage returns an Unavailable plan
        // with the availability state so the client waits and re-plans once it is online.
        var availability = mediaAvailability is null
            ? null
            : await mediaAvailability.CheckMediaAsync(row.Id, force: false, cancellationToken, wake: input.Wake);
        if (availability is { IsAvailable: false })
        {
            return new PlaybackPlanOutcome(
                UnavailablePlan(
                    PlaybackReasonCodes.MediaUnavailable,
                    quality,
                    networkClass,
                    ("state", availability.State.ToString().ToLowerInvariant())),
                null,
                row.Id,
                availability,
                capabilities.Inferred);
        }

        var inventory = await mediaInventory.EnsureAnalyzedAsync(row.Id, cancellationToken);
        if (inventory?.Technical is not { } technical)
        {
            return new PlaybackPlanOutcome(
                UnavailablePlan(PlaybackReasonCodes.MediaNotAnalyzed, quality, networkClass),
                null,
                row.Id,
                availability,
                capabilities.Inferred);
        }

        var media = PlaybackMediaProfile.From(row.Path, row.SizeBytes, technical);
        var plan = PlaybackDecisionEngine.Decide(new PlaybackDecisionRequest(
            media,
            capabilities,
            serverCapabilities.Current(),
            input.AudioStreamIndex,
            input.SubtitleStreamIndex,
            input.BurnInSubtitle,
            quality,
            network,
            input.ModePreference,
            input.FailedModes,
            previous?.Plan.Quality.DeliveredBitrateKbps));

        PlaybackStreamSession? session = null;
        if (plan.Mode != PlaybackDeliveryMode.Unavailable)
        {
            session = sessions.Create(
                profileId,
                episodeId,
                row.Id,
                row.Path,
                media.DurationSeconds,
                plan,
                new PlaybackStreamSelections(
                    input.AudioStreamIndex,
                    input.SubtitleStreamIndex,
                    input.BurnInSubtitle,
                    quality,
                    input.ModePreference,
                    capabilities.Client.Kind),
                previous?.Id);

            if (activeSessions is not null)
            {
                await activeSessions.OpenAsync(
                    session.Id,
                    profileId,
                    row.Id,
                    plan.Mode.ToString(),
                    capabilities.Client.Kind,
                    input.ReplacesSessionId,
                    cancellationToken);
            }

            // Known clients/devices registry (#527): the same touch point that opens the live
            // session records/refreshes the device that opened it.
            if (deviceRegistry is not null)
            {
                await deviceRegistry.TouchAsync(
                    profileId,
                    capabilities.Client.Kind,
                    capabilities.Client.Name,
                    capabilities.Client.AppVersion,
                    input.UserAgent,
                    cancellationToken);
            }
        }

        return new PlaybackPlanOutcome(plan, session, row.Id, availability, capabilities.Inferred);
    }

    private static PlaybackPlan UnavailablePlan(
        string code,
        PlaybackQualityPreset quality,
        PlaybackNetworkClass network,
        params (string Key, string Value)[] values) =>
        new(
            PlaybackDeliveryMode.Unavailable,
            PlaybackTransport.None,
            "",
            null,
            null,
            new PlaybackQualityResolution(quality, network, null, PlaybackLimitSource.None, null, null),
            [
                new PlaybackReason(
                    code,
                    PlaybackReasonSeverity.Blocker,
                    Values: values.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal))
            ],
            PlaybackCapabilitySupport.Unknown);
}
