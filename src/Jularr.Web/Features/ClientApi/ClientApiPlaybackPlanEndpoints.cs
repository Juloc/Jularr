using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.MediaSegments;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Subtitles;

namespace Jularr.Web.Features.ClientApi;

public sealed record ClientVideoTarget(
    Guid WorkId,
    Guid? WorkEpisodeId)
{
    public bool IsValid => WorkId != Guid.Empty && (WorkEpisodeId is null || WorkEpisodeId != Guid.Empty);
}

/// <summary>
/// One playback-plan request for every client (web, PWA, Android, TV). The capability
/// document is optional; without it the server infers a conservative one. New video
/// callers send <see cref="Target"/>; the legacy Anime route supplies it server-side.
/// </summary>
public sealed record ClientPlaybackPlanRequest(
    ClientPlaybackCapabilities? Capabilities = null,
    string? AudioTrackId = null,
    string? SubtitleTrackId = null,
    bool BurnInSubtitle = false,
    string? Quality = null,
    string? Mode = null,
    PlaybackNetworkReport? Network = null,
    IReadOnlyList<PlaybackDeliveryMode>? FailedModes = null,
    Guid? ReplacesSessionId = null,
    bool Wake = true,
    ClientVideoTarget? Target = null);

/// <summary>
/// Where and how to fetch the plan's stream. Live transports restart at a position by adding
/// <see cref="StartParameter"/> (seconds) to <see cref="Url"/>; the client then shows
/// <c>start + currentTime</c> as the absolute position.
/// </summary>
public sealed record ClientPlaybackDelivery(
    string Url,
    PlaybackTransport Transport,
    string? StartParameter,
    bool SeekableWithinStream);

public sealed record ClientPlaybackPlanResponse(
    Guid? SessionId,
    PlaybackPlan Plan,
    ClientPlaybackDelivery? Delivery,
    ClientMediaAvailability? Availability,
    bool CapabilitiesInferred,
    ClientVideoTarget Target,
    long ResumePositionMs);

/// <summary>Whether the server still runs the session's stream. <see cref="State"/> is "active" or "ended"; <see cref="Reason"/> says why an ended one ended when the server knows.</summary>
public sealed record ClientStreamSessionStatus(string State, string? Reason);

public sealed record ClientVideoProgressUpdate(
    ClientVideoTarget? Target,
    long PositionMs,
    long? DurationMs,
    bool Completed);

public sealed record ClientVideoProgressResponse(
    ClientVideoTarget Target,
    long PositionMs,
    long? DurationMs,
    int Percent,
    bool IsCompleted,
    DateTime? UpdatedAtUtc,
    long ResumePositionMs);

public sealed record ClientVideoPlayerRequest(
    ClientVideoTarget? Target);

public sealed record ClientVideoNavigationItem(
    ClientVideoTarget Target,
    int SeasonNumber,
    int EpisodeNumber,
    string? Title);

public sealed record ClientVideoNavigation(
    ClientVideoNavigationItem? Previous,
    ClientVideoNavigationItem? Next);

public sealed record ClientCanonicalVideoMedia(
    Guid MediaFileId,
    string FileName,
    string ContentType,
    long SizeBytes,
    long? DurationMs,
    string? VideoCodec,
    string? PixelFormat,
    string? AudioCodec,
    string DirectContentUrl);

public sealed record ClientCanonicalVideoPlayerBootstrap(
    int ApiVersion,
    ClientVideoTarget Target,
    string MediaType,
    string WorkTitle,
    ClientVideoNavigation Navigation,
    ClientCanonicalVideoMedia Media,
    IReadOnlyList<ClientMediaTrack> AudioTracks,
    IReadOnlyList<ClientMediaTrack> SubtitleTracks,
    string? DefaultAudioTrackId,
    string? DefaultSubtitleTrackId,
    ClientPlayerDefaults Defaults,
    ClientVideoProgressResponse Progress,
    ClientPlayerControls Controls,
    string PlaybackPlanUrl,
    string ProgressUrl,
    ClientSegmentDescriptor? Segments = null,
    ClientTrickplayDescriptor? Trickplay = null);

