using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Progress;

namespace Jularr.Web.Features.ClientApi;

public sealed record ClientVideoTarget(
    Guid WorkId,
    Guid? WorkEpisodeId);

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

public static class ClientApiPlaybackPlanEndpoints
{
    public const string RateLimitPolicy = PlaybackDecisionRegistration.RateLimitPolicy;

    public static IEndpointRouteBuilder MapClientApiPlaybackPlanV1(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup(ClientApiContract.BasePath)
            .RequireAuthorization();

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

        group.MapPost("/video/playback-plan", async (
            ClientPlaybackPlanRequest request,
            PlaybackPlanService plans,
            CurrentAccountContext currentAccount,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (request.Target is not { } target || !ValidTarget(target))
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
            if (!ValidTarget(update.Target) ||
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

        group.MapGet("/stream-sessions/{sessionId:guid}/stream", (
            Guid sessionId,
            double? startSeconds,
            PlaybackStreamSessionStore sessions,
            PlaybackTranscodeSlots slots,
            CurrentAccountContext currentAccount) =>
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

            IDisposable? lease = null;
            if (session.Plan.TranscodesVideo && (lease = slots.TryAcquire()) is null)
            {
                return TranscoderBusy();
            }

            try
            {
                var live = LivePlaybackStream.Start(
                    PlaybackDeliveryCommand.Progressive(session.SourcePath, session.Plan, start),
                    lease);
                return Results.File(live, "video/mp4", enableRangeProcessing: false);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                lease?.Dispose();
                return StartFailed();
            }
        })
        .RequireRateLimiting(RateLimitPolicy);

        group.MapGet("/stream-sessions/{sessionId:guid}/hls", async (
            Guid sessionId,
            double? startSeconds,
            PlaybackStreamSessionStore sessions,
            PlaybackTranscodeSlots slots,
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
            var manager = HlsPlaybackSessionManager.Shared;
            try
            {
                var hlsSessionId = await session.EnsureHlsAsync(
                    start,
                    running => manager.IsActive(running, session.ProfileId),
                    async token =>
                    {
                        IDisposable? lease = null;
                        if (session.Plan.TranscodesVideo && (lease = slots.TryAcquire()) is null)
                        {
                            return null;
                        }

                        var hls = await manager.StartAsync(
                            session.EpisodeId,
                            session.ProfileId,
                            start,
                            directory => PlaybackDeliveryCommand.Hls(session.SourcePath, session.Plan, start, directory),
                            lease,
                            token);
                        return hls.SessionId;
                    },
                    previous => manager.Stop(previous, session.ProfileId),
                    cancellationToken);
                if (hlsSessionId is not { } started)
                {
                    return TranscoderBusy();
                }

                return Results.Redirect(
                    ClientApiRoutes.StreamSessionHlsAsset(session.Id, started, "index.m3u8"),
                    permanent: false,
                    preserveMethod: false);
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
            CurrentAccountContext currentAccount,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var session = sessions.Get(sessionId, currentAccount.ProfileId);
            if (session is null || session.HlsSessionId != hlsSessionId)
            {
                return SessionNotFound();
            }

            var asset = HlsPlaybackSessionManager.Shared.GetAsset(
                hlsSessionId,
                session.EpisodeId,
                currentAccount.ProfileId,
                fileName);
            if (asset is null)
            {
                return Results.NotFound(new ClientErrorResponse(
                    "hls_asset_not_found",
                    "The HLS playback segment is unavailable or expired."));
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

            HlsPlaybackSessionManager.Shared.PruneBehind(hlsSessionId, currentAccount.ProfileId, fileName);
            return Results.File(asset.Path, asset.ContentType, enableRangeProcessing: asset.EnableRangeProcessing);
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

    private static bool ValidTarget(ClientVideoTarget? target) =>
        target is not null &&
        target.WorkId != Guid.Empty &&
        (target.WorkEpisodeId is null || target.WorkEpisodeId != Guid.Empty);

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

    private static IResult TranscoderBusy() =>
        Results.Json(
            new ClientErrorResponse("transcoder_busy", "Every server transcode slot is in use."),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult StartFailed() =>
        Results.Json(
            new ClientErrorResponse("playback_start_failed", "The server could not start the playback stream."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
