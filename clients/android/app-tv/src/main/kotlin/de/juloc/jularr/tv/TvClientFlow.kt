package de.juloc.jularr.tv

import de.juloc.jularr.core.api.JularrClientApi
import de.juloc.jularr.core.api.ApiCompatibility
import de.juloc.jularr.core.api.ClientApiCompatibility
import de.juloc.jularr.core.api.ClientApiRoutes
import de.juloc.jularr.core.model.AnimeDetail
import de.juloc.jularr.core.model.ClientAccount
import de.juloc.jularr.core.model.ClientCapabilities
import de.juloc.jularr.core.model.ClientLibrary
import de.juloc.jularr.core.model.ClientLogin
import de.juloc.jularr.core.model.ContinueWatchingItem
import de.juloc.jularr.core.model.CueResponse
import de.juloc.jularr.core.model.DevicePairingPollResult
import de.juloc.jularr.core.model.DevicePairingSession
import de.juloc.jularr.core.model.EpisodeDetail
import de.juloc.jularr.core.model.EpisodeProgress
import de.juloc.jularr.core.model.EpisodeProgressUpdate
import de.juloc.jularr.core.model.MediaAvailability
import de.juloc.jularr.core.model.PlaybackHistoryItem
import de.juloc.jularr.core.model.PlayerBootstrap
import de.juloc.jularr.core.model.RootAvailability
import de.juloc.jularr.core.model.TermStateResult
import de.juloc.jularr.core.model.WatchlistItem

