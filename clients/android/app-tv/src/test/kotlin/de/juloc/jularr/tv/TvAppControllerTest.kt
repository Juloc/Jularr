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
import de.juloc.jularr.core.model.WatchlistItem
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.coroutines.startCoroutine

class TvAppControllerTest {
    @Test
    fun connectPersistsNormalizedOriginAndMovesToLogin() {
        val store = FakeOriginStore()
        val api = FakeApi()
        val controller = TvAppController(store) { api }

        val state = runSuspend {
            controller.connect("HTTPS://Jularr.Example/")
        }

        assertEquals("https://jularr.example", store.origin)
        assertEquals(TvRoute.Login, state.navigation.route)
        assertFalse(state.busy)
        assertNull(state.error)
    }

    @Test
    fun busyActionsReturnTheSameIdleSnapshotTheyStore() {
        // TvAppHost assigns the returned snapshot straight to UI state, so a
        // returned busy = true would leave the TV UI stuck in its busy state (#497).
        val store = FakeOriginStore("https://jularr.example")
        val api = FakeApi()
        val controller = TvAppController(store) { api }

        val results = listOf(
            runSuspend { controller.restoreConnection() },
            runSuspend { controller.login("jessi", "password-password") },
            runSuspend { controller.openEpisode(episodeId = "episode", animeId = "anime") },
        )

        for (result in results) {
            assertFalse(result.busy)
        }
        assertEquals(controller.snapshot, results.last())
    }

    @Test
    fun loginMovesToHomeAndKeepsAccountProfile() {
        val store = FakeOriginStore("https://jularr.example")
        val api = FakeApi()
        val controller = TvAppController(store) { api }

        runSuspend { controller.restoreConnection() }
        val state = runSuspend { controller.login("jessi", "password-password") }

        assertEquals(TvRoute.Home, state.navigation.route)
        assertEquals("profile", state.account?.profileId)
        assertEquals(1, state.library?.anime?.size)
        assertEquals(1, state.continueWatching.size)
    }

    @Test
    fun selectingActivityLoadsPlaybackHistoryWhenTheServerAdvertisesIt() {
        val store = FakeOriginStore("https://jularr.example")
        val api = FakeApi()
        val controller = TvAppController(store) { api }

        runSuspend { controller.restoreConnection() }
        runSuspend { controller.login("jessi", "password-password") }
        val state = runSuspend { controller.selectSidebarRoute(TvRoute.Activity) }

        assertEquals(TvRoute.Activity, state.navigation.route)
        assertFalse(state.activityUsesContinueWatchingFallback)
        assertEquals(1, state.activity.size)
    }

    @Test
    fun selectingActivityFallsBackToContinueWatchingWithoutTheFlag() {
        val store = FakeOriginStore("https://jularr.example")
        val api = FakeApi(advertisePlaybackHistory = false)
        val controller = TvAppController(store) { api }

        runSuspend { controller.restoreConnection() }
        runSuspend { controller.login("jessi", "password-password") }
        val state = runSuspend { controller.selectSidebarRoute(TvRoute.Activity) }

        assertTrue(state.activityUsesContinueWatchingFallback)
        assertEquals(1, state.continueWatching.size)
    }

    @Test
    fun switchingSidebarTabsClearsAnimeButKeepsSelection() {
        val store = FakeOriginStore("https://jularr.example")
        val api = FakeApi()
        val controller = TvAppController(store) { api }

        runSuspend { controller.restoreConnection() }
        runSuspend { controller.login("jessi", "password-password") }
        runSuspend { controller.openAnime("anime") }
        val state = runSuspend { controller.selectSidebarRoute(TvRoute.Watchlist) }

        assertEquals(TvRoute.Watchlist, state.navigation.route)
        assertNull(state.anime)
        assertEquals(1, state.watchlist.size)
    }

    @Test
    fun selectingWatchlistLeavesItEmptyWithoutTheCapabilityFlag() {
        val store = FakeOriginStore("https://jularr.example")
        val api = FakeApi(advertiseWatchlist = false)
        val controller = TvAppController(store) { api }

        runSuspend { controller.restoreConnection() }
        runSuspend { controller.login("jessi", "password-password") }
        val state = runSuspend { controller.selectSidebarRoute(TvRoute.Watchlist) }

        assertEquals(TvRoute.Watchlist, state.navigation.route)
        assertEquals(0, state.watchlist.size)
    }

    @Test
    fun changingServerDropsAuthenticatedStateAndCanonicalOrigin() {
        val store = FakeOriginStore("https://jularr.example")
        val api = FakeApi()
        val controller = TvAppController(store) { api }

        runSuspend { controller.restoreConnection() }
        runSuspend { controller.login("jessi", "password-password") }
        val state = controller.changeServer()

        assertNull(store.origin)
        assertEquals(TvRoute.Setup, state.navigation.route)
        assertNull(state.account)
        assertNull(state.library)
    }

    @Test
    fun failedLoginKeepsLoginScreenAndSurfacesError() {
        val store = FakeOriginStore("https://jularr.example")
        val api = FakeApi(failLogin = true)
        val controller = TvAppController(store) { api }

        runSuspend { controller.restoreConnection() }
        val state = runSuspend { controller.login("jessi", "wrong-password") }

        assertEquals(TvRoute.Login, state.navigation.route)
        assertEquals("Invalid user name or password.", state.error)
        assertFalse(state.busy)
    }

