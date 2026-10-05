using System.Reflection;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.MediaSegments;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Speech;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Watchlist;

namespace Jularr.Web.Features.ClientApi;

public static class ClientApiContract
{
    /// <summary>
    /// Version 2: playback checkpoints (<c>PUT episodes/{id}/progress</c>, <c>PUT video/progress</c>, offline progress
    /// replay) carry a client-declared <c>completed</c> flag and the server no longer infers completion from a
    /// position. A version 1 client never declares threshold completion, so it must update.
    /// </summary>
    public const int ApiVersion = 2;

    public const int MinimumSupportedApiVersion = 2;
    public const string BasePath = "/api/client/v1";

    public static ClientCapabilitiesResponse Capabilities(
        InstanceModuleSettings? instanceSettings = null)
    {
        var learningEnabled =
            instanceSettings?.IsEnabled(InstanceModule.Learning) ?? true;

        var assembly = typeof(ClientApiContract).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString() ?? "unknown"
            : informational.Split('+', 2)[0];

        return new ClientCapabilitiesResponse(
            ApiVersion,
            MinimumSupportedApiVersion,
            version,
            new ClientFeatureFlags(
                Library: true,
                NativeSessionAuth: true,
                NativePlayerBootstrap: true,
                DirectPlayback: true,
                PlaybackProgress: true,
                HttpRangeRequests: true,
                MediaTrackMetadata: true,
                NormalizedLearningCues: learningEnabled,
                LearningStateMutation: learningEnabled,
                LiveMp4Fallback: true,
                HlsFallback: true,
                PlaybackSessions: true,
                CompanionPairing: true,
                CompanionControl: true,
                StorageAvailability: true,
                OwnerWakeOnLan: true,
                EpisodeFlow: true,
                ContinueWatching: true,
                PlaybackHistory: true,
                PlaybackPreferences: true,
                EmbeddedSubtitleCues: true,
                OfflineDownloads: true,
                MediaSegments: true,
                Trickplay: true,
                OfflineLibrary: true,
                TtsPreferences: true,
                PlaybackPlan: true,
                Watchlist: true,
                DevicePairing: true,
                OfflinePackages: true));
    }
}

public static class ClientApiRoutes
{
    public static bool IsClientApi(PathString path) =>
        path.StartsWithSegments(
            new PathString(ClientApiContract.BasePath),
            StringComparison.OrdinalIgnoreCase);

    public static string Login => $"{ClientApiContract.BasePath}/session/login";

    public static string Logout => $"{ClientApiContract.BasePath}/session/logout";

    public static string Anime(Guid animeId) =>
        $"{ClientApiContract.BasePath}/anime/{animeId:D}";

    public static string Episode(Guid episodeId) =>
        $"{ClientApiContract.BasePath}/episodes/{episodeId:D}";

    public static string Player(Guid episodeId) =>
        $"{Episode(episodeId)}/player";

    public static string Progress(Guid episodeId) =>
        $"{Episode(episodeId)}/progress";

    public static string Watched(Guid episodeId) =>
        $"{Episode(episodeId)}/watched";

    public static string Flow(Guid episodeId) =>
        $"{Episode(episodeId)}/flow";

    public static string ContinueWatching =>
        $"{ClientApiContract.BasePath}/continue-watching";

    public static string PlaybackPreferences =>
        $"{ClientApiContract.BasePath}/me/playback-preferences";

    public static string PlaybackHistory =>
        $"{ClientApiContract.BasePath}/me/playback-history";

    public static string Watchlist =>
        $"{ClientApiContract.BasePath}/watchlist";

    public static string TtsPreferences =>
        $"{ClientApiContract.BasePath}/me/tts-preferences";

    public static string SpeechModels =>
        $"{ClientApiContract.BasePath}/speech/models";

    public static string Cues(Guid episodeId) =>
        $"{Episode(episodeId)}/cues";

    public static string SubtitleTrackCues(Guid episodeId, string trackId) =>
        $"{Episode(episodeId)}/subtitle-tracks/{Uri.EscapeDataString(trackId)}/cues";

    public static string Segments(Guid episodeId) =>
        $"{Episode(episodeId)}/segments";

