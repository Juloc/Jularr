using System.Net;
using System.Net.Sockets;
using Jularr.Web.Data;
using Jularr.Web.Features.Devices;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Playback.Transcoding;
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
    bool Wake = true,
    PlaybackAdaptationAdvice FollowedAdvice = PlaybackAdaptationAdvice.None);

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
    bool CapabilitiesInferred,
    PlaybackVideoTarget Target,
    long ResumePositionMs);

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
/// The current server-side inputs of a decision: the Admin's transcoding switch, the detected
/// encoder (hardware only when its test encode passed and its breaker is closed) and the free
/// slots of that encoder's cost class.
/// </summary>
public sealed class PlaybackServerCapabilityProvider(
    PlaybackTranscodingSettingsStore settings,
    PlaybackTranscodeSlots slots,
    PlaybackHardwareService hardware)
{
    /// <param name="tooSlow">Encoders that already failed to keep up with real time for the title being planned; see <see cref="PlaybackHardwareService.Choose"/>.</param>
    public PlaybackServerCapabilities Current(IReadOnlyCollection<PlaybackHardwareBackend>? tooSlow = null)
    {
        var choice = hardware.Choose(tooSlow);
        var costClass = choice.Target.IsHardware ? PlaybackCostClass.HardwareVideo : PlaybackCostClass.SoftwareVideo;
        return PlaybackServerCapabilities.Software(slots.Available(costClass)) with
        {
            // Until the first detection finished the server behaves as it always did: ffmpeg is assumed, hardware is not.
            // Only "not found" blocks remux and transcode; a timeout or a failed run is transient and the sweeper detects again.
            ProcessingAvailable = hardware.Detected?.FfmpegState is not PlaybackFfmpegState.NotFound,
            TranscodingEnabled = settings.Current.TranscodingEnabled,
            H264Encoder = PlaybackHardwareBackends.H264Encoder(choice.Target.Backend),
            MaxTranscodeHeight = choice.Target.IsHardware ? PlaybackServerCapabilities.HardwareMaxHeight : PlaybackServerCapabilities.SoftwareMaxHeight,
            SuspendedHardware = choice.Suspended,
            BufferPreset = settings.Current.BufferPreset,
            // Software is the last choice: when it is the one that was too slow nothing is left to try.
            EncoderTooSlow = tooSlow?.Contains(choice.Target.Backend) == true
        };
    }
}

