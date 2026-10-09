package de.juloc.jularr.tv

import de.juloc.jularr.core.api.JularrClientApi
import java.util.UUID
import de.juloc.jularr.core.model.AnimeDetail
import de.juloc.jularr.core.model.EpisodeSummary
import de.juloc.jularr.core.model.ClientAccount
import de.juloc.jularr.core.model.ClientCapabilities
import de.juloc.jularr.core.model.ClientLibrary
import de.juloc.jularr.core.model.ContinueWatchingItem
import de.juloc.jularr.core.model.CueResponse
import de.juloc.jularr.core.model.DevicePairingPollResult
import de.juloc.jularr.core.model.DevicePairingSession
import de.juloc.jularr.core.model.PlaybackHistoryItem
import de.juloc.jularr.core.model.WatchlistItem
import de.juloc.jularr.core.model.ClientPlaybackPreferences
import de.juloc.jularr.core.model.ClientPlaybackPreferencesUpdate

data class TvEpisodeNeighbors(
    val previous: EpisodeSummary? = null,
    val next: EpisodeSummary? = null,
)

data class TvAppSnapshot(
    val navigation: TvNavigationState,
    val capabilities: ClientCapabilities? = null,
    val account: ClientAccount? = null,
    val library: ClientLibrary? = null,
    val continueWatching: List<ContinueWatchingItem> = emptyList(),
    val activity: List<PlaybackHistoryItem> = emptyList(),
    /**
     * True when the server has no `/me/playback-history` data (older server, or the
     * feature flag is off): Activity then shows Continue Watching instead of a dead
     * screen, and the UI notes why (#522 item 4).
     */
    val activityUsesContinueWatchingFallback: Boolean = false,
    val watchlist: List<WatchlistItem> = emptyList(),
    val searchQuery: String = "",
    val searchCategory: TvContentFilter = TvContentFilter.ALL,
    val playbackPreferences: ClientPlaybackPreferences? = null,
    val anime: AnimeDetail? = null,
    val episodePage: TvEpisodePageData? = null,
    val episode: TvEpisodeBundle? = null,
    val storageDecision: TvStorageDecision? = null,
    val busy: Boolean = false,
    val error: String? = null,
)