    public static string Trickplay(Guid episodeId) =>
        $"{Episode(episodeId)}/trickplay";

    public static string TrickplayAsset(Guid episodeId, string fileName) =>
        $"{Trickplay(episodeId)}/{fileName}";

    public static string DirectContent(Guid mediaFileId) =>
        $"{ClientApiContract.BasePath}/media/{mediaFileId:D}/content";

    public static string MediaTrickplay(Guid mediaFileId) =>
        $"{ClientApiContract.BasePath}/media/{mediaFileId:D}/trickplay";

    public static string MediaTrickplayAsset(Guid mediaFileId, string fileName) =>
        $"{MediaTrickplay(mediaFileId)}/{Uri.EscapeDataString(fileName)}";

    public static string MediaAvailability(Guid mediaFileId) =>
        $"{ClientApiContract.BasePath}/media/{mediaFileId:D}/availability";

    public static string RootAvailability(Guid rootId) =>
        $"{ClientApiContract.BasePath}/library-roots/{rootId:D}/availability";

    public static string WakeRoot(Guid rootId) =>
        $"{ClientApiContract.BasePath}/library-roots/{rootId:D}/wake";

    public static string Fallback(Guid episodeId) =>
        $"{Episode(episodeId)}/fallback";

    public static string Hls(Guid episodeId) =>
        $"{Episode(episodeId)}/hls";

    public static string HlsPlaylist(
        Guid episodeId,
        Guid sessionId) =>
        $"{Hls(episodeId)}/{sessionId:D}/index.m3u8";

    public static string HlsAsset(
        Guid episodeId,
        Guid sessionId,
        string fileName) =>
        $"{Hls(episodeId)}/{sessionId:D}/{fileName}";

    public static string PlaybackPlan(Guid episodeId) =>
        $"{Episode(episodeId)}/playback-plan";

    public static string VideoPlayer =>
        $"{ClientApiContract.BasePath}/video/player";

    public static string VideoPlaybackPlan =>
        $"{ClientApiContract.BasePath}/video/playback-plan";

    public static string VideoProgress =>
        $"{ClientApiContract.BasePath}/video/progress";

    public static string StreamSession(Guid sessionId) =>
        $"{ClientApiContract.BasePath}/stream-sessions/{sessionId:D}";

    public static string StreamSessionStream(Guid sessionId) =>
        $"{StreamSession(sessionId)}/stream";

    public static string StreamSessionHls(Guid sessionId) =>
        $"{StreamSession(sessionId)}/hls";

    public static string StreamSessionHlsAsset(Guid sessionId, Guid hlsSessionId, string fileName) =>
        $"{StreamSessionHls(sessionId)}/{hlsSessionId:D}/{fileName}";

    public static string PlaybackSessions =>
        $"{ClientApiContract.BasePath}/playback-sessions";

    public static string PlaybackSession(Guid sessionId) =>
        $"{PlaybackSessions}/{sessionId:D}";

    public static string PlaybackSessionState(Guid sessionId) =>
        $"{PlaybackSession(sessionId)}/state";

    public static string PlaybackPairing(Guid sessionId) =>
        $"{PlaybackSession(sessionId)}/pairing";

    public static string PlaybackPair =>
        $"{PlaybackSessions}/pair";

    public static string PlaybackParticipantState(Guid sessionId) =>
        $"{PlaybackSession(sessionId)}/participant-state";

    public static string PlaybackCommands(Guid sessionId) =>
        $"{PlaybackSession(sessionId)}/commands";

    public static string PlaybackRevoke(Guid sessionId) =>
        $"{PlaybackSession(sessionId)}/revoke";

    public static string PlaybackHub =>
        "/hubs/playback-session";
}

public sealed record ClientCapabilitiesResponse(
    int ApiVersion,
    int MinimumSupportedApiVersion,
    string ServerVersion,
    ClientFeatureFlags Features);