class TvClientFlow(
    private val apiFactory: (String) -> JularrClientApi,
) {
    private var api: JularrClientApi? = null

    var origin: String? = null
        private set

    suspend fun connect(rawOrigin: String): ClientCapabilities {
        val normalized = TvServerOrigin.normalize(rawOrigin)
        val client = apiFactory(normalized)
        val capabilities = client.getCapabilities()

        when (val compatibility = ClientApiCompatibility.evaluate(capabilities)) {
            ApiCompatibility.Compatible -> Unit
            is ApiCompatibility.ClientTooOld -> throw TvClientCompatibilityException(
                "This Jularr server requires client API ${compatibility.minimumSupportedApiVersion}. Update the TV app.",
            )
            is ApiCompatibility.ServerTooOld -> throw TvClientCompatibilityException(
                "This TV app requires client API ${ClientApiRoutes.ApiVersion}, but the server provides ${compatibility.serverApiVersion}. Update Jularr.",
            )
        }

        if (!capabilities.features.nativeSessionAuth) {
            throw TvClientCompatibilityException(
                "This Jularr server does not support native TV sign-in.",
            )
        }

        origin = normalized
        api = client
        return capabilities
    }

    suspend fun login(
        userName: String,
        password: String,
        capabilities: ClientCapabilities,
    ): TvSignedInData {
        val account = requireApi().login(
            ClientLogin(
                userName = userName,
                password = password,
                rememberMe = true,
            ),
        )
        return signedInData(account, capabilities)
    }

    /**
     * `POST /pairing/start` (#489): begins a device-code pairing on the connected server. The
     * caller shows [DevicePairingSession.userCode] and repeatedly calls [pollDevicePairing] with
     * [DevicePairingSession.deviceCode] until it stops being
     * [DevicePairingPollResult.Pending].
     */
    suspend fun startDevicePairing(): DevicePairingSession =
        requireApi().startDevicePairing()

    suspend fun pollDevicePairing(deviceCode: String): DevicePairingPollResult =
        requireApi().pollDevicePairing(deviceCode)

    /**
     * Finishes sign-in after [pollDevicePairing] returns
     * [DevicePairingPollResult.Approved]: the server already signed this connection's cookie in,
     * so this only loads the same account data [login] does — it never calls `login` itself.
     */
    suspend fun completeDevicePairing(
        account: ClientAccount,
        capabilities: ClientCapabilities,
    ): TvSignedInData =
        signedInData(account, capabilities)

    suspend fun restoreSession(
        capabilities: ClientCapabilities,
    ): TvSignedInData {
        val account = requireApi().getMe()
        return signedInData(account, capabilities)
    }

    private suspend fun signedInData(
        account: ClientAccount,
        capabilities: ClientCapabilities,
    ): TvSignedInData {
        val client = requireApi()
        return TvSignedInData(
            account = account,
            library = client.getLibrary(),
            continueWatching = if (capabilities.features.continueWatching) {
                client.getContinueWatching()
            } else {
                emptyList()
            },
        )
    }

    suspend fun refreshLibrary(): ClientLibrary =
        requireApi().getLibrary()

    suspend fun loadContinueWatching(): List<ContinueWatchingItem> =
        requireApi().getContinueWatching()

    suspend fun loadPlaybackHistory(): List<PlaybackHistoryItem> =
        requireApi().getPlaybackHistory()

    suspend fun loadWatchlist(): List<WatchlistItem> =
        requireApi().getWatchlist()

    suspend fun loadAnime(animeId: String): AnimeDetail =
        requireApi().getAnime(animeId)

    suspend fun loadEpisodePage(episodeId: String): TvEpisodePageData {
        val client = requireApi()
        return TvEpisodePageData(
            detail = client.getEpisode(episodeId),
            progress = client.getProgress(episodeId),
        )
    }

    suspend fun loadEpisode(episodeId: String): TvEpisodeBundle {
        val client = requireApi()
        val bootstrap = client.getPlayer(episodeId)
        val progress = client.getProgress(episodeId)
        val activeTrackId = bootstrap.activeLearningSubtitleTrackId
        val cues = if (activeTrackId == null) {
            emptyCueWindow()
        } else {
            loadCueWindow(
                episodeId = episodeId,
                trackId = activeTrackId,
                positionMs = progress.positionMs,
            )
        }

        return TvEpisodeBundle(
            bootstrap = bootstrap,
            progress = progress,
            cues = cues,
        )
    }

    suspend fun loadCueWindow(
        episodeId: String,
        trackId: String,
        positionMs: Long,
        beforeMs: Int = 5_000,
        afterMs: Int = 60_000,
    ): CueResponse {
        require(beforeMs >= 0 && afterMs > 0) {
            "Cue window bounds must be positive."
        }

        val center = positionMs.coerceAtLeast(0)
        val from = (center - beforeMs)
            .coerceAtLeast(0)
            .coerceAtMost(Int.MAX_VALUE.toLong())
            .toInt()
        val to = (center + afterMs)
            .coerceAtMost(Int.MAX_VALUE.toLong())
            .toInt()

        return requireApi().getCues(
            episodeId = episodeId,
            trackId = trackId,
            fromMs = from,
            toMs = to,
        )
    }

    suspend fun refreshMediaAvailability(
        mediaFileId: String,
        fresh: Boolean = true,
    ): MediaAvailability =
        requireApi().getMediaAvailability(mediaFileId, fresh)

    suspend fun wakeRoot(rootId: String): RootAvailability =
        requireApi().wakeRoot(rootId)

    suspend fun saveProgress(
        episodeId: String,
        positionMs: Long,
        durationMs: Long?,
        completed: Boolean,
    ): EpisodeProgress =
        requireApi().setProgress(
            episodeId,
            EpisodeProgressUpdate(
                positionMs = positionMs.coerceAtLeast(0),
                durationMs = durationMs?.coerceAtLeast(0),
                completed = completed,
            ),
        )

    suspend fun setTermState(
        termId: String,
        state: String,
    ): TermStateResult {
        require(state == "known" || state == "learning") {
            "TV learning state must be known or learning."
        }
        return requireApi().setTermState(termId, state)
    }

    suspend fun logout() {
        requireApi().logout()
    }

    private fun requireApi(): JularrClientApi =
        api ?: error("Jularr TV has not connected to a server yet.")

    private fun emptyCueWindow() = CueResponse(
        trackId = null,
        fromMs = null,
        toMs = null,
        cues = emptyList(),
    )
}

data class TvSignedInData(
    val account: ClientAccount,
    val library: ClientLibrary,
    val continueWatching: List<ContinueWatchingItem> = emptyList(),
)

data class TvEpisodePageData(
    val detail: EpisodeDetail,
    val progress: EpisodeProgress,
)

data class TvEpisodeBundle(
    val bootstrap: PlayerBootstrap,
    val progress: EpisodeProgress,
    val cues: CueResponse,
)

class TvClientCompatibilityException(
    message: String,
) : IllegalStateException(message)
