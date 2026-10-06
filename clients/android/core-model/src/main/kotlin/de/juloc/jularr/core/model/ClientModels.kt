package de.juloc.jularr.core.model

data class ClientCapabilities(
    val apiVersion: Int,
    val minimumSupportedApiVersion: Int,
    val serverVersion: String,
    val features: ClientFeatureFlags,
)

data class ClientFeatureFlags(
    val library: Boolean,
    val nativeSessionAuth: Boolean = false,
    val nativePlayerBootstrap: Boolean,
    val directPlayback: Boolean,
    val playbackProgress: Boolean,
    val httpRangeRequests: Boolean,
    val mediaTrackMetadata: Boolean,
    val normalizedLearningCues: Boolean,
    val learningStateMutation: Boolean,
    val liveMp4Fallback: Boolean,
    val hlsFallback: Boolean,
    val playbackSessions: Boolean,
    val companionPairing: Boolean,
    val companionControl: Boolean,
    val storageAvailability: Boolean,
    val ownerWakeOnLan: Boolean,
    val offlineDownloads: Boolean = false,
    val offlineLibrary: Boolean = false,
    val ttsPreferences: Boolean = false,
    val continueWatching: Boolean = false,
    val playbackHistory: Boolean = false,
    val watchlist: Boolean = false,
    /** TV device-code pairing, POST /api/client/v1/pairing start|approve|poll (#489). */
    val devicePairing: Boolean = false,
    val offlinePackages: Boolean = false,
)

data class ClientAccount(
    val profileId: String,
    val userName: String?,
    val role: String,
)

data class ClientLogin(
    val userName: String,
    val password: String,
    val rememberMe: Boolean = true,
)

/**
 * Result of `POST /pairing/start` (#489): [userCode] is what the TV displays for a human to type
 * on their phone/computer; [deviceCode] is the opaque value the TV itself polls with and never
 * shows on screen.
 */
data class DevicePairingSession(
    val deviceCode: String,
    val userCode: String,
    val expiresInSeconds: Int,
    val intervalSeconds: Int,
)

/** Result of one `POST /pairing/poll` call (#489). */
sealed interface DevicePairingPollResult {
    data class Pending(val intervalSeconds: Int) : DevicePairingPollResult
    data class Approved(val account: ClientAccount) : DevicePairingPollResult
    data object Expired : DevicePairingPollResult
}

/** One server found while searching the LAN for Jularr (#489, see the discovery beacon protocol). */
data class DiscoveredJularrServer(
    val origin: String,
    val name: String,
    val version: String,
)

data class ClientLibrary(
    val anime: List<AnimeSummary>,
)

data class AnimeSummary(
    val id: String,
    val title: String,
    val localTitle: String,
    val nativeTitle: String?,
    val coverImageUrl: String?,
    val bannerImageUrl: String?,
    val episodeCount: Int,
    val seasonCount: Int,
    val seasonYear: Int?,
    val format: String?,
)

/**
 * One row of `GET /continue-watching` (docs/ANDROID_CLIENTS.md, "Playback continuity
 * endpoints"): the profile's in-progress episodes, most recently played first.
 */
data class ContinueWatchingItem(
    val kind: String,
    val episodeId: String,
    val animeId: String,
    val animeTitle: String,
    val seasonNumber: Int,
    val episodeNumber: Int,
    val episodeTitle: String,
    val resumePositionMs: Long,
    val durationMs: Long?,
    val percent: Int,
    val updatedAtUtc: String,
    val coverImageUrl: String?,
)

/**
 * One row of `GET /me/playback-history`: a past playback entry, most recent first. The
 * server bounds the number of rows it keeps (`EpisodeProgressService.HistoryLimit`).
 */
data class PlaybackHistoryItem(
    val id: String,
    val episodeId: String,
    val animeId: String,
    val animeTitle: String,
    val seasonNumber: Int,
    val episodeNumber: Int,
    val episodeTitle: String,
    val startedAtUtc: String,
    val lastPlayedAtUtc: String,
    val positionMs: Long,
    val durationMs: Long?,
    val reachedEnd: Boolean,
)

/**
 * One entry of `GET /watchlist`: a followed work from the signed-in profile's watchlist
 * (docs/ANDROID_CLIENTS.md, "Playback continuity endpoints"). `availability` is
 * `in_library` when the work is matched to a local library entry (`detailsUrl` then points
 * at that library page) or `external` when it is only known through its provider.
 * `addedAtUtc` is null for works only included through a followed franchise, never
 * followed individually.
 */
data class WatchlistItem(
    val id: String,
    val mediaType: String,
    val title: String,
    val artworkUrl: String?,
    val availability: String,
    val detailsUrl: String?,
    val addedAtUtc: String?,
)

data class AnimeDetail(
    val id: String,
    val title: String,
    val localTitle: String,
    val nativeTitle: String?,
    val description: String?,
    val coverImageUrl: String?,
    val bannerImageUrl: String?,
    val seasonYear: Int?,
    val format: String?,
    val seasons: List<Season>,
)

data class Season(
    val number: Int,
    val episodes: List<EpisodeSummary>,
)

data class EpisodeSummary(
    val id: String,
    val seasonNumber: Int,
    val number: Int,
    val title: String,
    val hasMedia: Boolean,
    val hasJapaneseLearningSubtitle: Boolean,
)