public sealed record ClientFeatureFlags(
    bool Library,
    bool NativeSessionAuth,
    bool NativePlayerBootstrap,
    bool DirectPlayback,
    bool PlaybackProgress,
    bool HttpRangeRequests,
    bool MediaTrackMetadata,
    bool NormalizedLearningCues,
    bool LearningStateMutation,
    bool LiveMp4Fallback,
    bool HlsFallback,
    bool PlaybackSessions,
    bool CompanionPairing,
    bool CompanionControl,
    bool StorageAvailability,
    bool OwnerWakeOnLan,
    bool EpisodeFlow,
    bool ContinueWatching,
    bool PlaybackHistory,
    bool PlaybackPreferences,
    bool EmbeddedSubtitleCues,
    bool OfflineDownloads,
    bool MediaSegments,
    bool Trickplay,
    bool OfflineLibrary,
    bool TtsPreferences = false,
    bool PlaybackPlan = false,
    bool Watchlist = false,
    // TV device-code pairing, "/api/client/v1/pairing/*" (#489).
    bool DevicePairing = false,
    bool OfflinePackages = false);

public sealed record ClientErrorResponse(
    string Code,
    string Message);

public sealed record ClientAccountResponse(
    string ProfileId,
    string? UserName,
    string Role);

public sealed record ClientLoginRequest(
    string UserName,
    string Password,
    bool RememberMe = true);

public sealed record ClientLibraryResponse(
    IReadOnlyList<ClientAnimeSummary> Anime);

public sealed record ClientAnimeSummary(
    Guid Id,
    string Title,
    string LocalTitle,
    string? NativeTitle,
    string? CoverImageUrl,
    string? BannerImageUrl,
    int EpisodeCount,
    int SeasonCount,
    int? SeasonYear,
    string? Format);

public sealed record ClientAnimeDetail(
    Guid Id,
    string Title,
    string LocalTitle,
    string? NativeTitle,
    string? Description,
    string? CoverImageUrl,
    string? BannerImageUrl,
    int? SeasonYear,
    string? Format,
    IReadOnlyList<ClientSeason> Seasons);

public sealed record ClientSeason(
    int Number,
    IReadOnlyList<ClientEpisodeSummary> Episodes);

public sealed record ClientEpisodeSummary(
    Guid Id,
    int SeasonNumber,
    int Number,
    string Title,
    bool HasMedia,
    bool HasJapaneseLearningSubtitle);

public sealed record ClientEpisodeDetail(
    Guid Id,
    Guid AnimeId,
    string AnimeTitle,
    string Title,
    int SeasonNumber,
    int Number,
    bool HasMedia,
    Guid? ActiveLearningSubtitleTrackId,
    int LearningCueCount,
    ClientLearningCoverage Learning);

public sealed record ClientLearningCoverage(
    int TotalTerms,
    int KnownTerms,
    int LearningTerms,
    int NewTerms);

public sealed record ClientEpisodeProgressUpdate(
    long PositionMs,
    long? DurationMs,
    bool Completed);

public sealed record ClientEpisodeProgress(
    long PositionMs,
    long? DurationMs,
    int Percent,
    bool IsCompleted,
    DateTime? UpdatedAtUtc,
    long ResumePositionMs);

public sealed record ClientEpisodeWatchedUpdate(bool Watched);

public sealed record ClientEpisodeReference(
    Guid Id,
    int SeasonNumber,
    int Number,
    string Title);

public sealed record ClientEpisodeFlow(
    Guid EpisodeId,
    Guid AnimeId,
    ClientEpisodeReference? Previous,
    ClientEpisodeReference? Next,
    bool AutoplayNext);

public sealed record ClientContinueWatchingResponse(
    IReadOnlyList<ClientContinueWatchingItem> Items);

public sealed record ClientContinueWatchingItem(
    string Kind,
    Guid EpisodeId,
    Guid AnimeId,
    string AnimeTitle,
    int SeasonNumber,
    int EpisodeNumber,
    string EpisodeTitle,
    long ResumePositionMs,
    long? DurationMs,
    int Percent,
    DateTime UpdatedAtUtc,
    string? CoverImageUrl);

/// <summary>
/// Profile-scoped playback preferences. Language values are normalized tags;
/// <c>preferredSubtitleLanguage</c> may be <c>off</c>. Null means "use the
/// file default".
/// </summary>
public sealed record ClientPlaybackPreferences(
    bool AutoplayNext,
    string? PreferredAudioLanguage,
    string? PreferredSubtitleLanguage,
    double DefaultPlaybackSpeed);

