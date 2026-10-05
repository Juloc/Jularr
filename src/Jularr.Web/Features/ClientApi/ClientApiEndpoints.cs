using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.MediaSegments;
using Jularr.Web.Features.Pairing;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;
using Jularr.Web.Features.PlaybackSessions;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Speech;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Watchlist;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace Jularr.Web.Features.ClientApi;

public static class ClientApiEndpoints
{
    public static IEndpointRouteBuilder MapClientApiV1(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup(ClientApiContract.BasePath)
            .RequireAuthorization()
            .AddEndpointFilter<ClientVideoAccessFilter>();

        group.MapGet("/capabilities", async (HttpContext context) =>
            {
                var moduleService =
                    context.RequestServices.GetService<IInstanceModuleService>();
                var settings = moduleService is null
                    ? InstanceModuleSettings.Default
                    : await moduleService.GetAsync(context.RequestAborted);
                return Results.Ok(ClientApiContract.Capabilities(settings));
            })
            .AllowAnonymous();

        group.MapPost("/session/login", async (
            ClientLoginRequest request,
            OwnerAuthService ownerAuth,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!await ownerAuth.HasOwnerAsync(cancellationToken))
            {
                return Results.Json(
                    new ClientErrorResponse(
                        "setup_required",
                        "Jularr must be set up in the browser before a native client can sign in."),
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (string.IsNullOrWhiteSpace(request.UserName) ||
                string.IsNullOrEmpty(request.Password))
            {
                return BadRequest(
                    "invalid_credentials",
                    "User name and password are required.");
            }

            var account = await ownerAuth.ValidateCredentialsAsync(
                request.UserName,
                request.Password,
                cancellationToken);

            if (account is null)
            {
                return Results.Json(
                    new ClientErrorResponse(
                        "invalid_credentials",
                        "Invalid user name or password."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var properties = new AuthenticationProperties
            {
                IsPersistent = request.RememberMe,
                AllowRefresh = true
            };

            if (request.RememberMe)
            {
                properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30);
            }

            await httpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                OwnerAuthService.CreatePrincipal(account),
                properties);

            return Results.Ok(new ClientAccountResponse(
                account.Id,
                account.UserName,
                account.Role == AccountRole.Owner ? "owner" : "user"));
        })
        .AllowAnonymous()
        .RequireRateLimiting("login");

        group.MapPost("/session/logout", async (HttpContext httpContext) =>
        {
            await httpContext.SignOutAsync(
                CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        group.MapGet("/me", (
            HttpContext httpContext,
            CurrentAccountContext account) =>
        {
            var response = new ClientAccountResponse(
                account.ProfileId,
                httpContext.User.Identity?.Name,
                account.IsOwner ? "owner" : "user");
            return Results.Ok(response);
        });

        group.MapGet("/library", async (
            ClientApiService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetLibraryAsync(cancellationToken)));

        group.MapGet("/anime/{animeId:guid}", async (
            Guid animeId,
            ClientApiService service,
            CancellationToken cancellationToken) =>
        {
            var anime = await service.GetAnimeAsync(animeId, cancellationToken);
            return anime is null
                ? NotFound("anime_not_found", "The requested anime does not exist.")
                : Results.Ok(anime);
        });

        group.MapGet("/episodes/{episodeId:guid}", async (
            Guid episodeId,
            ClientApiService service,
            CancellationToken cancellationToken) =>
        {
            var episode = await service.GetEpisodeAsync(
                episodeId,
                cancellationToken);
            return episode is null
                ? NotFound("episode_not_found", "The requested episode does not exist.")
                : Results.Ok(episode);
        });

        group.MapGet("/episodes/{episodeId:guid}/progress", async (
            Guid episodeId,
            EpisodeProgressService progressService,
            CancellationToken cancellationToken) =>
        {
            var progress = await progressService.GetAsync(
                episodeId,
                cancellationToken);

            return progress is null
                ? NotFound("episode_not_found", "The requested episode does not exist.")
                : Results.Ok(ClientApiMappings.ToClientEpisodeProgress(progress));
        });

        group.MapPut("/episodes/{episodeId:guid}/progress", async (
            Guid episodeId,
            ClientEpisodeProgressUpdate update,
            EpisodeProgressService progressService,
            CancellationToken cancellationToken) =>
        {
            if (update.PositionMs < 0 || update.DurationMs is < 0)
            {
                return BadRequest(
                    "invalid_progress",
                    "Playback progress values must be zero or greater.");
            }

            var progress = await progressService.UpdateAsync(
                episodeId,
                new EpisodeProgressUpdate(
                    update.PositionMs,
                    update.DurationMs,
                    update.Completed),
                cancellationToken);

            return progress is null
                ? NotFound("episode_not_found", "The requested episode does not exist.")
                : Results.Ok(ClientApiMappings.ToClientEpisodeProgress(progress));
        });

        group.MapPut("/episodes/{episodeId:guid}/watched", async (
            Guid episodeId,
            ClientEpisodeWatchedUpdate update,
            EpisodeProgressService progressService,
            CancellationToken cancellationToken) =>
        {
            var progress = await progressService.SetWatchedAsync(
                episodeId,
                update.Watched,
                cancellationToken);

            return progress is null
                ? NotFound("episode_not_found", "The requested episode does not exist.")
                : Results.Ok(ClientApiMappings.ToClientEpisodeProgress(progress));
        });

        group.MapGet("/episodes/{episodeId:guid}/flow", async (
            Guid episodeId,
            EpisodeProgressService progressService,
            CancellationToken cancellationToken) =>
        {
            var flow = await progressService.GetFlowAsync(
                episodeId,
                cancellationToken);

            return flow is null
                ? NotFound("episode_not_found", "The requested episode does not exist.")
                : Results.Ok(ClientApiMappings.ToClientEpisodeFlow(flow));
        });

        group.MapGet("/continue-watching", async (
            EpisodeProgressService progressService,
            CancellationToken cancellationToken) =>
        {
            var items = await progressService.GetContinueWatchingAsync(
                cancellationToken: cancellationToken);

            return Results.Ok(new ClientContinueWatchingResponse(
                items.Select(ClientApiMappings.ToClientContinueWatchingItem).ToArray()));
        });

        group.MapGet("/watchlist", async (
            WatchlistStore watchlist,
            WatchlistLibraryResolver library,
            CurrentAccountContext currentAccount,
            CancellationToken cancellationToken) =>
        {
            var items = await library.ApplyAsync(
                await watchlist.GetEffectiveAsync(currentAccount.ProfileId, cancellationToken),
                cancellationToken);

            return Results.Ok(new ClientWatchlistResponse(
                items.Select(ClientApiMappings.ToClientWatchlistItem).ToArray()));
        });

        group.MapGet("/me/playback-preferences", async (
            EpisodeProgressService progressService,
            CancellationToken cancellationToken) =>
        {
            var preferences = await progressService.GetPreferencesAsync(
                cancellationToken);
            return Results.Ok(ClientApiMappings.ToClientPreferences(preferences));
        });

        group.MapPut("/me/playback-preferences", async (
            ClientPlaybackPreferencesUpdate update,
            EpisodeProgressService progressService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var preferences = await progressService.UpdatePreferencesAsync(
                    new PlaybackPreferencesUpdate(
                        update.AutoplayNext,
                        update.PreferredAudioLanguage,
                        update.PreferredSubtitleLanguage,
                        update.DefaultPlaybackSpeed),
                    cancellationToken);
                return Results.Ok(ClientApiMappings.ToClientPreferences(preferences));
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return exception.ParamName switch
                {
                    "speed" => BadRequest(
                        "invalid_playback_speed",
                        $"defaultPlaybackSpeed must be one of {PlaybackPreferenceRules.SpeedList}."),
                    "audioLanguage" => BadRequest(
                        "invalid_audio_language",
                        "preferredAudioLanguage must be an ISO 639 language tag or empty."),
                    _ => BadRequest(
                        "invalid_subtitle_language",
                        "preferredSubtitleLanguage must be an ISO 639 language tag, off, or empty.")
                };
            }
        });

        group.MapGet("/me/playback-history", async (
            EpisodeProgressService progressService,
            CancellationToken cancellationToken) =>
        {
            var items = await progressService.GetHistoryAsync(cancellationToken);
            return Results.Ok(new ClientPlaybackHistoryResponse(
                EpisodeProgressService.HistoryLimit,
                items.Select(ClientApiMappings.ToClientPlaybackHistoryItem).ToArray()));
        });

        group.MapDelete("/me/playback-history", async (
            EpisodeProgressService progressService,
            CancellationToken cancellationToken) =>
        {
            await progressService.ClearHistoryAsync(cancellationToken);
            return Results.NoContent();
        });

        group.MapGet("/me/tts-preferences", async (
            TtsPreferencesService ttsPreferences,
            CancellationToken cancellationToken) =>
        {
            var preferences = await ttsPreferences.GetAsync(cancellationToken);
            return Results.Ok(ClientApiMappings.ToClientTtsPreferences(preferences));
        });

        group.MapPut("/me/tts-preferences", async (
            ClientTtsPreferencesUpdate update,
            TtsPreferencesService ttsPreferences,
            CancellationToken cancellationToken) =>
        {
            var preferences = await ttsPreferences.UpdateAsync(
                new TtsPreferencesUpdate(
                    update.ProviderId,
                    update.Rate,
                    update.Pitch,
                    update.Volume,
                    update.VoiceLanguage,
                    update.VoiceId),
                cancellationToken);
            return Results.Ok(ClientApiMappings.ToClientTtsPreferences(preferences));
        });

        group.MapGet("/speech/models", async (
            SpeechModelManifestStore manifestStore,
            CancellationToken cancellationToken) =>
        {
            var manifest = await manifestStore.LoadAsync(cancellationToken);
            return Results.Ok(new ClientSpeechModelsResponse(
                manifest.Entries.Select(ClientApiMappings.ToClientSpeechModel).ToArray()));
        });

        group.MapGet("/episodes/{episodeId:guid}/player", async (
            Guid episodeId,
            ClientApiService service,
            CancellationToken cancellationToken) =>
        {
            var player = await service.GetPlayerAsync(
                episodeId,
                cancellationToken);
            return player is null
                ? NotFound("episode_not_found", "The requested episode does not exist.")
                : Results.Ok(player);
        });

        group.MapGet("/episodes/{episodeId:guid}/cues", async (
            Guid episodeId,
            Guid? trackId,
            int? fromMs,
            int? toMs,
            ClientApiService service,
            CancellationToken cancellationToken) =>
        {
            if (fromMs is < 0 || toMs is < 0)
            {
                return BadRequest(
                    "invalid_time_range",
                    "fromMs and toMs must be zero or greater.");
            }

            if (fromMs.HasValue &&
                toMs.HasValue &&
                fromMs.Value > toMs.Value)
            {
                return BadRequest(
                    "invalid_time_range",
                    "fromMs must be less than or equal to toMs.");
            }

            var cues = await service.GetCuesAsync(
                episodeId,
                trackId,
                fromMs,
                toMs,
                cancellationToken);

            if (cues is null)
            {
                return NotFound(
                    "episode_not_found",
                    "The requested episode does not exist.");
            }

            if (trackId.HasValue && cues.TrackId is null)
            {
                return NotFound(
                    "subtitle_track_not_found",
                    "The requested Japanese learning subtitle track does not exist for this episode.");
            }

            return Results.Ok(cues);
        });

        group.MapGet("/episodes/{episodeId:guid}/subtitle-tracks/{trackId}/cues", async (
            Guid episodeId,
            string trackId,
            PlaybackService playbackService,
            CancellationToken cancellationToken) =>
        {
            if (!PlaybackTrackIds.TryParse(trackId, out _))
            {
                return BadRequest(
                    "invalid_track_id",
                    "trackId must be a canonical stream:{index} track id.");
            }

            var cues = await playbackService.GetEmbeddedSubtitleCuesAsync(
                episodeId,
                trackId,
                cancellationToken);

            return cues is null
                ? NotFound(
                    "subtitle_track_not_found",
                    "The requested embedded text subtitle stream is unavailable.")
                : Results.Ok(ClientApiMappings.ToClientEmbeddedSubtitleCues(cues));
        });

        group.MapGet("/episodes/{episodeId:guid}/segments", async (
            Guid episodeId,
            ClientApiService service,
            CancellationToken cancellationToken) =>
        {
            var segments = await service.GetSegmentsAsync(episodeId, cancellationToken);
            return segments is null
                ? NotFound("episode_not_found", "The requested episode does not exist.")
                : Results.Ok(segments);
        });

        group.MapGet("/episodes/{episodeId:guid}/trickplay", async (
            Guid episodeId,
            ClientApiService service,
            CancellationToken cancellationToken) =>
        {
            var trickplay = await service.GetTrickplayAsync(episodeId, cancellationToken);
            return trickplay is null
                ? NotFound("episode_not_found", "The requested episode does not exist.")
                : Results.Ok(trickplay);
        });

        group.MapGet("/episodes/{episodeId:guid}/trickplay/{fileName}", async (
            Guid episodeId,
            string fileName,
            MediaSegmentService mediaSegments,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var asset = await mediaSegments.GetTrickplayAssetAsync(
                episodeId,
                fileName,
                cancellationToken);

            if (asset is null)
            {
                return NotFound(
                    "trickplay_asset_not_found",
                    "The requested seek preview asset is not available.");
            }

            // Assets are immutable per media identity and generator version.
            httpContext.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(asset.Path, asset.ContentType);
        });

        group.MapGet("/media/{mediaFileId:guid}/availability", async (
            Guid mediaFileId,
            bool fresh,
            bool? wake,
            MediaAvailabilityService mediaAvailability,
            CurrentAccountContext currentAccount,
            CancellationToken cancellationToken) =>
        {
            // wake=true: the client is about to play or open the media, so sleeping storage
            // is started; plain checks never wake it.
            var availability = await mediaAvailability.CheckMediaAsync(
                mediaFileId,
                force: fresh,
                cancellationToken,
                wake: wake == true);

            return availability is null
                ? NotFound("media_not_found", "The requested media file does not exist.")
                : Results.Ok(ClientApiMappings.ToClientAvailability(
                    availability,
                    currentAccount.IsOwner));
        });

        group.MapGet("/media/{mediaFileId:guid}/content", async (
            Guid mediaFileId,
            PlaybackService playbackService,
            MediaAvailabilityService mediaAvailability,
            CurrentAccountContext currentAccount,
            CancellationToken cancellationToken) =>
        {
            var availability = await mediaAvailability.CheckMediaAsync(
                mediaFileId,
                force: false,
                cancellationToken,
                wake: true);

            if (availability is null)
            {
                return NotFound(
                    "media_not_found",
                    "The requested media file does not exist.");
            }

            if (!availability.IsAvailable)
            {
                return Results.Json(
                    ClientApiMappings.ToClientAvailability(
                        availability,
                        currentAccount.IsOwner),
                    statusCode: availability.State == StorageAvailabilityState.FileMissing
                        ? StatusCodes.Status404NotFound
                        : StatusCodes.Status503ServiceUnavailable);
            }

            var stream = await playbackService.GetOriginalContentAsync(
                mediaFileId,
                cancellationToken);

            if (stream is null)
            {
                return NotFound(
                    "media_not_found",
                    "The requested media file is unavailable.");
            }

            return Results.File(
                stream.SourcePath,
                stream.ContentType,
                lastModified: stream.LastModified,
                enableRangeProcessing: true);
        });

        group.MapGet("/episodes/{episodeId:guid}/hls", async (
            Guid episodeId,
            double? startSeconds,
            string? audioTrackId,
            string? quality,
            PlaybackService playbackService,
            MediaAvailabilityService mediaAvailability,
            HlsPlaybackSessionManager hlsSessions,
            PlaybackAdmissionService admission,
            CurrentAccountContext currentAccount,
            CancellationToken cancellationToken) =>
        {
            if (startSeconds.HasValue &&
                (!double.IsFinite(startSeconds.Value) || startSeconds.Value < 0))
            {
                return BadRequest(
                    "invalid_start_position",
                    "startSeconds must be a finite value greater than or equal to zero.");
            }

            if (!PlaybackQuality.TryParse(quality, out var qualityCap))
            {
                return BadRequest(
                    "invalid_quality_cap",
                    $"quality must be one of {string.Join(", ", PlaybackQuality.Names)}.");
            }

            var availability = await mediaAvailability.CheckEpisodeAsync(
                episodeId,
                force: false,
                cancellationToken,
                wake: true);

            if (availability is null)
            {
                return NotFound(
                    "media_not_found",
                    "This episode does not have a media file.");
            }

            if (!availability.IsAvailable)
            {
                return Results.Json(
                    ClientApiMappings.ToClientAvailability(
                        availability,
                        currentAccount.IsOwner),
                    statusCode: availability.State == StorageAvailabilityState.FileMissing
                        ? StatusCodes.Status404NotFound
                        : StatusCodes.Status503ServiceUnavailable);
            }

            var stream = await playbackService.GetStreamAsync(
                episodeId,
                new PlaybackStreamRequest(
                    PlaybackRequestedMode.Server,
                    NormalizeTrackId(audioTrackId),
                    qualityCap),
                cancellationToken);

            if (stream is null || !File.Exists(stream.SourcePath))
            {
                return NotFound(
                    "playback_unavailable",
                    "No source media is available for HLS fallback with the requested audio track.");
            }

            // This compatibility fallback always encodes with libx264, so it is a software video delivery.
            var admitted = admission.AdmitLegacy(PlaybackCostClass.SoftwareVideo, currentAccount.ProfileId);
            if (!admitted.Admitted)
            {
                return ClientApiPlaybackPlanEndpoints.Refused(admitted.RefusalCode!);
            }

            try
            {
                var start = NormalizeStart(
                    startSeconds,
                    stream.DurationSeconds);
                var session = await hlsSessions.StartAsync(
                    episodeId,
                    currentAccount.ProfileId,
                    stream.SourcePath,
                    start,
                    cancellationToken,
                    stream.AudioStreamIndex,
                    stream.QualityCap,
                    admitted.Lease);

                return Results.Redirect(
                    ClientApiRoutes.HlsPlaylist(
                        episodeId,
                        session.SessionId),
                    permanent: false,
                    preserveMethod: false);
            }
            catch (PlaybackAdmissionRefusedException refusal)
            {
                return ClientApiPlaybackPlanEndpoints.Refused(refusal.Code);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                TimeoutException or
                System.ComponentModel.Win32Exception)
            {
                return Results.Json(
                    new ClientErrorResponse(
                        "hls_start_failed",
                        "The server could not start the seekable compatibility stream."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet(
            "/episodes/{episodeId:guid}/hls/{sessionId:guid}/{fileName}",
            (
                Guid episodeId,
                Guid sessionId,
                string fileName,
                HlsPlaybackSessionManager hlsSessions,
                CurrentAccountContext currentAccount) =>
            {
                var asset = hlsSessions.GetAsset(
                    sessionId,
                    episodeId,
                    currentAccount.ProfileId,
                    fileName);

                return asset is null
                    ? NotFound(
                        "hls_asset_not_found",
                        "The HLS playback segment is unavailable or expired.")
                    : Results.File(
                        asset.Path,
                        asset.ContentType,
                        enableRangeProcessing: asset.EnableRangeProcessing);
            });

        group.MapGet("/episodes/{episodeId:guid}/fallback", async (
            Guid episodeId,
            string? mode,
            double? startSeconds,
            string? audioTrackId,
            string? quality,
            PlaybackService playbackService,
            MediaAvailabilityService mediaAvailability,
            PlaybackAdmissionService admission,
            CurrentAccountContext currentAccount,
            CancellationToken cancellationToken) =>
        {
            if (startSeconds.HasValue &&
                (!double.IsFinite(startSeconds.Value) || startSeconds.Value < 0))
            {
                return BadRequest(
                    "invalid_start_position",
                    "startSeconds must be a finite value greater than or equal to zero.");
            }

            if (!PlaybackQuality.TryParse(quality, out var qualityCap))
            {
                return BadRequest(
                    "invalid_quality_cap",
                    $"quality must be one of {string.Join(", ", PlaybackQuality.Names)}.");
            }

            var requestedMode = string.Equals(
                mode,
                "device",
                StringComparison.OrdinalIgnoreCase)
                ? PlaybackRequestedMode.Device
                : PlaybackRequestedMode.Server;

            var availability = await mediaAvailability.CheckEpisodeAsync(
                episodeId,
                force: false,
                cancellationToken,
                wake: true);

            if (availability is null)
            {
                return NotFound(
                    "media_not_found",
                    "This episode does not have a media file.");
            }

            if (!availability.IsAvailable)
            {
                return Results.Json(
                    ClientApiMappings.ToClientAvailability(
                        availability,
                        currentAccount.IsOwner),
                    statusCode: availability.State == StorageAvailabilityState.FileMissing
                        ? StatusCodes.Status404NotFound
                        : StatusCodes.Status503ServiceUnavailable);
            }

            var stream = await playbackService.GetStreamAsync(
                episodeId,
                new PlaybackStreamRequest(
                    requestedMode,
                    NormalizeTrackId(audioTrackId),
                    qualityCap),
                cancellationToken);

            if (stream is null || !File.Exists(stream.SourcePath))
            {
                return NotFound(
                    "playback_unavailable",
                    "No compatible playback stream is available for this episode and audio track.");
            }

            if (!stream.IsLive)
            {
                return Results.File(
                    stream.SourcePath,
                    stream.ContentType,
                    lastModified: stream.LastModified,
                    enableRangeProcessing: true);
            }

            // A copy is a remux; anything else is a libx264 software transcode. Both obey the Admin limits.
            var admitted = admission.AdmitLegacy(
                stream.LivePlan!.VideoMode == PlaybackVideoMode.Copy ? PlaybackCostClass.Remux : PlaybackCostClass.SoftwareVideo,
                currentAccount.ProfileId);
            if (!admitted.Admitted)
            {
                return ClientApiPlaybackPlanEndpoints.Refused(admitted.RefusalCode!);
            }

            try
            {
                var start = NormalizeStart(startSeconds, stream.DurationSeconds);
                var live = LivePlaybackStream.Start(stream.SourcePath, stream.LivePlan!, start, stream.AudioStreamIndex, stream.QualityCap, admitted.Lease);

                return Results.File(
                    live,
                    stream.ContentType,
                    enableRangeProcessing: false);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                admitted.Lease?.Dispose();
                return Results.Json(
                    new ClientErrorResponse(
                        "playback_start_failed",
                        "The server could not start the compatibility stream."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch
            {
                admitted.Lease?.Dispose();
                throw;
            }
        });

        group.MapGet("/library-roots/{rootId:guid}/availability", async (
            Guid rootId,
            LibraryRootAvailabilityService availability,
            CancellationToken cancellationToken) =>
        {
            var status = await availability.CheckAsync(
                rootId,
                force: false,
                cancellationToken);

            return status is null
                ? NotFound("library_root_not_found", "The requested library root does not exist.")
                : Results.Ok(ClientApiMappings.ToClientRootAvailability(status));
        })
        .RequireAuthorization(new AuthorizeAttribute { Roles = AccountRoles.Owner });

        group.MapPost("/library-roots/{rootId:guid}/test", async (
            Guid rootId,
            LibraryRootAvailabilityService availability,
            CancellationToken cancellationToken) =>
        {
            var status = await availability.CheckAsync(
                rootId,
                force: true,
                cancellationToken);

            return status is null
                ? NotFound("library_root_not_found", "The requested library root does not exist.")
                : Results.Ok(ClientApiMappings.ToClientRootAvailability(status));
        })
        .RequireAuthorization(new AuthorizeAttribute { Roles = AccountRoles.Owner });

        group.MapPost("/library-roots/{rootId:guid}/wake", async (
            Guid rootId,
            WakeOnLanService wakeOnLan,
            CancellationToken cancellationToken) =>
        {
            var result = await wakeOnLan.WakeAsync(
                rootId,
                cancellationToken);

            if (result.Availability is null)
            {
                return NotFound(
                    "library_root_not_found",
                    "The requested library root does not exist.");
            }

            if (!result.Accepted)
            {
                return Results.Conflict(new ClientErrorResponse(
                    "wake_unavailable",
                    result.Message));
            }

            return Results.Ok(ClientApiMappings.ToClientRootAvailability(
                result.Availability));
        })
        .RequireAuthorization(new AuthorizeAttribute { Roles = AccountRoles.Owner })
        .RequireRateLimiting("wake");

        group.MapGet("/terms/{termId:guid}", async (
            Guid termId,
            ClientApiService service,
            CancellationToken cancellationToken) =>
        {
            var term = await service.GetTermAsync(termId, cancellationToken);
            return term is null
                ? NotFound("term_not_found", "The requested term does not exist.")
                : Results.Ok(term);
        });

        group.MapPut("/terms/{termId:guid}/state", async (
            Guid termId,
            ClientTermStateUpdate update,
            ClientApiService service,
            CancellationToken cancellationToken) =>
        {
            UserTermState state;
            if (string.Equals(update.State, "known", StringComparison.OrdinalIgnoreCase))
            {
                state = UserTermState.Known;
            }
            else if (string.Equals(update.State, "learning", StringComparison.OrdinalIgnoreCase))
            {
                state = UserTermState.Learning;
            }
            else
            {
                return BadRequest(
                    "invalid_learning_state",
                    "state must be either 'known' or 'learning'.");
            }

            var result = await service.SetTermStateAsync(
                termId,
                state,
                cancellationToken);

            return result is null
                ? NotFound("term_not_found", "The requested term does not exist.")
                : Results.Ok(result);
        });

        endpoints.MapPlaybackSessionApiV1();
        endpoints.MapPairingApiV1();
        return endpoints;
    }

    private static IResult NotFound(string code, string message) =>
        Results.NotFound(new ClientErrorResponse(code, message));

    private static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new ClientErrorResponse(code, message));

    private static string? NormalizeTrackId(string? trackId) =>
        string.IsNullOrWhiteSpace(trackId) ? null : trackId.Trim();

    private static double NormalizeStart(
        double? requested,
        double? durationSeconds)
    {
        if (requested is null || requested.Value <= 0)
        {
            return 0;
        }

        if (durationSeconds is > 0 && double.IsFinite(durationSeconds.Value))
        {
            return Math.Min(
                requested.Value,
                Math.Max(0, durationSeconds.Value - 0.05));
        }

        return requested.Value;
    }
}
