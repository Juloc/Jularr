package de.juloc.jularr.tv

import de.juloc.jularr.core.api.JularrClientApi
import de.juloc.jularr.core.model.AnimeDetail
import de.juloc.jularr.core.model.ClientAccount
import de.juloc.jularr.core.model.ClientCapabilities
import de.juloc.jularr.core.model.ClientFeatureFlags
import de.juloc.jularr.core.model.ClientLibrary
import de.juloc.jularr.core.model.ClientLogin
import de.juloc.jularr.core.model.CueResponse
import de.juloc.jularr.core.model.EpisodeDetail
import de.juloc.jularr.core.model.EpisodeProgress
import de.juloc.jularr.core.model.EpisodeProgressUpdate
import de.juloc.jularr.core.model.MediaAvailability
import de.juloc.jularr.core.model.PlayerBootstrap
import de.juloc.jularr.core.model.RootAvailability
import de.juloc.jularr.core.model.SpeechModelsResponse
import de.juloc.jularr.core.model.TermDetail
import de.juloc.jularr.core.model.TermStateResult
import de.juloc.jularr.core.model.TtsPreferences
import de.juloc.jularr.core.model.TtsPreferencesUpdate
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test
import kotlin.coroutines.startCoroutine

class TvClientFlowTest {
    @Test
    fun connectRequiresNativeSessionAuth() {
        val api = FakeApi(capabilities = capabilities(nativeSessionAuth = false))
        val flow = TvClientFlow { api }

        val error = assertThrows(TvClientCompatibilityException::class.java) {
            runSuspend { flow.connect("https://jularr.example") }
        }

        assertEquals(
            "This Jularr server does not support native TV sign-in.",
            error.message,
        )
    }

    @Test
    fun cueWindowIsBoundedAroundPlaybackPosition() {
        val api = FakeApi(capabilities = capabilities())
        val flow = TvClientFlow { api }
        runSuspend { flow.connect("https://jularr.example") }

        runSuspend {
            flow.loadCueWindow(
                episodeId = "episode",
                trackId = "track",
                positionMs = 42_000,
            )
        }

        assertEquals(37_000, api.lastCueFromMs)
        assertEquals(102_000, api.lastCueToMs)
    }

    private fun capabilities(
        nativeSessionAuth: Boolean = true,
    ) = ClientCapabilities(
        apiVersion = 2,
        minimumSupportedApiVersion = 2,
        serverVersion = "test",
        features = ClientFeatureFlags(
            library = true,
            nativeSessionAuth = nativeSessionAuth,
            nativePlayerBootstrap = true,
            directPlayback = true,
            playbackProgress = true,
            httpRangeRequests = true,
            mediaTrackMetadata = true,
            normalizedLearningCues = true,
            learningStateMutation = true,
            liveMp4Fallback = true,
            hlsFallback = false,
            playbackSessions = false,
            companionPairing = false,
            companionControl = false,
            storageAvailability = true,
            ownerWakeOnLan = true,
        ),
    )

    private class FakeApi(
        private val capabilities: ClientCapabilities,
    ) : JularrClientApi {
        var lastCueFromMs: Int? = null
        var lastCueToMs: Int? = null

        override suspend fun getCapabilities() = capabilities
        override suspend fun login(credentials: ClientLogin) =
            ClientAccount("profile", credentials.userName, "owner")
        override suspend fun logout() = Unit
        override suspend fun getMe() = ClientAccount("profile", "owner", "owner")
        override suspend fun getLibrary() = ClientLibrary(emptyList())
        override suspend fun getContinueWatching() = emptyList<de.juloc.jularr.core.model.ContinueWatchingItem>()
        override suspend fun getPlaybackHistory() = emptyList<de.juloc.jularr.core.model.PlaybackHistoryItem>()
        override suspend fun getWatchlist() = emptyList<de.juloc.jularr.core.model.WatchlistItem>()
        override suspend fun getAnime(animeId: String): AnimeDetail =
            error("unused")
        override suspend fun getEpisode(episodeId: String): EpisodeDetail =
            error("unused")
        override suspend fun getProgress(episodeId: String): EpisodeProgress =
            error("unused")
        override suspend fun setProgress(
            episodeId: String,
            update: EpisodeProgressUpdate,
        ): EpisodeProgress = error("unused")
        override suspend fun getPlayer(episodeId: String): PlayerBootstrap =
            error("unused")

        override suspend fun getCues(
            episodeId: String,
            trackId: String?,
            fromMs: Int?,
            toMs: Int?,
        ): CueResponse {
            lastCueFromMs = fromMs
            lastCueToMs = toMs
            return CueResponse(trackId, fromMs, toMs, emptyList())
        }

        override suspend fun getMediaAvailability(
            mediaFileId: String,
            fresh: Boolean,
        ): MediaAvailability = error("unused")

        override suspend fun getTerm(termId: String): TermDetail =
            error("unused")

        override suspend fun setTermState(
            termId: String,
            state: String,
        ) = TermStateResult(termId, state)

        override suspend fun getRootAvailability(rootId: String): RootAvailability =
            error("unused")
        override suspend fun testRoot(rootId: String): RootAvailability =
            error("unused")
        override suspend fun wakeRoot(rootId: String): RootAvailability =
            error("unused")
        override suspend fun getTtsPreferences(): TtsPreferences =
            error("unused")
        override suspend fun updateTtsPreferences(update: TtsPreferencesUpdate): TtsPreferences =
            error("unused")
        override suspend fun getSpeechModels(): SpeechModelsResponse =
            error("unused")
        override suspend fun startDevicePairing(): de.juloc.jularr.core.model.DevicePairingSession =
            error("unused")
        override suspend fun pollDevicePairing(
            deviceCode: String,
        ): de.juloc.jularr.core.model.DevicePairingPollResult = error("unused")
    }

    private fun <T> runSuspend(block: suspend () -> T): T {
        var result: Result<T>? = null
        block.startCoroutine(
            object : kotlin.coroutines.Continuation<T> {
                override val context = kotlin.coroutines.EmptyCoroutineContext
                override fun resumeWith(value: Result<T>) {
                    result = value
                }
            },
        )
        return result!!.getOrThrow()
    }
}