/// <summary>
/// Partial update: omitted/null fields keep their stored value; an empty
/// language string clears that preference.
/// </summary>
public sealed record ClientPlaybackPreferencesUpdate(
    bool? AutoplayNext = null,
    string? PreferredAudioLanguage = null,
    string? PreferredSubtitleLanguage = null,
    double? DefaultPlaybackSpeed = null);

public sealed record ClientPlaybackHistoryResponse(
    int Limit,
    IReadOnlyList<ClientPlaybackHistoryItem> Items);

public sealed record ClientWatchlistResponse(
    IReadOnlyList<ClientWatchlistItem> Items);

/// <summary>
/// A followed work from the signed-in profile's watchlist. <c>availability</c> is
/// <c>in_library</c> when <see cref="WatchlistLibraryResolver"/> matched it to a local library
/// entry (<c>detailsUrl</c> then points at that library page) or <c>external</c> when it is only
/// known through its provider (<c>detailsUrl</c> then points at the provider page, if any).
/// <c>addedAtUtc</c> is null for works only included through a followed franchise, never
/// followed individually.
/// </summary>
public sealed record ClientWatchlistItem(
    Guid Id,
    string MediaType,
    string Title,
    string? ArtworkUrl,
    string Availability,
    string? DetailsUrl,
    DateTime? AddedAtUtc);

/// <summary>
/// Profile-level TTS preferences, backed by the canonical Reader preference "default"
/// scope row (see <see cref="Jularr.Web.Features.Speech.TtsPreferencesService"/>).
/// "auto" means the deterministic resolver order (offline neural, then device, then
/// cloud) picks the provider; VoiceIds maps a normalized BCP-47 tag to a provider voice id.
/// </summary>
public sealed record ClientTtsPreferences(
    string ProviderId,
    IReadOnlyDictionary<string, string> VoiceIds,
    double Rate,
    double Pitch,
    double Volume);

/// <summary>
/// Partial update: omitted/null scalar fields keep their stored value. Setting
/// VoiceLanguage without VoiceId (or with a blank/"auto" VoiceId) clears that language's
/// stored voice.
/// </summary>
public sealed record ClientTtsPreferencesUpdate(
    string? ProviderId = null,
    double? Rate = null,
    double? Pitch = null,
    double? Volume = null,
    string? VoiceLanguage = null,
    string? VoiceId = null);

public sealed record ClientSpeechModelFile(
    string Name,
    string Url,
    long SizeBytes,
    string Sha256);

/// <summary>
/// One explicit, verifiable offline-neural voice pack an owner has pinned in the model
/// manifest (docs/TTS.md, Phase 3). A client must never infer support for a language or
/// voice this entry does not list.
/// </summary>
public sealed record ClientSpeechModel(
    string ProviderId,
    string ModelId,
    string Version,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Voices,
    IReadOnlyList<ClientSpeechModelFile> Files,
    long TotalSizeBytes,
    string MinimumCompatibleVersion);

public sealed record ClientSpeechModelsResponse(IReadOnlyList<ClientSpeechModel> Models);

public sealed record ClientPlaybackHistoryItem(
    Guid Id,
    Guid EpisodeId,
    Guid AnimeId,
    string AnimeTitle,
    int SeasonNumber,
    int EpisodeNumber,
    string EpisodeTitle,
    DateTime StartedAtUtc,
    DateTime LastPlayedAtUtc,
    long PositionMs,
    long? DurationMs,
    bool ReachedEnd);

public sealed record ClientPlayerBootstrap(
    int ApiVersion,
    ClientPlayerEpisode Episode,
    ClientPlayerMedia? Media,
    IReadOnlyList<ClientMediaTrack> AudioTracks,
    IReadOnlyList<ClientMediaTrack> SubtitleTracks,
    IReadOnlyList<ClientLearningSubtitle> LearningSubtitles,
    Guid? ActiveLearningSubtitleTrackId,
    string? DefaultAudioTrackId,
    string? DefaultSubtitleTrackId,
    ClientCompatibilityFallback Fallback,
    ClientPlayerDefaults Defaults,
    ClientPlayerControls Controls,
    ClientSegmentDescriptor? Segments = null,
    ClientTrickplayDescriptor? Trickplay = null);