public static class ClientApiPlaybackPlanEndpoints
{
    public const string RateLimitPolicy = PlaybackDecisionRegistration.RateLimitPolicy;

    public static IEndpointRouteBuilder MapClientApiPlaybackPlanV1(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup(ClientApiContract.BasePath)
            .RequireAuthorization()
            .AddEndpointFilter<ClientVideoAccessFilter>();

        group.MapPost("/episodes/{episodeId:guid}/playback-plan", async (
            Guid episodeId,
            ClientPlaybackPlanRequest request,
            PlaybackPlanService plans,
            CurrentAccountContext currentAccount,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseInput(request, httpContext, out var input, out var error))
            {
                return error!;
            }

            var outcome = await plans.PlanAsync(episodeId, currentAccount.ProfileId, input, cancellationToken);
            if (outcome is null)
            {
                return Results.NotFound(new ClientErrorResponse(
                    "media_not_found",
                    "This episode does not have a media file."));
            }

            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(ToResponse(outcome, currentAccount));
        })
        .RequireRateLimiting(RateLimitPolicy);

        group.MapPost("/video/player", async (
            ClientVideoPlayerRequest request,
            CanonicalVideoPlayerService player,
            CurrentAccountContext currentAccount,
            CancellationToken cancellationToken) =>
        {
            if (request.Target is not { IsValid: true })
            {
                return Results.BadRequest(new ClientErrorResponse(
                    "invalid_playback_target",
                    "target.workId is required and target.workEpisodeId must be a valid id when present."));
            }

            var target = request.Target!;
            var snapshot = await player.GetAsync(
                currentAccount.ProfileId,
                new PlaybackVideoTarget(target.WorkId, target.WorkEpisodeId),
                cancellationToken);
            if (snapshot is null)
            {
                return Results.NotFound(new ClientErrorResponse(
                    "video_target_not_found",
                    "The canonical video target is not locally playable."));
            }

            return Results.Ok(ToPlayerBootstrap(snapshot));
        });

        group.MapPost("/video/playback-plan", async (
            ClientPlaybackPlanRequest request,
            PlaybackPlanService plans,
            CurrentAccountContext currentAccount,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (request.Target is not { IsValid: true } target)
            {
                return Results.BadRequest(new ClientErrorResponse(
                    "invalid_playback_target",
                    "target.workId is required and target.workEpisodeId must be a valid id when present."));
            }

            if (!TryParseInput(request, httpContext, out var input, out var error))
            {
                return error!;
            }

            var outcome = await plans.PlanAsync(
                new PlaybackVideoTarget(target.WorkId, target.WorkEpisodeId),
                currentAccount.ProfileId,
                input,
                cancellationToken);
            if (outcome is null)
            {
                return Results.NotFound(new ClientErrorResponse(
                    "media_not_found",
                    "This canonical video target does not have a playable file."));
            }

            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(ToResponse(outcome, currentAccount));
        })
        .RequireRateLimiting(RateLimitPolicy);

        group.MapPut("/video/progress", async (
            ClientVideoProgressUpdate update,
            VideoProgressService progress,
            CurrentAccountContext currentAccount,
            CancellationToken cancellationToken) =>
        {
            if (update.Target is not { IsValid: true } ||
                update.PositionMs < 0 ||
                update.DurationMs is < 0)
            {
                return Results.BadRequest(new ClientErrorResponse(
                    "invalid_progress",
                    "A valid canonical video target and non-negative progress values are required."));
            }

            var target = update.Target!;
            var snapshot = await progress.UpdateAsync(
                currentAccount.ProfileId,
                new MediaProgressTarget(target.WorkId, target.WorkEpisodeId),
                new MediaProgressUpdate(update.PositionMs, update.DurationMs, update.Completed),
                cancellationToken);
            if (snapshot is null)
            {
                return Results.NotFound(new ClientErrorResponse(
                    "video_target_not_found",
                    "The canonical video target does not exist."));
            }

            return Results.Ok(new ClientVideoProgressResponse(
                new ClientVideoTarget(snapshot.WorkId, snapshot.WorkEpisodeId),
                snapshot.PositionMs,
                snapshot.DurationMs,
                snapshot.Percent,
                snapshot.IsCompleted,
                snapshot.UpdatedAt,
                snapshot.ResumePositionMs));
        });