    @Test
    fun openingEpisodeShowsDetailRouteBeforePlayback() {
        val store = FakeOriginStore("https://jularr.example")
        val api = FakeApi()
        val controller = TvAppController(store) { api }

        runSuspend { controller.restoreConnection() }
        val state = runSuspend {
            controller.openEpisode(
                episodeId = "episode",
                animeId = "anime",
            )
        }

        assertEquals(
            TvRoute.Episode("episode", "anime"),
            state.navigation.route,
        )
        assertEquals("Episode 3", state.episodePage?.detail?.title)
        assertEquals(37, state.episodePage?.progress?.percent)
        assertNull(state.episode)
    }

    private class FakeOriginStore(
        override var origin: String? = null,
    ) : TvServerOriginStore {
        override fun clear() {
            origin = null
        }
    }

    private class FakeApi(
        private val failLogin: Boolean = false,
        private val advertisePlaybackHistory: Boolean = true,
        private val advertiseWatchlist: Boolean = true,
    ) : JularrClientApi {
        override suspend fun getCapabilities() = ClientCapabilities(
            apiVersion = 2,
            minimumSupportedApiVersion = 2,
            serverVersion = "test",
            features = ClientFeatureFlags(
                library = true,
                nativeSessionAuth = true,
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
                continueWatching = true,
                playbackHistory = advertisePlaybackHistory,
                watchlist = advertiseWatchlist,
            ),
        )

        override suspend fun login(credentials: ClientLogin): ClientAccount {
            if (failLogin) {
                error("Invalid user name or password.")
            }
            return ClientAccount("profile", credentials.userName, "owner")
        }

        override suspend fun logout() = Unit

        override suspend fun getMe() = ClientAccount(
            "profile",
            "jessi",
            "owner",
        )

        override suspend fun getLibrary() = ClientLibrary(
            anime = listOf(
                de.juloc.jularr.core.model.AnimeSummary(
                    id = "anime",
                    title = "Anime",
                    localTitle = "Anime",
                    nativeTitle = null,
                    coverImageUrl = null,
                    bannerImageUrl = null,
                    episodeCount = 1,
                    seasonCount = 1,
                    seasonYear = 2026,
                    format = "TV",
                ),
            ),
        )

        override suspend fun getContinueWatching() = listOf(
            de.juloc.jularr.core.model.ContinueWatchingItem(
                kind = "episode",
                episodeId = "episode",
                animeId = "anime",
                animeTitle = "Anime",
                seasonNumber = 1,
                episodeNumber = 3,
                episodeTitle = "Episode 3",
                resumePositionMs = 444_000,
                durationMs = 1_200_000,
                percent = 37,
                updatedAtUtc = "2026-09-27T00:00:00Z",
                coverImageUrl = null,
            ),
        )

        override suspend fun getPlaybackHistory() = listOf(
            de.juloc.jularr.core.model.PlaybackHistoryItem(
                id = "history-1",
                episodeId = "episode",
                animeId = "anime",
                animeTitle = "Anime",
                seasonNumber = 1,
                episodeNumber = 3,
                episodeTitle = "Episode 3",
                startedAtUtc = "2026-09-26T00:00:00Z",
                lastPlayedAtUtc = "2026-09-27T00:00:00Z",
                positionMs = 444_000,
                durationMs = 1_200_000,
                reachedEnd = false,
            ),
        )

        override suspend fun getWatchlist() = listOf(
            WatchlistItem(
                id = "anime",
                mediaType = "anime",
                title = "Anime",
                artworkUrl = null,
                availability = "in_library",
                detailsUrl = "/Library/Anime/anime",
                addedAtUtc = "2026-09-20T00:00:00Z",
            ),
        )

        override suspend fun getAnime(animeId: String): AnimeDetail = error("unused")

        override suspend fun getEpisode(episodeId: String) = EpisodeDetail(
            id = episodeId,
            animeId = "anime",
            animeTitle = "Anime",
            title = "Episode 3",
            seasonNumber = 1,
            number = 3,
            hasMedia = true,
            activeLearningSubtitleTrackId = "sub-ja",
            learningCueCount = 12,
            learning = de.juloc.jularr.core.model.LearningCoverage(
                totalTerms = 100,
                knownTerms = 60,
                learningTerms = 20,
                newTerms = 20,
            ),
        )

        override suspend fun getProgress(episodeId: String) = EpisodeProgress(
            positionMs = 444_000,
            durationMs = 1_200_000,
            percent = 37,
            isCompleted = false,
            updatedAtUtc = null,
        )
        override suspend fun setProgress(
            episodeId: String,
            update: EpisodeProgressUpdate,
        ): EpisodeProgress = error("unused")
        override suspend fun getPlayer(episodeId: String): PlayerBootstrap = error("unused")
        override suspend fun getCues(
            episodeId: String,
            trackId: String?,
            fromMs: Int?,
            toMs: Int?,
        ): CueResponse = error("unused")
        override suspend fun getMediaAvailability(
            mediaFileId: String,
            fresh: Boolean,
        ): MediaAvailability = error("unused")
        override suspend fun getTerm(termId: String): TermDetail = error("unused")
        override suspend fun setTermState(
            termId: String,
            state: String,
        ): TermStateResult = TermStateResult(termId, state)
        override suspend fun getRootAvailability(rootId: String): RootAvailability = error("unused")
        override suspend fun testRoot(rootId: String): RootAvailability = error("unused")
        override suspend fun wakeRoot(rootId: String): RootAvailability = error("unused")
        override suspend fun getTtsPreferences(): TtsPreferences = error("unused")
        override suspend fun updateTtsPreferences(update: TtsPreferencesUpdate): TtsPreferences =
            error("unused")
        override suspend fun getSpeechModels(): SpeechModelsResponse = error("unused")
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