// Canonical skip markers. Clients seek to EndMs of a segment with CanSkip and
// never derive their own boundaries or confidence rules.
public sealed record ClientSegmentDescriptor(
    double SkipConfidenceThreshold,
    IReadOnlyList<ClientMediaSegment> Segments);

public sealed record ClientMediaSegment(
    string Kind,
    long StartMs,
    long EndMs,
    string Source,
    string Method,
    string Version,
    double Confidence,
    bool CanSkip);

// Timeline preview sprites. Thumbnail i covers [i * IntervalMs, (i + 1) * IntervalMs)
// and sits on SpriteUrls[i / (Columns * Rows)] at column i % Columns, row (i / Columns) % Rows.
public sealed record ClientTrickplayDescriptor(
    string State,
    string? Message,
    int GeneratorVersion,
    int? IntervalMs,
    int? TileWidth,
    int? TileHeight,
    int? Columns,
    int? Rows,
    int? ThumbnailCount,
    IReadOnlyList<string> SpriteUrls,
    string DescriptorUrl);

/// <summary>
/// Server-resolved initial selection for this profile: file defaults
/// overridden by the profile's language preferences. <c>subtitleMode</c> is
/// <c>off</c>, <c>learning</c> (the Jularr cue overlay) or <c>embedded</c>
/// (with <c>subtitleTrackId</c>).
/// </summary>
public sealed record ClientPlayerDefaults(
    string? AudioTrackId,
    string SubtitleMode,
    string? SubtitleTrackId,
    double PlaybackSpeed,
    ClientPlaybackPreferences Preferences);

/// <summary>Canonical control vocabulary: allowed speeds and quality-cap names.</summary>
public sealed record ClientPlayerControls(
    IReadOnlyList<double> PlaybackSpeeds,
    IReadOnlyList<string> QualityCaps);

public sealed record ClientEmbeddedSubtitleCues(
    string TrackId,
    string? Language,
    IReadOnlyList<ClientPlainCue> Cues);

public sealed record ClientPlainCue(
    int StartMs,
    int EndMs,
    string Text);

public sealed record ClientPlayerEpisode(
    Guid Id,
    Guid AnimeId,
    string AnimeTitle,
    string Title,
    int SeasonNumber,
    int Number);

public sealed record ClientPlayerMedia(
    Guid MediaFileId,
    string FileName,
    string ContentType,
    long? SizeBytes,
    long? DurationMs,
    string? VideoCodec,
    string? PixelFormat,
    string? AudioCodec,
    string DirectContentUrl,
    bool SupportsRangeRequests,
    ClientPlaybackOption Device,
    ClientPlaybackOption Server,
    ClientMediaAvailability Availability);

public sealed record ClientPlaybackOption(
    string Availability,
    string Message,
    bool UsesLiveStream);


public sealed record ClientMediaAvailability(
    string State,
    bool Retryable,
    int RetryAfterMs,
    bool CanWake,
    Guid? RootId,
    string AvailabilityUrl,
    string? WakeUrl,
    string? Health = null,
    string? DiagnosticCode = null);

public sealed record ClientRootAvailability(
    Guid RootId,
    string State,
    bool Retryable,
    DateTimeOffset CheckedAtUtc,
    DateTimeOffset? LastAvailableAtUtc,
    bool WakeConfigured,
    string? DiagnosticCode,
    string? Health = null);

public sealed record ClientMediaTrack(
    string Id,
    int StreamIndex,
    string Kind,
    string? Codec,
    string? Language,
    string? Title,
    bool IsDefault,
    bool IsForced,
    bool IsText);

public sealed record ClientLearningSubtitle(
    Guid TrackId,
    string Language,
    string Format,
    bool IsActive,
    string CuesUrl);

public sealed record ClientCompatibilityFallback(
    bool Available,
    string? Kind,
    bool SeekableWithinStream,
    bool CanRestartAtPosition,
    string? Url);

public sealed record ClientCueResponse(
    Guid? TrackId,
    int? FromMs,
    int? ToMs,
    IReadOnlyList<ClientCue> Cues);

