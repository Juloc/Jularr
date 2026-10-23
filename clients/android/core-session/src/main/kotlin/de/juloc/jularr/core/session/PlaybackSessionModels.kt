package de.juloc.jularr.core.session

data class PlaybackSessionToken(
    val surface: String,
    val termId: String?,
    val canonical: String?,
    val reading: String?,
    val meaning: String?,
    val state: String,
)

data class PlaybackSessionSnapshot(
    val sessionId: String,
    val episodeId: String,
    val animeTitle: String,
    val episodeTitle: String,
    val positionMs: Long,
    val durationMs: Long?,
    val isPlaying: Boolean,
    val playbackRate: Double,
    val audioTrackId: String?,
    val subtitleTrackId: String?,
    val currentCueId: Long?,
    val currentCueText: String?,
    val currentCueTokens: List<PlaybackSessionToken>,
    val selectedTermId: String?,
    val revision: Long,
    val updatedAtUtc: String,
)

data class PlaybackSessionUpdate(
    val animeTitle: String,
    val episodeTitle: String,
    val positionMs: Long,
    val durationMs: Long?,
    val isPlaying: Boolean,
    val playbackRate: Double = 1.0,
    val audioTrackId: String?,
    val subtitleTrackId: String?,
    val currentCueId: Long?,
    val currentCueText: String?,
    val currentCueTokens: List<PlaybackSessionToken>,
    val selectedTermId: String?,
)

data class PlaybackSessionOwner(
    val state: PlaybackSessionSnapshot,
    val hubUrl: String,
)

data class PlaybackPairing(
    val sessionId: String,
    val code: String,
    val token: String,
    val companionUrl: String,
    val expiresAtUtc: String,
)

data class PlaybackParticipant(
    val sessionId: String,
    val accessToken: String,
    val state: PlaybackSessionSnapshot,
    val hubUrl: String,
)

data class PlaybackCommand(
    val commandId: String,
    val sessionId: String,
    val expectedRevision: Long,
    val type: String,
    val payload: Map<String, String> = emptyMap(),
    val sentAtUtc: String,
)

data class PlaybackCommandResponse(
    val acceptance: String,
    val state: PlaybackSessionSnapshot?,
    val command: PlaybackCommand?,
)