/// <summary>
/// Resolves one shared playback plan from a canonical video target and opens the bounded
/// session that serves it. Movie, Anime and TV all flow through the same inventory and
/// <see cref="PlaybackDecisionEngine"/> policy. The legacy Anime Episode overload is only
/// a compatibility adapter.
/// </summary>
public sealed class PlaybackPlanService(
    AppDbContext db,
    MediaInventoryService mediaInventory,
    PlaybackStreamSessionStore sessions,
    PlaybackServerCapabilityProvider serverCapabilities,
    MediaAvailabilityService? mediaAvailability = null,
    KnownDeviceRegistry? deviceRegistry = null,
    ActiveSessionService? activeSessions = null,
    CanonicalMediaStorageService? canonicalStorage = null,
    VideoProgressService? videoProgress = null,
    PlaybackAdmissionService? admission = null)
{
    /// <summary>
    /// Legacy Anime compatibility adapter. New callers use
    /// <see cref="PlanAsync(PlaybackVideoTarget,string,PlaybackPlanInput,CancellationToken)"/>.
    /// </summary>
    public async Task<PlaybackPlanOutcome?> PlanAsync(
        Guid episodeId,
        string profileId,
        PlaybackPlanInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var legacy = await (
                from file in db.MediaFiles.AsNoTracking()
                join assetValue in db.MediaAssets.AsNoTracking()
                    on file.MediaAssetId equals (Guid?)assetValue.Id into assetRows
                from asset in assetRows.DefaultIfEmpty()
                where file.EpisodeId == episodeId
                orderby file.Path
                select new LegacyPlayableRow(
                    file.Id,
                    file.Path,
                    file.SizeBytes,
                    asset == null ? null : asset.WorkId,
                    asset == null ? null : asset.WorkEpisodeId))
            .FirstOrDefaultAsync(cancellationToken);
        if (legacy is null)
        {
            return null;
        }

        var target = legacy.WorkId is { } workId
            ? new PlaybackVideoTarget(workId, legacy.WorkEpisodeId)
            : new PlaybackVideoTarget(episodeId, episodeId);

        return await PlanResolvedAsync(
            target,
            new ResolvedPlayableFile(legacy.Id, legacy.Path, legacy.SizeBytes),
            profileId,
            input,
            legacyEpisodeId: episodeId,
            cancellationToken);
    }

    public async Task<PlaybackPlanOutcome?> PlanAsync(
        PlaybackVideoTarget target,
        string profileId,
        PlaybackPlanInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(input);

        var playable = canonicalStorage is not null
            ? await canonicalStorage.ResolveVideoAsync(
                target.WorkId,
                target.WorkEpisodeId,
                cancellationToken)
            : await ResolveCanonicalVideoAsync(target, cancellationToken);

        if (playable is null)
        {
            return null;
        }

        return await PlanResolvedAsync(
            target,
            new ResolvedPlayableFile(
                playable.StoredFileId,
                playable.Path,
                playable.SizeBytes),
            profileId,
            input,
            legacyEpisodeId: null,
            cancellationToken);
    }

    private async Task<PlaybackPlanOutcome> PlanResolvedAsync(
        PlaybackVideoTarget target,
        ResolvedPlayableFile row,
        string profileId,
        PlaybackPlanInput input,
        Guid? legacyEpisodeId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var capabilities = input.Capabilities?.Normalize() ??
                           ClientPlaybackCapabilities.InferFromUserAgent(input.UserAgent, input.ClientKind);
        var networkClass = PlaybackNetworkClassifier.Classify(input.RemoteAddress, input.Network);
        var previous = input.ReplacesSessionId is { } replaced ? sessions.Get(replaced, profileId) : null;

        // What the replaced session's player reported (its buffer and the stalls of the last minute) is the evidence of how that
        // delivery went and wins over the hints of the request, but only for the same title and only while it is fresh: another
        // title's stalls say nothing about this one, and a stale or missing report leaves the request's own hints in charge.
        // Throughput stays the request's hint because a player cannot measure the link while the browser is not fetching.
        var evidence = previous is not null && previous.Target == target ? sessions.TelemetryEvidence(previous) : null;
        var network = new PlaybackNetworkConditions(
            networkClass,
            input.Network?.ThroughputKbps is > 0 and <= 10_000_000 ? input.Network.ThroughputKbps : null,
            evidence?.BufferSeconds ?? (input.Network?.BufferSeconds is >= 0 and <= 3600 ? input.Network.BufferSeconds : null),
            evidence?.RecentStalls ?? Math.Clamp(input.Network?.RecentStalls ?? 0, 0, 100));
        var quality = input.Quality ?? PlaybackQualityPresets.DefaultFor(networkClass);

        // The replaced session's advice and what the server learned about its own capacity for this title (a tier ceiling, encoders that could
        // not keep up) travel with the chain of re-plans of the title and nowhere else.
        var directive = previous is not null && previous.Target == target ? sessions.NextDirective(previous, input.FollowedAdvice) : PlaybackAdaptationDirective.None;
        var resumePositionMs = videoProgress is null
            ? 0
            : (await videoProgress.GetAsync(
                profileId,
                target.ToProgressTarget(),
                cancellationToken))?.ResumePositionMs ?? 0;

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
                capabilities.Inferred,
                target,
                resumePositionMs);
        }

        var inventory = await mediaInventory.EnsureAnalyzedAsync(row.Id, cancellationToken);
        if (inventory?.Technical is not { } technical)
        {
            return new PlaybackPlanOutcome(
                UnavailablePlan(PlaybackReasonCodes.MediaNotAnalyzed, quality, networkClass),
                null,
                row.Id,
                availability,
                capabilities.Inferred,
                target,
                resumePositionMs);
        }

        var media = PlaybackMediaProfile.From(row.Path, row.SizeBytes, technical);
        var plan = PlaybackDecisionEngine.Decide(new PlaybackDecisionRequest(
            media,
            capabilities,
            serverCapabilities.Current(directive.SlowBackends),
            input.AudioStreamIndex,
            input.SubtitleStreamIndex,
            input.BurnInSubtitle,
            quality,
            network,
            input.ModePreference,
            input.FailedModes,
            previous?.Plan.Quality.DeliveredBitrateKbps,
            directive));

        PlaybackStreamSession? session = null;
        if (plan.Mode != PlaybackDeliveryMode.Unavailable)
        {
            // A re-plan that follows the server's advice is requested while the old stream keeps playing: the old session is only retired
            // after this one's first output succeeded (see the stream endpoints), and an admission refusal is answered here, before the
            // player swaps anything.
            var followsAdvice = previous is not null && previous.Target == target && input.FollowedAdvice != PlaybackAdaptationAdvice.None;
            session = sessions.Create(
                profileId,
                target,
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
                previous?.Id,
                legacyEpisodeId,
                directive,
                deferRetirement: followsAdvice);

            if (followsAdvice && admission?.Preflight(plan, profileId, session) is { } refusal)
            {
                sessions.Remove(session.Id, profileId);
                return new PlaybackPlanOutcome(
                    UnavailablePlan(refusal, quality, networkClass),
                    null,
                    row.Id,
                    availability,
                    capabilities.Inferred,
                    target,
                    resumePositionMs);
            }

            // The ActiveSession of a deferred re-plan changes hands when the old session is retired.
            if (activeSessions is not null && !followsAdvice)
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

        return new PlaybackPlanOutcome(
            plan,
            session,
            row.Id,
            availability,
            capabilities.Inferred,
            target,
            resumePositionMs);
    }

    private async Task<CanonicalPlayableFile?> ResolveCanonicalVideoAsync(
        PlaybackVideoTarget target,
        CancellationToken cancellationToken) =>
        await (
            from asset in db.MediaAssets.AsNoTracking()
            join file in db.StoredFiles.AsNoTracking()
                on (Guid?)asset.Id equals file.MediaAssetId
            where asset.Kind == MediaAssetKind.Video &&
                  asset.WorkId == target.WorkId &&
                  asset.WorkEpisodeId == target.WorkEpisodeId
            orderby file.Path
            select new CanonicalPlayableFile(
                asset.Id,
                file.Id,
                asset.WorkId,
                asset.WorkEpisodeId,
                asset.WorkVersionId,
                file.Path,
                file.SizeBytes,
                file.LastWriteTimeUtc))
        .FirstOrDefaultAsync(cancellationToken);

    private sealed record ResolvedPlayableFile(
        Guid Id,
        string Path,
        long SizeBytes);

    private sealed record LegacyPlayableRow(
        Guid Id,
        string Path,
        long SizeBytes,
        Guid? WorkId,
        Guid? WorkEpisodeId);

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