public sealed record ClientCue(
    long Id,
    int StartMs,
    int EndMs,
    string Text,
    IReadOnlyList<ClientCueToken> Tokens);

public sealed record ClientCueToken(
    string Surface,
    Guid? TermId,
    string? Canonical,
    string? Reading,
    string? Meaning,
    string State);

public sealed record ClientTermDetail(
    Guid Id,
    string Canonical,
    string? Reading,
    string? Meaning,
    string State);

public sealed record ClientTermStateUpdate(string State);

public sealed record ClientTermStateResult(
    Guid TermId,
    string State);

public static class ClientApiMappings
{
    public static ClientEpisodeProgress ToClientEpisodeProgress(
        EpisodeProgressSnapshot progress) =>
        new(
            progress.PositionMs,
            progress.DurationMs,
            progress.Percent,
            progress.IsCompleted,
            progress.UpdatedAt,
            progress.ResumePositionMs);

    public static ClientEpisodeFlow ToClientEpisodeFlow(EpisodeFlowSnapshot flow) =>
        new(
            flow.EpisodeId,
            flow.AnimeId,
            ToClientEpisodeReference(flow.Previous),
            ToClientEpisodeReference(flow.Next),
            flow.AutoplayNext);

    public static ClientContinueWatchingItem ToClientContinueWatchingItem(
        ContinueWatchingItem item) =>
        new(
            item.Kind == ContinueWatchingKind.UpNext ? "up_next" : "resume",
            item.EpisodeId,
            item.AnimeId,
            item.AnimeTitle,
            item.SeasonNumber,
            item.EpisodeNumber,
            item.EpisodeTitle,
            item.ResumePositionMs,
            item.DurationMs,
            item.Percent,
            DateTime.SpecifyKind(item.UpdatedAt, DateTimeKind.Utc),
            item.CoverImageUrl);

    public static ClientPlaybackHistoryItem ToClientPlaybackHistoryItem(
        PlaybackHistoryItem item) =>
        new(
            item.Id,
            item.EpisodeId,
            item.AnimeId,
            item.AnimeTitle,
            item.SeasonNumber,
            item.EpisodeNumber,
            item.EpisodeTitle,
            DateTime.SpecifyKind(item.StartedAt, DateTimeKind.Utc),
            DateTime.SpecifyKind(item.LastPlayedAt, DateTimeKind.Utc),
            item.PositionMs,
            item.DurationMs,
            item.ReachedEnd);

    public static ClientWatchlistItem ToClientWatchlistItem(WatchlistItem item) =>
        new(
            item.StableId,
            WatchlistMediaTypeNames.ToCategory(item.Identity.MediaType),
            item.Title,
            item.CoverImageUrl,
            item.LocalMediaId is null ? "external" : "in_library",
            item.DetailsUrl,
            item.AddedAtUtc);

    private static ClientEpisodeReference? ToClientEpisodeReference(
        EpisodeReference? episode) =>
        episode is null
            ? null
            : new ClientEpisodeReference(
                episode.Id,
                episode.SeasonNumber,
                episode.Number,
                episode.Title);

    public static ClientPlaybackPreferences ToClientPreferences(
        PlaybackPreferencesSnapshot preferences) =>
        new(
            preferences.AutoplayNext,
            preferences.PreferredAudioLanguage,
            preferences.PreferredSubtitleLanguage,
            preferences.DefaultPlaybackSpeed);

    public static ClientTtsPreferences ToClientTtsPreferences(
        TtsPreferencesSnapshot preferences) =>
        new(
            preferences.ProviderId,
            preferences.VoiceIds,
            preferences.Rate,
            preferences.Pitch,
            preferences.Volume);

    public static ClientSpeechModel ToClientSpeechModel(SpeechModelManifestEntry model) =>
        new(
            model.ProviderId,
            model.ModelId,
            model.Version,
            model.Languages,
            model.Voices,
            model.Files
                .Select(file => new ClientSpeechModelFile(file.Name, file.Url, file.SizeBytes, file.Sha256))
                .ToArray(),
            model.TotalSizeBytes,
            model.MinimumCompatibleVersion);