        group.MapGet("/video/subtitle-tracks/{trackId}/cues", async (
            string trackId,
            Guid workId,
            Guid? workEpisodeId,
            CanonicalMediaStorageService storage,
            PlaybackService playback,
            CancellationToken cancellationToken) =>
        {
            if (!PlaybackTrackIds.TryParse(trackId, out var streamIndex))
            {
                return Results.BadRequest(new ClientErrorResponse("invalid_track_id", "trackId must be a canonical stream:{index} track id."));
            }

            var file = await storage.ResolveVideoAsync(workId, workEpisodeId, cancellationToken);
            var cues = file is null ? null : await playback.GetEmbeddedSubtitleCuesAsync(file.StoredFileId, file.Path, streamIndex, cancellationToken);
            return cues is null
                ? Results.NotFound(new ClientErrorResponse("subtitle_track_not_found", "The requested embedded text subtitle stream is unavailable."))
                : Results.Ok(ClientApiMappings.ToClientEmbeddedSubtitleCues(cues));
        })
        .RequireRateLimiting(RateLimitPolicy);

        group.MapGet("/media/{mediaFileId:guid}/trickplay", async (
            Guid mediaFileId,
            CanonicalPlayerNavigationAssetService navigationAssets,
            CancellationToken cancellationToken) =>
        {
            var descriptor = await navigationAssets.DescribeAsync(mediaFileId, cancellationToken);
            return Results.Ok(ToCanonicalTrickplay(mediaFileId, descriptor));
        });

        group.MapGet("/media/{mediaFileId:guid}/trickplay/{fileName}", async (
            Guid mediaFileId,
            string fileName,
            CanonicalPlayerNavigationAssetService navigationAssets,
            CancellationToken cancellationToken) =>
        {
            var asset = await navigationAssets.GetAssetAsync(
                mediaFileId,
                fileName,
                cancellationToken);
            return asset is null
                ? Results.NotFound(new ClientErrorResponse(
                    "trickplay_asset_not_found",
                    "The seek preview asset is unavailable."))
                : Results.File(asset.Path, asset.ContentType);
        });

        group.MapGet("/stream-sessions/{sessionId:guid}/stream", async (
            Guid sessionId,
            double? startSeconds,
            PlaybackStreamSessionStore sessions,
            PlaybackAdmissionService admission,
            CurrentAccountContext currentAccount,
            CancellationToken cancellationToken) =>
        {
            var session = sessions.Get(sessionId, currentAccount.ProfileId);
            if (session is null)
            {
                return SessionNotFound();
            }

            if (session.Plan.Transport != PlaybackTransport.ProgressiveMp4)
            {
                return Results.BadRequest(new ClientErrorResponse(
                    "wrong_transport",
                    "This playback session is not delivered as a progressive stream."));
            }

            if (!TryNormalizeStart(startSeconds, session.DurationSeconds, out var start))
            {
                return InvalidStart();
            }

            if (!File.Exists(session.SourcePath))
            {
                return Results.NotFound(new ClientErrorResponse(
                    "media_not_found",
                    "The media file of this playback session is unavailable."));
            }

            try
            {
                // The response only starts once ffmpeg produced its first bytes, so a failing encoder can still be replaced by the fallback.
                var live = await admission.StartAsync(
                    session.Plan,
                    currentAccount.ProfileId,
                    async admitted =>
                    {
                        LivePlaybackStream? stream = null;
                        try
                        {
                            stream = LivePlaybackStream.Start(PlaybackDeliveryCommand.Progressive(session.SourcePath, session.Plan, start, admitted.Encoder), admitted.Lease);
                            await stream.WaitForFirstBytesAsync(PlaybackDeliveryCommand.FirstOutputTimeout, cancellationToken);
                            return stream;
                        }
                        catch
                        {
                            stream?.Dispose();
                            admitted.Lease?.Dispose();
                            throw;
                        }
                    });
                return Results.File(live, "video/mp4", enableRangeProcessing: false);
            }
            catch (PlaybackAdmissionRefusedException refusal)
            {
                return Refused(refusal.Code);
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                return StartFailed();
            }
        })
        .RequireRateLimiting(RateLimitPolicy);