data class EpisodeDetail(
    val id: String,
    val animeId: String,
    val animeTitle: String,
    val title: String,
    val seasonNumber: Int,
    val number: Int,
    val hasMedia: Boolean,
    val activeLearningSubtitleTrackId: String?,
    val learningCueCount: Int,
    val learning: LearningCoverage,
)

data class LearningCoverage(
    val totalTerms: Int,
    val knownTerms: Int,
    val learningTerms: Int,
    val newTerms: Int,
)

data class EpisodeProgressUpdate(
    val positionMs: Long,
    val durationMs: Long?,
    val completed: Boolean,
)

data class EpisodeProgress(
    val positionMs: Long,
    val durationMs: Long?,
    val percent: Int,
    val isCompleted: Boolean,
    val updatedAtUtc: String?,
)

data class PlayerBootstrap(
    val apiVersion: Int,
    val episode: PlayerEpisode,
    val media: PlayerMedia?,
    val audioTracks: List<MediaTrack>,
    val subtitleTracks: List<MediaTrack>,
    val learningSubtitles: List<LearningSubtitle>,
    val activeLearningSubtitleTrackId: String?,
    val defaultAudioTrackId: String?,
    val defaultSubtitleTrackId: String?,
    val fallback: CompatibilityFallback,
)

data class PlayerEpisode(
    val id: String,
    val animeId: String,
    val animeTitle: String,
    val title: String,
    val seasonNumber: Int,
    val number: Int,
)

data class PlayerMedia(
    val mediaFileId: String,
    val fileName: String,
    val contentType: String,
    val sizeBytes: Long?,
    val durationMs: Long?,
    val videoCodec: String?,
    val pixelFormat: String?,
    val audioCodec: String?,
    val directContentUrl: String,
    val supportsRangeRequests: Boolean,
    val device: PlaybackOption,
    val server: PlaybackOption,
    val availability: MediaAvailability,
)

data class PlaybackOption(
    val availability: String,
    val message: String,
    val usesLiveStream: Boolean,
)

data class MediaAvailability(
    val state: String,
    val retryable: Boolean,
    val retryAfterMs: Int,
    val canWake: Boolean,
    val rootId: String?,
    val availabilityUrl: String,
    val wakeUrl: String?,
) {
    val isAvailable: Boolean
        get() = state == "available"
}

data class RootAvailability(
    val rootId: String,
    val state: String,
    val retryable: Boolean,
    val checkedAtUtc: String,
    val lastAvailableAtUtc: String?,
    val wakeConfigured: Boolean,
    val diagnosticCode: String?,
)

data class MediaTrack(
    val id: String,
    val streamIndex: Int,
    val kind: String,
    val codec: String?,
    val language: String?,
    val title: String?,
    val isDefault: Boolean,
    val isForced: Boolean,
    val isText: Boolean,
)

data class LearningSubtitle(
    val trackId: String,
    val language: String,
    val format: String,
    val isActive: Boolean,
    val cuesUrl: String,
)

data class CompatibilityFallback(
    val available: Boolean,
    val kind: String?,
    val seekableWithinStream: Boolean,
    val canRestartAtPosition: Boolean,
    val url: String?,
)

data class CueResponse(
    val trackId: String?,
    val fromMs: Int?,
    val toMs: Int?,
    val cues: List<SubtitleCue>,
)

data class SubtitleCue(
    val id: Long,
    val startMs: Int,
    val endMs: Int,
    val text: String,
    val tokens: List<CueToken>,
)

data class CueToken(
    val surface: String,
    val termId: String?,
    val canonical: String?,
    val reading: String?,
    val meaning: String?,
    val state: String,
)

data class TermDetail(
    val id: String,
    val canonical: String,
    val reading: String?,
    val meaning: String?,
    val state: String,
)

data class TermStateUpdate(
    val state: String,
)

data class TermStateResult(
    val termId: String,
    val state: String,
)

/**
 * Profile-level TTS preferences (server: `ClientTtsPreferences`, backed by the canonical
 * Reader preference "default" scope row; docs/TTS.md). "auto" means the deterministic
 * resolver order picks the provider; voiceIds maps a normalized BCP-47 tag to a provider
 * voice id. Device-only facts (which offline models are installed) are never part of this
 * profile-scoped model; see `de.juloc.jularr.core.tts.model.TtsModelManager`.
 */
data class TtsPreferences(
    val providerId: String,
    val voiceIds: Map<String, String>,
    val rate: Double,
    val pitch: Double,
    val volume: Double,
)

/**
 * Partial update: omitted/null scalar fields keep their stored value. Setting
 * voiceLanguage without voiceId (or with a blank voiceId) clears that language's stored
 * voice.
 */
data class TtsPreferencesUpdate(
    val providerId: String? = null,
    val rate: Double? = null,
    val pitch: Double? = null,
    val volume: Double? = null,
    val voiceLanguage: String? = null,
    val voiceId: String? = null,
)

data class SpeechModelFileDescriptor(
    val name: String,
    val url: String,
    val sizeBytes: Long,
    val sha256: String,
)

/**
 * One offline-neural voice pack an owner has pinned in the server's model manifest
 * (docs/TTS.md, Phase 3). Never infer support for a language or voice this entry does not
 * list.
 */
data class SpeechModel(
    val providerId: String,
    val modelId: String,
    val version: String,
    val languages: List<String>,
    val voices: List<String>,
    val files: List<SpeechModelFileDescriptor>,
    val totalSizeBytes: Long,
    val minimumCompatibleVersion: String,
)

data class SpeechModelsResponse(
    val models: List<SpeechModel>,
)