    public static ClientEmbeddedSubtitleCues ToClientEmbeddedSubtitleCues(
        PlaybackEmbeddedSubtitleCues cues) =>
        new(
            cues.TrackId,
            cues.Language,
            cues.Cues.Select(x => new ClientPlainCue(x.StartMs, x.EndMs, x.Text)).ToArray());

    public static ClientMediaTrack ToClientTrack(PlaybackMediaTrack track) =>
        new(
            PlaybackTrackIds.Format(track.StreamIndex),
            track.StreamIndex,
            track.Kind == PlaybackTrackKind.Audio ? "audio" : "subtitle",
            track.Codec,
            track.Language,
            track.Title,
            track.IsDefault,
            track.IsForced,
            track.IsText);

    public static ClientPlaybackOption ToClientOption(PlaybackOption option) =>
        new(
            option.Availability.ToString().ToLowerInvariant(),
            option.StatusMessage,
            option.UsesLiveStream);


    public static ClientMediaAvailability ToClientAvailability(
        MediaAvailabilitySnapshot availability,
        bool isOwner) =>
        new(
            AvailabilityStateName(availability.State),
            availability.Retryable,
            availability.RetryAfterMs,
            isOwner && availability.WakeConfigured,
            isOwner ? availability.RootId : null,
            ClientApiRoutes.MediaAvailability(availability.MediaFileId),
            isOwner && availability.WakeConfigured
                ? ClientApiRoutes.WakeRoot(availability.RootId)
                : null,
            StorageHealth.Name(availability.Health),
            availability.DiagnosticCode);

    public static ClientRootAvailability ToClientRootAvailability(
        LibraryRootAvailabilitySnapshot availability) =>
        new(
            availability.RootId,
            AvailabilityStateName(availability.State),
            availability.IsRetryable,
            availability.CheckedAtUtc,
            availability.LastAvailableAtUtc,
            availability.WakeConfigured,
            availability.DiagnosticCode,
            StorageHealth.Name(availability.Health));

    public static string AvailabilityStateName(StorageAvailabilityState state) =>
        state switch
        {
            StorageAvailabilityState.Available => "available",
            StorageAvailabilityState.Starting => "source_starting",
            StorageAvailabilityState.Offline => "source_offline",
            StorageAvailabilityState.Unreachable => "source_unreachable",
            StorageAvailabilityState.FileMissing => "file_missing",
            _ => "unknown"
        };

    public static string StateName(UserTermState? state) =>
        state switch
        {
            UserTermState.Known => "known",
            UserTermState.Learning => "learning",
            _ => "new"
        };

    public static ClientSegmentDescriptor ToClientSegments(EpisodeSegmentDescriptor descriptor) =>
        new(
            descriptor.SkipConfidenceThreshold,
            descriptor.Segments
                .Select(segment => new ClientMediaSegment(
                    MediaSegmentPolicy.KindName(segment.Kind),
                    segment.StartMs,
                    segment.EndMs,
                    MediaSegmentPolicy.SourceName(segment.Source),
                    segment.Method,
                    segment.Version,
                    segment.Confidence,
                    segment.CanSkip))
                .ToArray());

    public static ClientTrickplayDescriptor ToClientTrickplay(
        Guid episodeId,
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
                    // Asset names repeat across generations; the identity query keeps a
                    // privately cached sprite of a replaced file from being reused.
                    .Select(sprite =>
                        $"{ClientApiRoutes.TrickplayAsset(episodeId, sprite)}?v={index.MediaIdentity[..Math.Min(16, index.MediaIdentity.Length)]}-{index.GeneratorVersion}")
                    .ToArray(),
            ClientApiRoutes.Trickplay(episodeId));
    }

    public static ClientCue ToClientCue(PlaybackCue cue)
    {
        var tokens = cue.Tokens
            .Select(token => new ClientCueToken(
                token.Surface,
                token.TermId,
                token.Canonical,
                token.Reading,
                token.Meaning,
                string.IsNullOrWhiteSpace(token.State)
                    ? "new"
                    : token.State.ToLowerInvariant()))
            .ToArray();

        return new ClientCue(
            cue.CueId,
            cue.StartMs,
            cue.EndMs,
            string.Concat(cue.Tokens.Select(x => x.Surface)),
            tokens);
    }
}
