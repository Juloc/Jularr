package de.juloc.jularr.core.api

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
import de.juloc.jularr.core.model.SpeechModelsResponse
import de.juloc.jularr.core.model.TermDetail
import de.juloc.jularr.core.model.TermStateResult
import de.juloc.jularr.core.model.TtsPreferences
import de.juloc.jularr.core.model.TtsPreferencesUpdate
import de.juloc.jularr.core.model.WatchlistItem
import de.juloc.jularr.core.model.TvPlaybackPreferences
import de.juloc.jularr.core.model.TvPlaybackPreferencesUpdate

interface JularrClientApi {
    suspend fun getCapabilities(): ClientCapabilities
    suspend fun login(credentials: ClientLogin): ClientAccount
    suspend fun logout()
    suspend fun getMe(): ClientAccount
    suspend fun getLibrary(): ClientLibrary

    /** `GET /continue-watching`: in-progress episodes, most recently played first. */
    suspend fun getContinueWatching(): List<ContinueWatchingItem>

    /** `GET /me/playback-history`: past playback entries, most recent first. */
    suspend fun getPlaybackHistory(): List<PlaybackHistoryItem>

    /** `GET /watchlist`: the signed-in profile's followed works. */
    suspend fun getWatchlist(): List<WatchlistItem>

    suspend fun getPlaybackPreferences(): TvPlaybackPreferences =
        throw UnsupportedOperationException("Playback preferences require the client API.")
    suspend fun updatePlaybackPreferences(update: TvPlaybackPreferencesUpdate): TvPlaybackPreferences =
        throw UnsupportedOperationException("Playback preferences require the client API.")

    suspend fun getAnime(animeId: String): AnimeDetail
    suspend fun getEpisode(episodeId: String): EpisodeDetail
    suspend fun getProgress(episodeId: String): EpisodeProgress
    suspend fun setProgress(
        episodeId: String,
        update: EpisodeProgressUpdate,
    ): EpisodeProgress

    suspend fun getPlayer(episodeId: String): PlayerBootstrap

    suspend fun getCues(
        episodeId: String,
        trackId: String? = null,
        fromMs: Int? = null,
        toMs: Int? = null,
    ): CueResponse

    suspend fun getMediaAvailability(
        mediaFileId: String,
        fresh: Boolean = false,
    ): MediaAvailability

    suspend fun getTerm(termId: String): TermDetail
    suspend fun setTermState(
        termId: String,
        state: String,
    ): TermStateResult

    suspend fun getRootAvailability(rootId: String): RootAvailability
    suspend fun testRoot(rootId: String): RootAvailability
    suspend fun wakeRoot(rootId: String): RootAvailability

    suspend fun getTtsPreferences(): TtsPreferences
    suspend fun updateTtsPreferences(update: TtsPreferencesUpdate): TtsPreferences
    suspend fun getSpeechModels(): SpeechModelsResponse

    /**
     * `POST /pairing/start` (#489): begins a device-code pairing and returns the short user
     * code the TV displays plus the opaque device code it polls with. Anonymous: a fresh TV has
     * no session yet.
     */
    suspend fun startDevicePairing(): DevicePairingSession

    /**
     * `POST /pairing/poll` (#489): asks whether [deviceCode] has been approved yet. A successful
     * [DevicePairingPollResult.Approved] result means the server already signed this connection
     * in (the same cookie mechanism `login` uses) — the caller does not call `login` afterward.
     */
    suspend fun pollDevicePairing(deviceCode: String): DevicePairingPollResult
}

sealed interface ApiCompatibility {
    data object Compatible : ApiCompatibility
    data class ServerTooOld(val serverApiVersion: Int) : ApiCompatibility
    data class ClientTooOld(val minimumSupportedApiVersion: Int) : ApiCompatibility
}

object ClientApiCompatibility {
    fun evaluate(capabilities: ClientCapabilities): ApiCompatibility =
        when {
            capabilities.minimumSupportedApiVersion > ClientApiRoutes.ApiVersion ->
                ApiCompatibility.ClientTooOld(capabilities.minimumSupportedApiVersion)

            capabilities.apiVersion < ClientApiRoutes.ApiVersion ->
                ApiCompatibility.ServerTooOld(capabilities.apiVersion)

            else -> ApiCompatibility.Compatible
        }
}