        group.MapGet("/stream-sessions/{sessionId:guid}/hls", async (
            Guid sessionId,
            double? startSeconds,
            PlaybackStreamSessionStore sessions,
            PlaybackAdmissionService admission,
            HlsPlaybackSessionManager manager,
            CurrentAccountContext currentAccount,
            CancellationToken cancellationToken) =>
        {
            var session = sessions.Get(sessionId, currentAccount.ProfileId);
            if (session is null)
            {
                return SessionNotFound();
            }

            if (session.Plan.Transport != PlaybackTransport.Hls)
            {
                return Results.BadRequest(new ClientErrorResponse(
                    "wrong_transport",
                    "This playback session is not delivered as HLS."));
            }

            if (!TryNormalizeStart(startSeconds, session.DurationSeconds, out var start))
            {
                return InvalidStart();
            }

            if (!File.Exists(session.SourcePath))
            {
                return Results.NotFound(new ClientErrorResponse(
                    "media_not_found",
                    "The media file of this playback session is unavailable."));
            }

            // Repeated requests for the same position reuse the running output; a new
            // position replaces it. Concurrent requests share one start.
            try
            {
                var hlsSessionId = await session.EnsureHlsAsync(
                    start,
                    running => manager.IsActive(running, session.ProfileId),
                    token => admission.StartAsync(
                        session.Plan,
                        session.ProfileId,
                        async admitted => (Guid?)(await manager.StartAsync(
                            session.EpisodeId,
                            session.ProfileId,
                            start,
                            directory => PlaybackDeliveryCommand.Hls(session.SourcePath, session.Plan, start, directory, admitted.Encoder),
                            admitted.Lease,
                            token)).SessionId),
                    previous => manager.Stop(previous, session.ProfileId),
                    cancellationToken);
                if (hlsSessionId is not { } started)
                {
                    return StartFailed();
                }

                return Results.Redirect(
                    ClientApiRoutes.StreamSessionHlsAsset(session.Id, started, "index.m3u8"),
                    permanent: false,
                    preserveMethod: false);
            }
            catch (PlaybackAdmissionRefusedException refusal)
            {
                return Refused(refusal.Code);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                return StartFailed();
            }
        })
        .RequireRateLimiting(RateLimitPolicy);

        group.MapGet("/stream-sessions/{sessionId:guid}/hls/{hlsSessionId:guid}/{fileName}", async (
            Guid sessionId,
            Guid hlsSessionId,
            string fileName,
            PlaybackStreamSessionStore sessions,
            HlsPlaybackSessionManager manager,
            CurrentAccountContext currentAccount,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var session = sessions.Get(sessionId, currentAccount.ProfileId);
            if (session is null || session.HlsSessionId != hlsSessionId)
            {
                return SessionNotFound();
            }

            var asset = manager.GetAsset(
                hlsSessionId,
                session.EpisodeId,
                currentAccount.ProfileId,
                fileName);
            if (asset is null)
            {
                return Results.NotFound(new ClientErrorResponse(
                    "hls_asset_not_found",
                    $"The HLS playback segment is unavailable or expired ({manager.EndReason(hlsSessionId) ?? "unknown"})."));
            }

            if (fileName == "index.m3u8")
            {
                string playlist;
                try
                {
                    playlist = await File.ReadAllTextAsync(asset.Path, cancellationToken);
                }
                catch (IOException)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                httpContext.Response.Headers.CacheControl = "no-store";
                return Results.Text(PlaybackDeliveryCommand.StartAtBeginning(playlist), asset.ContentType);
            }

            manager.PruneBehind(hlsSessionId, currentAccount.ProfileId, fileName);
            return Results.File(asset.Path, asset.ContentType, enableRangeProcessing: asset.EnableRangeProcessing);
        });