class TvAppController(
    private val settings: TvServerOriginStore,
    private val sessionStore: TvSessionStore? = null,
    private val cookiesStore: TvSessionCookieStore? = null,
    apiFactory: (String) -> JularrClientApi,
) {
    private val flow = TvClientFlow(apiFactory)

    var snapshot = TvAppSnapshot(
        navigation = TvNavigation.initial(
            hasServerOrigin = settings.origin != null,
            hasMultipleSessions = (sessionStore?.getSessions()?.size ?: 0) > 1,
            hasSavedSessions = sessionStore?.getSessions()?.isNotEmpty() == true,
        ),
    )
        private set

    suspend fun connect(rawOrigin: String): TvAppSnapshot =
        runBusy {
            val capabilities = flow.connect(rawOrigin)
            val origin = requireNotNull(flow.origin)
            settings.origin = origin

            copy(
                navigation = TvNavigation.connected(navigation),
                capabilities = capabilities,
                error = null,
            )
        }

    suspend fun login(
        userName: String,
        password: String,
    ): TvAppSnapshot =
        runBusy {
            val resolvedCapabilities = ensureConnected()
            sessionStore?.beginNewSignIn()
            cookiesStore?.loadCookies(emptyMap())
            val signedIn = flow.login(userName, password, resolvedCapabilities)
            val origin = settings.origin.orEmpty()

            sessionStore?.saveSession(
                TvSavedSession(
                    id = UUID.randomUUID().toString(),
                    serverOrigin = origin,
                    userName = signedIn.account.userName ?: userName,
                    cookies = cookiesStore?.getRawCookies() ?: emptyMap(),
                ),
            )

            copy(
                navigation = TvNavigation.signedIn(navigation),
                account = signedIn.account,
                library = signedIn.library,
                continueWatching = signedIn.continueWatching,
                anime = null,
                episodePage = null,
                episode = null,
                activity = emptyList(),
                activityUsesContinueWatchingFallback = false,
                watchlist = emptyList(),
                playbackPreferences = runCatching { flow.loadPlaybackPreferences() }.getOrNull(),
                storageDecision = null,
                error = null,
            )
        }

    /**
     * `POST /pairing/start` (#489). Called from the Login screen's own pairing loop, not wrapped
     * in [runBusy]: it must not fight the polling loop over `snapshot.busy`/`snapshot.error`.
     */
    suspend fun startDevicePairing(): DevicePairingSession {
        sessionStore?.beginNewSignIn()
        cookiesStore?.loadCookies(emptyMap())
        return flow.startDevicePairing()
    }

    /** `POST /pairing/poll` (#489). See [startDevicePairing]. */
    suspend fun pollDevicePairing(deviceCode: String): DevicePairingPollResult =
        flow.pollDevicePairing(deviceCode)

    /**
     * Finishes sign-in once the Login screen's pairing loop sees
     * [DevicePairingPollResult.Approved]: the server already signed this connection's cookie in
     * (see [JularrClientApi.pollDevicePairing]), so this only loads the
     * same account data [login] does.
     */
    suspend fun completeDevicePairing(account: ClientAccount): TvAppSnapshot =
        runBusy {
            val resolvedCapabilities = ensureConnected()
            val signedIn = flow.completeDevicePairing(account, resolvedCapabilities)
            val origin = settings.origin.orEmpty()

            sessionStore?.saveSession(
                TvSavedSession(
                    id = UUID.randomUUID().toString(),
                    serverOrigin = origin,
                    userName = signedIn.account.userName ?: "TV User",
                    cookies = cookiesStore?.getRawCookies() ?: emptyMap(),
                ),
            )

            copy(
                navigation = TvNavigation.signedIn(navigation),
                account = signedIn.account,
                library = signedIn.library,
                continueWatching = signedIn.continueWatching,
                anime = null,
                episodePage = null,
                episode = null,
                activity = emptyList(),
                activityUsesContinueWatchingFallback = false,
                watchlist = emptyList(),
                playbackPreferences = runCatching { flow.loadPlaybackPreferences() }.getOrNull(),
                storageDecision = null,
                error = null,
            )
        }

    suspend fun restoreConnection(): TvAppSnapshot =
        runBusy {
            val sessions = sessionStore?.getSessions().orEmpty()
            val activeSession = sessionStore?.getActiveSession()
            val origin = activeSession?.serverOrigin ?: settings.origin
                ?: return@runBusy copy(
                    navigation = TvNavigation.changeServer(),
                    error = null,
                )

            val capabilities = flow.connect(origin)

            if (sessions.size > 1 && navigation.route == TvRoute.Login) {
                return@runBusy copy(
                    navigation = TvNavigation.accountSelect(),
                    capabilities = capabilities,
                    error = null,
                )
            }

            if (activeSession != null) {
                cookiesStore?.loadCookies(activeSession.cookies)
                val signedIn = runCatching { flow.restoreSession(capabilities) }.getOrNull()
                if (signedIn != null) {
                    settings.origin = activeSession.serverOrigin
                    return@runBusy copy(
                        navigation = TvNavigation.signedIn(navigation),
                        capabilities = capabilities,
                        account = signedIn.account,
                        library = signedIn.library,
                        continueWatching = signedIn.continueWatching,
                        playbackPreferences = runCatching { flow.loadPlaybackPreferences() }.getOrNull(),
                        error = null,
                    )
                }
            }

            cookiesStore?.loadCookies(emptyMap())
            copy(
                navigation = TvNavigation.connected(navigation),
                capabilities = capabilities,
                error = null,
            )
        }

    suspend fun selectSavedSession(session: TvSavedSession): TvAppSnapshot {
        snapshot = snapshot.copy(
            navigation = TvNavigation.accountSelect(),
            capabilities = null,
            account = null,
            searchQuery = "",
            searchCategory = TvContentFilter.ALL,
            library = null,
            continueWatching = emptyList(),
            watchlist = emptyList(),
            playbackPreferences = null,
            activity = emptyList(),
            activityUsesContinueWatchingFallback = false,
            anime = null,
            episodePage = null,
            episode = null,
            storageDecision = null,
        )
        return runBusy {
            val saved = sessionStore?.getSessions()?.firstOrNull { it.id == session.id }
                ?: session.takeIf { sessionStore == null }
                ?: error("This account is no longer saved.")
            sessionStore?.setActiveSessionId(saved.id)
            cookiesStore?.loadCookies(saved.cookies)
            settings.origin = saved.serverOrigin

            val capabilities = flow.connect(saved.serverOrigin)
            val signedIn = flow.restoreSession(capabilities)

            copy(
                navigation = TvNavigation.signedIn(navigation),
                capabilities = capabilities,
                account = signedIn.account,
                library = signedIn.library,
                continueWatching = signedIn.continueWatching,
                watchlist = emptyList(),
                playbackPreferences = runCatching { flow.loadPlaybackPreferences() }.getOrNull(),
                activity = emptyList(),
                activityUsesContinueWatchingFallback = false,
                anime = null,
                episodePage = null,
                episode = null,
                storageDecision = null,
                error = null,
            )
        }
    }

    suspend fun changePlaybackPreferences(update: ClientPlaybackPreferencesUpdate): TvAppSnapshot =
        runBusy {
            copy(
                playbackPreferences = flow.updatePlaybackPreferences(update),
                error = null,
            )
        }

    fun openAccountSelect(): TvAppSnapshot {
        snapshot = snapshot.copy(
            navigation = if (snapshot.account != null) {
                TvNavigation.openAccountSelect(snapshot.navigation)
            } else {
                TvNavigation.accountSelect()
            },
            error = null,
        )
        return snapshot
    }

    fun openProfileSelect(): TvAppSnapshot {
        snapshot = snapshot.copy(
            navigation = if (snapshot.account != null) {
                TvNavigation.openProfileSelect(snapshot.navigation)
            } else {
                TvNavigation.accountSelect()
            },
            error = null,
        )
        return snapshot
    }

    /**
     * Switches to one of the four sidebar destinations (#522). Home and Activity reload
     * their data on the way in (Home's library/continue-watching, Activity's playback
     * history or its Continue Watching fallback) rather than needing a manual refresh
     * control.
     */
    suspend fun selectSidebarRoute(route: TvRoute): TvAppSnapshot =
        runBusy {
            val withContent = when (route) {
                TvRoute.Home -> copy(
                    library = flow.refreshLibrary(),
                    continueWatching = if (capabilities?.features?.continueWatching == true) {
                        flow.loadContinueWatching()
                    } else {
                        emptyList()
                    },
                )

                TvRoute.Settings -> {
                    val preferences = runCatching { flow.loadPlaybackPreferences() }
                    copy(
                        playbackPreferences = preferences.getOrNull(),
                        error = preferences.exceptionOrNull()?.message,
                    )
                }

                TvRoute.Watchlist -> copy(
                    watchlist = if (capabilities?.features?.watchlist == true) {
                        flow.loadWatchlist()
                    } else {
                        emptyList()
                    },
                    continueWatching = if (capabilities?.features?.continueWatching == true) {
                        flow.loadContinueWatching()
                    } else {
                        emptyList()
                    },
                )

                else -> this
            }

            withContent.copy(
                navigation = TvNavigation.openSidebarRoute(navigation, route),
                anime = null,
                episodePage = null,
                episode = null,
                storageDecision = null,
                error = if (route == TvRoute.Settings) withContent.error else null,
            )
        }

    fun updateSearchQuery(query: String): TvAppSnapshot {
        snapshot = snapshot.copy(searchQuery = query)
        return snapshot
    }

    fun updateSearchCategory(category: TvContentFilter): TvAppSnapshot {
        snapshot = snapshot.copy(searchCategory = category)
        return snapshot
    }

    fun openSearch(): TvAppSnapshot {
        snapshot = snapshot.copy(
            navigation = TvNavigation.openSearch(snapshot.navigation),
            error = null,
        )
        return snapshot
    }

    suspend fun openAnime(animeId: String): TvAppSnapshot =
        runBusy {
            val loaded = flow.loadAnime(animeId)
            copy(
                navigation = TvNavigation.openAnime(navigation, animeId),
                anime = loaded,
                episodePage = null,
                episode = null,
                storageDecision = null,
                error = null,
            )
        }

    suspend fun openEpisode(
        episodeId: String,
        animeId: String,
    ): TvAppSnapshot =
        runBusy {
            copy(
                navigation = TvNavigation.openEpisode(
                    navigation,
                    episodeId,
                    animeId,
                ),
                episodePage = flow.loadEpisodePage(episodeId),
                episode = null,
                storageDecision = null,
                error = null,
            )
        }

    suspend fun playEpisode(
        episodeId: String,
        animeId: String,
    ): TvAppSnapshot =
        runBusy {
            val bundle = flow.loadEpisode(episodeId)
            val decision = bundle.bootstrap.media?.availability?.let {
                TvStorageRecoveryPolicy.decide(it, elapsedMs = 0)
            }

            val advancing = navigation.route is TvRoute.Player
            val fromEpisode = navigation.previous.lastOrNull() is TvRoute.Episode
            copy(
                navigation = if (advancing) {
                    TvNavigation.nextPlayer(navigation, episodeId, animeId)
                } else {
                    TvNavigation.openPlayer(navigation, episodeId, animeId)
                },
                episodePage = if (advancing && fromEpisode) {
                    flow.loadEpisodePage(episodeId)
                } else {
                    episodePage
                },
                episode = bundle,
                storageDecision = decision?.takeUnless {
                    it.primaryAction == TvStorageAction.PLAY
                },
                error = null,
            )
        }

    suspend fun episodeNeighbors(episodeId: String, animeId: String): TvEpisodeNeighbors {
        val details = snapshot.anime?.takeIf { it.id == animeId } ?: flow.loadAnime(animeId)
        val ordered = details.seasons
            .flatMap { it.episodes }
            .sortedWith(compareBy<EpisodeSummary> { it.seasonNumber }.thenBy { it.number })
        val currentIndex = ordered.indexOfFirst { it.id == episodeId }
        if (currentIndex < 0) return TvEpisodeNeighbors()
        return TvEpisodeNeighbors(
            previous = ordered.getOrNull(currentIndex - 1)?.takeIf { it.hasMedia },
            next = ordered.getOrNull(currentIndex + 1)?.takeIf { it.hasMedia },
        )
    }

    suspend fun refreshEpisodeStorage(
        elapsedMs: Long,
    ): TvAppSnapshot =
        runBusy {
            val bundle = episode
                ?: error("No TV episode is loaded.")
            val media = bundle.bootstrap.media
                ?: error("This episode has no media.")
            val availability = flow.refreshMediaAvailability(
                mediaFileId = media.mediaFileId,
                fresh = true,
            )
            val decision = TvStorageRecoveryPolicy.decide(
                availability,
                elapsedMs,
            )
            copy(
                storageDecision = decision.takeUnless {
                    it.primaryAction == TvStorageAction.PLAY
                },
                error = null,
            )
        }

    suspend fun wakeEpisodeStorage(): TvAppSnapshot =
        runBusy {
            val media = episode?.bootstrap?.media
                ?: error("No TV media is loaded.")
            val rootId = media.availability.rootId
                ?: error("Wake-on-LAN is not available for this media.")
            if (!media.availability.canWake) {
                error("Wake-on-LAN is not available for this profile.")
            }

            flow.wakeRoot(rootId)
            copy(
                storageDecision = TvStorageRecoveryPolicy.wakeDecision(
                    media.availability,
                ),
                error = null,
            )
        }

    suspend fun refreshCueWindow(
        positionMs: Long,
    ): TvAppSnapshot =
        runBusy {
            val bundle = episode
                ?: error("No TV episode is loaded.")
            val trackId = bundle.bootstrap.activeLearningSubtitleTrackId
                ?: return@runBusy copy(error = null)
            val refreshed = flow.loadCueWindow(
                episodeId = bundle.bootstrap.episode.id,
                trackId = trackId,
                positionMs = positionMs,
            )
            copy(
                episode = bundle.copy(cues = refreshed),
                error = null,
            )
        }

    suspend fun saveProgress(
        write: TvProgressWrite,
        episodeId: String,
    ) {
        val progress = flow.saveProgress(
            episodeId = episodeId,
            positionMs = write.positionMs,
            durationMs = write.durationMs,
            completed = write.completed,
        )
        val page = snapshot.episodePage
        if (page?.detail?.id == episodeId) {
            snapshot = snapshot.copy(
                episodePage = page.copy(progress = progress),
            )
        }
    }

    suspend fun setTermState(
        termId: String,
        state: String,
    ): TvAppSnapshot =
        runBusy {
            flow.setTermState(termId, state)
            val bundle = episode
            if (bundle == null) {
                copy(error = null)
            } else {
                copy(
                    episode = bundle.copy(
                        cues = updateCueTermState(
                            bundle.cues,
                            termId,
                            state,
                        ),
                    ),
                    error = null,
                )
            }
        }

    suspend fun signOut(): TvAppSnapshot =
        runBusy {
            runCatching { flow.logout() }
            val active = sessionStore?.getActiveSession()
            if (active != null) {
                sessionStore.removeSession(active.id)
            }
            cookiesStore?.loadCookies(emptyMap())

            val remaining = sessionStore?.getSessions().orEmpty()
            val nextRoute = if (remaining.isNotEmpty()) TvRoute.AccountSelect else TvRoute.Login

            copy(
                navigation = TvNavigationState(nextRoute),
                account = null,
                library = null,
                continueWatching = emptyList(),
                activity = emptyList(),
                activityUsesContinueWatchingFallback = false,
                watchlist = emptyList(),
                searchQuery = "",
                searchCategory = TvContentFilter.ALL,
                playbackPreferences = null,
                anime = null,
                episodePage = null,
                episode = null,
                storageDecision = null,
                error = null,
            )
        }

    fun changeServer(): TvAppSnapshot {
        sessionStore?.beginNewSignIn()
        cookiesStore?.loadCookies(emptyMap())
        settings.clear()
        snapshot = TvAppSnapshot(
            navigation = TvNavigation.changeServer(),
        )
        return snapshot
    }

    fun back(): TvAppSnapshot? {
        val nextNavigation = TvNavigation.back(snapshot.navigation)
            ?: return null

        val stillBrowsingAnime = nextNavigation.route is TvRoute.Anime ||
            nextNavigation.route is TvRoute.Episode ||
            nextNavigation.route is TvRoute.Player

        snapshot = snapshot.copy(
            navigation = nextNavigation,
            anime = if (stillBrowsingAnime) snapshot.anime else null,
            episodePage = when (nextNavigation.route) {
                is TvRoute.Episode,
                is TvRoute.Player,
                -> snapshot.episodePage
                else -> null
            },
            episode = if (nextNavigation.route is TvRoute.Player) {
                snapshot.episode
            } else {
                null
            },
            storageDecision = null,
            error = null,
        )
        return snapshot
    }

    fun reportError(throwable: Throwable): TvAppSnapshot {
        snapshot = snapshot.copy(
            busy = false,
            error = throwable.message ?: "Jularr TV request failed.",
        )
        return snapshot
    }

    private suspend fun runBusy(
        action: suspend TvAppSnapshot.() -> TvAppSnapshot,
    ): TvAppSnapshot {
        snapshot = snapshot.copy(busy = true, error = null)
        return try {
            // The action runs against the busy snapshot, so its result still
            // carries busy = true. Clear it on the value we store *and* return:
            // TvAppHost assigns the returned snapshot directly to the UI state.
            snapshot.action().copy(busy = false).also { snapshot = it }
        } catch (throwable: Throwable) {
            reportError(throwable)
        }
    }

    private fun updateCueTermState(
        cues: CueResponse,
        termId: String,
        state: String,
    ): CueResponse =
        cues.copy(
            cues = cues.cues.map { cue ->
                cue.copy(
                    tokens = cue.tokens.map { token ->
                        if (token.termId == termId) {
                            token.copy(state = state)
                        } else {
                            token
                        }
                    },
                )
            },
        )

    private suspend fun TvAppSnapshot.ensureConnected(): ClientCapabilities {
        capabilities?.let { return it }
        val origin = settings.origin
            ?: error("Configure an Jularr server first.")
        val resolved = flow.connect(origin)
        snapshot = snapshot.copy(capabilities = resolved)
        return resolved
    }
}