        // The player cannot read the HTTP status behind a failed video element, so it asks here whether the server ended
        // the session itself (idle, cache policy, encoder crash) and then re-plans with the same mode instead of blaming the mode.
        group.MapGet("/stream-sessions/{sessionId:guid}", (
            Guid sessionId,
            PlaybackStreamSessionStore sessions,
            HlsPlaybackSessionManager manager,
            HttpContext httpContext,
            CurrentAccountContext currentAccount) =>
        {
            var session = sessions.Get(sessionId, currentAccount.ProfileId);
            if (session is null)
            {
                return SessionNotFound();
            }

            var ended = session.HlsSessionId is { } hlsSessionId && !manager.IsActive(hlsSessionId, currentAccount.ProfileId);
            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new ClientStreamSessionStatus(ended ? "ended" : "active", ended ? manager.EndReason(session.HlsSessionId!.Value) : null));
        });

        group.MapDelete("/stream-sessions/{sessionId:guid}", async (
            Guid sessionId,
            PlaybackStreamSessionStore sessions,
            ActiveSessionService activeSessions,
            CurrentAccountContext currentAccount,
            CancellationToken cancellationToken) =>
        {
            if (!sessions.Remove(sessionId, currentAccount.ProfileId))
            {
                return SessionNotFound();
            }

            await activeSessions.EndAsync(sessionId, currentAccount.ProfileId, cancellationToken);
            return Results.NoContent();
        });

        return endpoints;
    }

    private static ClientCanonicalVideoPlayerBootstrap ToPlayerBootstrap(
        CanonicalVideoPlayerSnapshot snapshot)
    {
        var technical = snapshot.Inventory.Technical!;
        var audio = technical.AudioStreams
            .OrderBy(x => x.Index)
            .Select(ToClientTrack)
            .ToArray();
        var subtitles = technical.SubtitleStreams
            .OrderBy(x => x.Index)
            .Select(ToClientTrack)
            .ToArray();
        var playbackTracks = PlaybackProbeResult.From(technical).Tracks ?? [];
        var defaultAudio = technical.AudioStreams.FirstOrDefault(x => x.IsDefault)
            ?? technical.AudioStreams.FirstOrDefault();
        var defaultSubtitle = technical.SubtitleStreams.FirstOrDefault(x => x.IsDefault && !x.IsForced);
        var initialAudio = PlaybackTrackSelection.ResolveAudio(
            playbackTracks,
            snapshot.Preferences.PreferredAudioLanguage);
        var initialSubtitle = PlaybackTrackSelection.ResolveSubtitle(
            playbackTracks,
            hasLearningCues: false,
            snapshot.Preferences.PreferredSubtitleLanguage);
        var durationMs = technical.DurationSeconds is > 0 and < (long.MaxValue / 1000d)
            ? (long?)Math.Round(technical.DurationSeconds.Value * 1000d)
            : null;

        ClientVideoNavigationItem? NavigationItem(CanonicalVideoNavigationItem? item) =>
            item is null
                ? null
                : new ClientVideoNavigationItem(
                    new ClientVideoTarget(item.Target.WorkId, item.Target.WorkEpisodeId),
                    item.SeasonNumber,
                    item.EpisodeNumber,
                    item.Title);

        return new ClientCanonicalVideoPlayerBootstrap(
            ClientApiContract.ApiVersion,
            new ClientVideoTarget(snapshot.Target.WorkId, snapshot.Target.WorkEpisodeId),
            WorkMediaTypes.ToStorage(snapshot.MediaType),
            snapshot.WorkTitle,
            new ClientVideoNavigation(
                NavigationItem(snapshot.Navigation.Previous),
                NavigationItem(snapshot.Navigation.Next)),
            new ClientCanonicalVideoMedia(
                snapshot.File.StoredFileId,
                Path.GetFileName(snapshot.File.Path),
                PlaybackMediaTypes.GetContentType(snapshot.File.Path),
                snapshot.File.SizeBytes,
                durationMs,
                technical.Video?.Codec,
                technical.Video?.PixelFormat,
                defaultAudio?.Codec,
                ClientApiRoutes.DirectContent(snapshot.File.StoredFileId)),
            audio,
            subtitles,
            defaultAudio is null ? null : PlaybackTrackIds.Format(defaultAudio.Index),
            defaultSubtitle is null ? null : PlaybackTrackIds.Format(defaultSubtitle.Index),
            new ClientPlayerDefaults(
                initialAudio is null ? null : PlaybackTrackIds.Format(initialAudio.StreamIndex),
                initialSubtitle.ModeName,
                initialSubtitle.TrackId,
                snapshot.Preferences.DefaultPlaybackSpeed,
                ClientApiMappings.ToClientPreferences(snapshot.Preferences)),
            new ClientVideoProgressResponse(
                new ClientVideoTarget(snapshot.Progress.WorkId, snapshot.Progress.WorkEpisodeId),
                snapshot.Progress.PositionMs,
                snapshot.Progress.DurationMs,
                snapshot.Progress.Percent,
                snapshot.Progress.IsCompleted,
                snapshot.Progress.UpdatedAt,
                snapshot.Progress.ResumePositionMs),
            new ClientPlayerControls(PlaybackPreferenceRules.Speeds, PlaybackQuality.Names),
            ClientApiRoutes.VideoPlaybackPlan,
            ClientApiRoutes.VideoProgress,
            ClientApiMappings.ToClientSegments(snapshot.Segments),
            ToCanonicalTrickplay(snapshot.File.StoredFileId, snapshot.Trickplay));
    }

    private static ClientTrickplayDescriptor ToCanonicalTrickplay(
        Guid mediaFileId,
        TrickplayDescriptor descriptor)
    {
        var index = descriptor.IsReady ? descriptor.Index : null;
        var state = descriptor.State switch
        {
            TrickplayState.Ready when index is not null => "ready",
            TrickplayState.Queued or TrickplayState.Generating => "generating",
            _ => "unavailable"
        };

        return new ClientTrickplayDescriptor(
            state,
            descriptor.Message,
            TrickplayGenerator.GeneratorVersion,
            index?.IntervalMs,
            index?.TileWidth,
            index?.TileHeight,
            index?.Columns,
            index?.Rows,
            index?.ThumbnailCount,
            index is null
                ? []
                : index.Sprites
                    .Select(sprite =>
                        $"{ClientApiRoutes.MediaTrickplayAsset(mediaFileId, sprite)}?v={index.MediaIdentity[..Math.Min(16, index.MediaIdentity.Length)]}-{index.GeneratorVersion}")
                    .ToArray(),
            ClientApiRoutes.MediaTrickplay(mediaFileId));
    }

    private static ClientMediaTrack ToClientTrack(MediaStreamInfo track) =>
        new(
            PlaybackTrackIds.Format(track.Index),
            track.Index,
            track.Kind.ToString().ToLowerInvariant(),
            track.Codec,
            PlaybackLanguages.Normalize(track.Language),
            track.Title,
            track.IsDefault,
            track.IsForced,
            track.IsText);

    private static ClientPlaybackPlanResponse ToResponse(
        PlaybackPlanOutcome outcome,
        CurrentAccountContext currentAccount) =>
        new(
            outcome.Session?.Id,
            outcome.Plan,
            outcome.Session is { } session ? Delivery(session) : null,
            outcome.Availability is { } availability
                ? ClientApiMappings.ToClientAvailability(availability, currentAccount.IsOwner)
                : null,
            outcome.CapabilitiesInferred,
            new ClientVideoTarget(outcome.Target.WorkId, outcome.Target.WorkEpisodeId),
            outcome.ResumePositionMs);

    public static ClientPlaybackDelivery? Delivery(PlaybackStreamSession session) =>
        session.Plan.Transport switch
        {
            PlaybackTransport.File => new ClientPlaybackDelivery(
                ClientApiRoutes.DirectContent(session.MediaFileId),
                PlaybackTransport.File,
                null,
                SeekableWithinStream: true),
            PlaybackTransport.ProgressiveMp4 => new ClientPlaybackDelivery(
                ClientApiRoutes.StreamSessionStream(session.Id),
                PlaybackTransport.ProgressiveMp4,
                "startSeconds",
                SeekableWithinStream: false),
            PlaybackTransport.Hls => new ClientPlaybackDelivery(
                ClientApiRoutes.StreamSessionHls(session.Id),
                PlaybackTransport.Hls,
                "startSeconds",
                SeekableWithinStream: false),
            _ => null
        };

    private static bool TryParseInput(
        ClientPlaybackPlanRequest request,
        HttpContext httpContext,
        out PlaybackPlanInput input,
        out IResult? error)
    {
        input = null!;
        error = null;

        int? audio = null;
        if (!string.IsNullOrWhiteSpace(request.AudioTrackId))
        {
            if (!PlaybackTrackIds.TryParse(request.AudioTrackId.Trim(), out var index))
            {
                error = Results.BadRequest(new ClientErrorResponse("invalid_audio_track", "audioTrackId must be a stream:N track id."));
                return false;
            }

            audio = index;
        }

        int? subtitle = null;
        if (!string.IsNullOrWhiteSpace(request.SubtitleTrackId))
        {
            if (!PlaybackTrackIds.TryParse(request.SubtitleTrackId.Trim(), out var index))
            {
                error = Results.BadRequest(new ClientErrorResponse("invalid_subtitle_track", "subtitleTrackId must be a stream:N track id."));
                return false;
            }

            subtitle = index;
        }

        PlaybackQualityPreset? quality = null;
        if (!string.IsNullOrWhiteSpace(request.Quality))
        {
            if (!PlaybackQualityPresets.TryParse(request.Quality, out var preset))
            {
                error = Results.BadRequest(new ClientErrorResponse(
                    "invalid_quality",
                    $"quality must be one of {string.Join(", ", PlaybackQualityPresets.Names)}."));
                return false;
            }

            quality = preset;
        }

        if (!TryParseMode(request.Mode, out var mode))
        {
            error = Results.BadRequest(new ClientErrorResponse(
                "invalid_mode",
                "mode must be auto, direct_only or always_transcode."));
            return false;
        }

        var kind = request.Capabilities?.Client?.Kind ?? ClientKinds.Web;
        input = new PlaybackPlanInput(
            request.Capabilities,
            kind,
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.Connection.RemoteIpAddress,
            audio,
            subtitle,
            request.BurnInSubtitle,
            quality,
            mode,
            request.Network,
            request.FailedModes is { Count: > 0 } failed ? failed.Take(4).ToHashSet() : null,
            request.ReplacesSessionId,
            request.Wake);
        return true;
    }

    public static bool TryParseMode(string? value, out PlaybackModePreference mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "auto":
                mode = PlaybackModePreference.Auto;
                return true;
            case "direct_only" or "device":
                mode = PlaybackModePreference.DirectOnly;
                return true;
            case "always_transcode":
                mode = PlaybackModePreference.AlwaysTranscode;
                return true;
            default:
                mode = PlaybackModePreference.Auto;
                return false;
        }
    }

    private static bool TryNormalizeStart(double? requested, double? durationSeconds, out double start)
    {
        start = 0;
        if (requested is null)
        {
            return true;
        }

        if (!double.IsFinite(requested.Value) || requested.Value < 0)
        {
            return false;
        }

        start = durationSeconds is > 0 && double.IsFinite(durationSeconds.Value)
            ? Math.Min(requested.Value, Math.Max(0, durationSeconds.Value - 0.05))
            : requested.Value;
        return true;
    }

    private static IResult SessionNotFound() =>
        Results.NotFound(new ClientErrorResponse(
            "stream_session_not_found",
            "The playback session does not exist or expired; request a new playback plan."));

    private static IResult InvalidStart() =>
        Results.BadRequest(new ClientErrorResponse(
            "invalid_start_position",
            "startSeconds must be a finite value greater than or equal to zero."));

    internal static IResult Refused(string code) =>
        Results.Json(
            new ClientErrorResponse(code, PlaybackAdmissionCodes.Message(code)),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult StartFailed() =>
        Results.Json(
            new ClientErrorResponse("playback_start_failed", "The server could not start the playback stream."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
