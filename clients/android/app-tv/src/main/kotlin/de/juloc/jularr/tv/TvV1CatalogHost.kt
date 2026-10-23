package de.juloc.jularr.tv

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.catalog.HttpTvCatalogV1Client
import de.juloc.jularr.core.api.catalog.TvContinueItemV1
import de.juloc.jularr.core.api.catalog.TvDiscoverItemV1
import de.juloc.jularr.core.api.catalog.TvCatalogV1Client
import de.juloc.jularr.core.api.catalog.TvWorkAvailabilityV1
import de.juloc.jularr.core.api.catalog.TvPersonV1
import de.juloc.jularr.core.api.catalog.TvWorkCardV1
import de.juloc.jularr.core.api.catalog.TvWorkCreditV1
import de.juloc.jularr.core.api.catalog.TvWorkEpisodeV1
import de.juloc.jularr.core.api.tvlegacy.HttpTvV1LegacyFeaturesClient
import de.juloc.jularr.core.api.playbackextras.HttpTvV1PlaybackExtrasClient
import de.juloc.jularr.core.api.profile.HttpTvV1ProfileClient
import de.juloc.jularr.core.api.profile.TvV1ProfileClient
import de.juloc.jularr.core.model.AnimeDetail
import de.juloc.jularr.core.model.ClientLibrary
import de.juloc.jularr.core.model.InstanceCapabilities
import de.juloc.jularr.core.model.WatchlistItem
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Transitional data host for the *original* TV Compose screens, not a replacement TV design.
 * /api/v1 never goes through the old cookie-based TvAppController or TvClientFlow.
 * The actual Media3 playback session adapter remains a separate follow-up.
 */
@Composable
fun TvV1CatalogHost(
    serverOrigin: String,
    capabilities: InstanceCapabilities,
    deviceSession: TvV1DeviceSession,
    onAuthenticationExpired: () -> Unit,
    onProfileChanged: () -> Unit,
    onSignedOut: () -> Unit,
    onChangeServer: () -> Unit,
    onSwitchAccount: () -> Unit,
) {
    val catalog: TvCatalogV1Client = remember(serverOrigin, deviceSession) {
        HttpTvCatalogV1Client(serverOrigin, deviceSession.authenticatedTokenStore())
    }
    val flow = remember(serverOrigin, catalog, capabilities) {
        TvV1ClientFlow(catalog, capabilities)
    }
    val profileApi: TvV1ProfileClient = remember(serverOrigin, deviceSession) {
        HttpTvV1ProfileClient(serverOrigin, deviceSession.authenticatedTokenStore())
    }
    val scope = rememberCoroutineScope()
    val playbackExtras = remember(serverOrigin, deviceSession) {
        HttpTvV1PlaybackExtrasClient(serverOrigin, deviceSession.authenticatedTokenStore())
    }
    val legacy = remember(serverOrigin, deviceSession) {
        HttpTvV1LegacyFeaturesClient(serverOrigin, deviceSession.authenticatedTokenStore())
    }
    val focusMemory = remember(serverOrigin) { TvFocusMemory() }
    var navigation by remember(serverOrigin) { mutableStateOf(TvV1NavigationState()) }
    val page = navigation.page
    fun openPage(
        next: TvV1CatalogPage,
        workId: Long? = null,
        personId: Long? = null,
        workUnitId: Long? = null,
    ) {
        navigation = TvV1AppController.open(navigation, next, workId, personId, workUnitId)
    }
    var searchQuery by remember(serverOrigin) { mutableStateOf("") }
    var category by remember(serverOrigin) { mutableStateOf(TvContentFilter.ALL) }
    var reload by remember(serverOrigin) { mutableIntStateOf(0) }
    var loading by remember(serverOrigin) { mutableStateOf(false) }
    var settingsSignOutRequested by remember(serverOrigin) { mutableStateOf(false) }
    var error by remember(serverOrigin) { mutableStateOf<String?>(null) }
    var library by remember(serverOrigin) { mutableStateOf(ClientLibrary(emptyList())) }
    var homeNextPage by remember(serverOrigin) { mutableStateOf<Int?>(null) }
    var searchNextPage by remember(serverOrigin) { mutableStateOf<Int?>(null) }
    var watchlistNextPage by remember(serverOrigin) { mutableStateOf<Int?>(null) }
    var fetchingNextPage by remember(serverOrigin) { mutableStateOf(false) }
    var pagingError by remember(serverOrigin) { mutableStateOf<String?>(null) }
    var searchResults by remember(serverOrigin) { mutableStateOf(ClientLibrary(emptyList())) }
    var continueWatching by remember(serverOrigin) { mutableStateOf<List<TvContinueItemV1>>(emptyList()) }
    var showContinueWatching by remember(serverOrigin) { mutableStateOf(true) }
    var homeShelfOrder by remember(serverOrigin) { mutableStateOf(listOf(1, 4, 5)) }
    var discoverHomeFeeds by remember(serverOrigin) { mutableStateOf<List<TvV1HomeVideoFeed>>(emptyList()) }
    var selectedDiscoverItem by remember(serverOrigin) { mutableStateOf<TvDiscoverItemV1?>(null) }
    var watchlist by remember(serverOrigin) { mutableStateOf<List<WatchlistItem>>(emptyList()) }
    var details by remember(serverOrigin) { mutableStateOf<AnimeDetail?>(null) }
    var isWatchlisted by remember(serverOrigin) { mutableStateOf(false) }
    var watchlistBusy by remember(serverOrigin) { mutableStateOf(false) }
    var watchlistError by remember(serverOrigin) { mutableStateOf<String?>(null) }
    var detailedTitle by remember(serverOrigin) { mutableStateOf("") }
    var credits by remember(serverOrigin) { mutableStateOf<List<TvWorkCreditV1>>(emptyList()) }
    var selectedPersonId by remember(serverOrigin) { mutableStateOf<Long?>(null) }
    var selectedPerson by remember(serverOrigin) { mutableStateOf<TvPersonV1?>(null) }
    var personWorks by remember(serverOrigin) { mutableStateOf<List<TvWorkCardV1>>(emptyList()) }
    var fractionalEpisodes by remember(serverOrigin) {
        mutableStateOf<List<de.juloc.jularr.core.api.catalog.TvWorkEpisodeV1>>(emptyList())
    }
    var detailWorkId by remember(serverOrigin) { mutableStateOf<Long?>(null) }
    var selectedWorkUnitId by remember(serverOrigin) { mutableStateOf<Long?>(null) }
    var episodeByUnit by remember(serverOrigin) { mutableStateOf<Map<Long, TvWorkEpisodeV1>>(emptyMap()) }
    var detailAssetByUnit by remember(serverOrigin) { mutableStateOf<Map<String, Long?>>(emptyMap()) }
    var playableMovieAssets by remember(serverOrigin) { mutableStateOf<List<TvWorkAvailabilityV1>>(emptyList()) }
    var selectedEpisodeId by remember(serverOrigin) { mutableStateOf<String?>(null) }
    var selectedAssetId by remember(serverOrigin) { mutableStateOf<Long?>(null) }

    fun playAsset(mediaAssetId: Long?, episodeId: String? = null) {
        selectedEpisodeId = episodeId
        selectedWorkUnitId = episodeId?.toLongOrNull()
        selectedAssetId = mediaAssetId
        if (mediaAssetId == null) {
            openPage(TvV1CatalogPage.PlaybackPending)
            return
        }
        if (!capabilities.hasModule("Playback") || !capabilities.hasPermission("profile.playback")) {
            error = "Dieses Profil hat keine Wiedergabeberechtigung."
            return
        }
        navigation = TvV1AppController.playback(
            navigation, mediaAssetId, detailWorkId, selectedWorkUnitId,
        )
    }

    fun openWork(rawId: String) {
        val workId = rawId.toLongOrNull()
        if (workId == null || workId <= 0) {
            error = "Ungültige Work-ID der Jularr-Instanz."
            return
        }
        detailWorkId = workId
        details = null
        episodeByUnit = emptyMap()
        selectedWorkUnitId = null
        selectedEpisodeId = null
        error = null
        navigation = TvV1AppController.work(navigation, workId)
    }

    fun openEpisode(rawId: String) {
        val unitId = rawId.toLongOrNull()
        if (unitId == null || !episodeByUnit.containsKey(unitId)) {
            error = "Diese Folge existiert nicht im aktuellen Work."
            return
        }
        val workId = requireNotNull(detailWorkId)
        navigation = TvV1AppController.episode(
            navigation, workId, unitId, episodeByUnit.keys,
        )
        selectedWorkUnitId = unitId
    }

    fun loadNextPage(which: TvV1CatalogPage) {
        if (fetchingNextPage || page != which) return
        val next = when (which) {
            TvV1CatalogPage.Home -> homeNextPage
            TvV1CatalogPage.Search -> searchNextPage
            TvV1CatalogPage.Watchlist -> watchlistNextPage
            else -> null
        } ?: return
        val requestedQuery = searchQuery
        fetchingNextPage = true
        pagingError = null
        scope.launch {
            try {
                if (which == TvV1CatalogPage.Watchlist) {
                    val result = flow.watchlistPage(next)
                    if (page == which) {
                        val cards = TvV1LegacyUiModels.watchlist(result.items)
                        watchlist = (watchlist + cards).distinctBy { it.id }
                        watchlistNextPage = if (result.hasMore) next + 1 else null
                    }
                } else {
                    val result = flow.videoPage(next, if (which == TvV1CatalogPage.Search) requestedQuery else null)
                    if (page == which && (which != TvV1CatalogPage.Search || searchQuery == requestedQuery)) {
                        val added = TvV1LegacyUiModels.videoLibrary(result.items).anime
                        if (which == TvV1CatalogPage.Home) {
                            library = ClientLibrary((library.anime + added).distinctBy { it.id })
                            homeNextPage = if (result.hasMore) next + 1 else null
                        } else {
                            searchResults = ClientLibrary((searchResults.anime + added).distinctBy { it.id })
                            searchNextPage = if (result.hasMore) next + 1 else null
                        }
                    }
                }
            } catch (failure: AppApiException) {
                if (failure.statusCode == 401) onAuthenticationExpired()
                else pagingError = "Weitere Medien: ${failure.code} (HTTP ${failure.statusCode})"
            } catch (failure: Exception) {
                pagingError = failure.message ?: "Weitere Medien konnten nicht geladen werden."
            } finally {
                fetchingNextPage = false
            }
        }
    }

    fun back() {
        error = null
        val restored = TvV1AppController.back(navigation)
        navigation = restored
        val restoredWorkId = restored.current.workId
        if (restoredWorkId != null && restoredWorkId != detailWorkId) {
            detailWorkId = restoredWorkId
            details = null // Reload selected Work's real metadata, not the last opened Work.
            episodeByUnit = emptyMap()
        }
        restored.current.personId?.let { selectedPersonId = it }
        restored.current.workUnitId?.let { selectedWorkUnitId = it }
    }

    androidx.compose.runtime.LaunchedEffect(settingsSignOutRequested) {
        if (settingsSignOutRequested) {
            try {
                profileApi.signOut()
                onSignedOut()
            } catch (failure: Exception) {
                if (failure is AppApiException && failure.statusCode == 401) onAuthenticationExpired()
                else error = failure.message ?: "Die Session konnte nicht abgemeldet werden."
            } finally {
                settingsSignOutRequested = false
            }
        }
    }

    // TvPlayerScreen handles Back itself so it can end its /api/v1 session first.
    BackHandler(enabled = page != TvV1CatalogPage.Home && page != TvV1CatalogPage.Player) { back() }

    LaunchedEffect(serverOrigin, page, searchQuery, detailWorkId, selectedPersonId, reload) {
        error = null
        loading = true
        try {
            when (page) {
                TvV1CatalogPage.Home -> {
                    val home = flow.home()
                    library = home.library
                    continueWatching = home.continueWatching
                    showContinueWatching = home.showContinueWatching
                    homeShelfOrder = home.shelfOrder
                    homeNextPage = if (home.hasMoreVideo) 2 else null
                    pagingError = null
                }
                TvV1CatalogPage.Search -> {
                    if (searchQuery.isNotBlank()) delay(300)
                    searchResults = flow.search(searchQuery)
                    searchNextPage = if (searchResults.anime.size == 100) 2 else null
                    pagingError = null
                }
                TvV1CatalogPage.Watchlist -> {
                    watchlist = flow.watchlist()
                    watchlistNextPage = if (watchlist.size == 100) 2 else null
                    pagingError = null
                }
                TvV1CatalogPage.Details -> {
                    val result = flow.work(requireNotNull(detailWorkId))
                    isWatchlisted = result.isWatchlisted
                    watchlistError = null
                    detailedTitle = result.title
                    playableMovieAssets = result.availableMovieAssets
                    fractionalEpisodes = result.fractionalEpisodes
                    episodeByUnit = result.episodeByUnit
                    detailAssetByUnit = result.assetByUnit
                    details = result.detail
                }
                TvV1CatalogPage.Episode -> Unit
                TvV1CatalogPage.Credits -> {
                    val workId = requireNotNull(detailWorkId)
                    credits = catalog.credits(workId)
                }
                TvV1CatalogPage.Person -> {
                    val personId = requireNotNull(selectedPersonId)
                    selectedPerson = catalog.person(personId)
                    personWorks = loadCatalogPages { n -> catalog.personWorks(personId, page = n, pageSize = 100) }
                        .filter { TvV1LegacyUiModels.isVideo(it.mediaTypeId) }
                }
                TvV1CatalogPage.PlaybackPending, TvV1CatalogPage.Player,
                TvV1CatalogPage.MovieEditions, TvV1CatalogPage.Settings,
                TvV1CatalogPage.ProfileSelect, TvV1CatalogPage.Discover,
                TvV1CatalogPage.Statistics, TvV1CatalogPage.Sessions -> Unit
            }
        } catch (cancel: CancellationException) {
            throw cancel
        } catch (failure: AppApiException) {
            if (failure.statusCode == 401) {
                onAuthenticationExpired()
                return@LaunchedEffect
            }
            error = "Jularr-API: ${failure.code} (HTTP ${failure.statusCode})"
        } catch (failure: Exception) {
            error = failure.message ?: "Die Medien konnten nicht geladen werden."
        } finally {
            loading = false
        }
    }

    // Remote rankings are optional: the library/Continue sections are independent.
    // Refresh only while Home is mounted; scoped concurrency and timeout are
    // implemented in the typed V1 flow, not in the Compose presentation layer.
    LaunchedEffect(catalog, capabilities, page, homeShelfOrder, reload) {
        if (page != TvV1CatalogPage.Home || 5 !in homeShelfOrder ||
            !capabilities.hasModule("Library") ||
            !capabilities.hasPermission("profile.media.read")
        ) {
            discoverHomeFeeds = emptyList()
            return@LaunchedEffect
        }
        discoverHomeFeeds = emptyList()
        try {
            TvV1HomeDiscoverFlow(catalog).load { discoverHomeFeeds = it }
        } catch (cancel: CancellationException) {
            throw cancel
        } catch (failure: AppApiException) {
            if (failure.statusCode == 401) onAuthenticationExpired()
            // A provider error must not replace local library content.
        } catch (_: Exception) {
            // Optional provider content is unavailable; the local Home remains.
        }
    }

    val authorization = deviceSession.authenticatedTokenStore().current
        ?.let { mapOf("Authorization" to "Bearer $it") }.orEmpty()

    when (page) {
        TvV1CatalogPage.Home, TvV1CatalogPage.Watchlist -> {
            Row(modifier = androidx.compose.ui.Modifier.fillMaxSize()) {
                TvSidebar(
                    selected = if (page == TvV1CatalogPage.Home) TvRoute.Home else TvRoute.Watchlist,
                    focusMemory = focusMemory,
                    onSelect = { route ->
                        when (route) {
                            TvRoute.Home -> openPage(TvV1CatalogPage.Home)
                            TvRoute.Watchlist -> openPage(TvV1CatalogPage.Watchlist)
                            TvRoute.Settings -> openPage(TvV1CatalogPage.Settings)
                            TvRoute.ProfileSelect -> openPage(TvV1CatalogPage.ProfileSelect)
                            else -> Unit
                        }
                    },
                )
                Box(modifier = androidx.compose.ui.Modifier.weight(1f)) {
                    when (page) {
                        TvV1CatalogPage.Home -> TvHomeScreen(
                            library = library,
                            continueWatching = emptyList(), // Explicitly deferred: no invented WorkUnit / playback timestamps.
                            serverOrigin = serverOrigin,
                            requestHeaders = authorization,
                            error = error ?: pagingError ?: if (loading) "Bibliothek wird geladen …" else null,
                            focusMemory = focusMemory,
                            onOpenSearch = { openPage(TvV1CatalogPage.Search) },
                            onAnime = { card -> openWork(card.id) },
                            onContinueWatching = { /* Legacy bootstrap is intentionally disconnected. */ },
                            v1ContinueWatching = continueWatching,
                            showV1Continue = showContinueWatching,
                            shelfOrder = homeShelfOrder,
                            discoverFeeds = discoverHomeFeeds,
                            onDiscoverItem = { item ->
                                val local = item.workId
                                if (local != null) {
                                    openWork(local.toString())
                                } else if (item.tmdbId != null) {
                                    selectedDiscoverItem = item
                                    openPage(TvV1CatalogPage.Discover)
                                }
                            },
                            onLoadMoreVideos = if (homeNextPage != null && !fetchingNextPage) {
                                { loadNextPage(TvV1CatalogPage.Home) }
                            } else null,
                            onContinueWork = { item ->
                                detailWorkId = item.workId
                                detailedTitle = item.title
                                playAsset(item.mediaAssetId)
                            },
                        )
                        TvV1CatalogPage.Watchlist -> {
                            if (error != null) {
                                TvV1ApiMessage(
                                    "Watchlist", requireNotNull(error),
                                    onRetry = { reload += 1 },
                                    onBack = { back() },
                                )
                            } else TvWatchlistScreen(
                                entries = watchlist,
                                supported = capabilities.hasModule("Library"),
                                continueWatching = emptyList(),
                                serverOrigin = serverOrigin,
                                requestHeaders = authorization,
                                focusMemory = focusMemory,
                                onOpenAnime = ::openWork,
                                onOpenVideoWork = ::openWork,
                                onLoadMore = if (watchlistNextPage != null && !fetchingNextPage) {
                                    { loadNextPage(TvV1CatalogPage.Watchlist) }
                                } else null,
                                pagingError = pagingError,
                            )
                        }
                        else -> Unit
                    }
                }
            }
        }
                TvV1CatalogPage.Settings -> Row(modifier = androidx.compose.ui.Modifier.fillMaxSize()) {
            TvSidebar(
                selected = TvRoute.Settings,
                focusMemory = focusMemory,
                onSelect = { route ->
                    when (route) {
                        TvRoute.Home -> openPage(TvV1CatalogPage.Home)
                        TvRoute.Watchlist -> openPage(TvV1CatalogPage.Watchlist)
                        TvRoute.ProfileSelect -> openPage(TvV1CatalogPage.ProfileSelect)
                        else -> Unit
                    }
                },
            )
            Box(modifier = androidx.compose.ui.Modifier.weight(1f)) {
                TvV1SettingsHost(
                    serverOrigin = serverOrigin,
                    error = error,
                    playbackExtras = playbackExtras,
                    onUnauthorized = onAuthenticationExpired,
                    onSwitchProfile = { openPage(TvV1CatalogPage.ProfileSelect) },
                    onSwitchAccount = onSwitchAccount,
                    onOpenStatistics = if (capabilities.hasModule("Statistics") &&
                        capabilities.hasPermission("profile.media.read")
                    ) {{ openPage(TvV1CatalogPage.Statistics) }} else null,
                    onOpenSessions = if (capabilities.hasPermission("session.self.read")) {
                        { openPage(TvV1CatalogPage.Sessions) }
                    } else null,
                    onSignOut = {
                        if (capabilities.hasPermission("session.self.logout")) {
                            error = null
                            settingsSignOutRequested = true
                        } else error = "Abmeldung ist für diese Session nicht freigegeben."
                    },
                    onChangeServer = onChangeServer,
                )
            }
        }
        TvV1CatalogPage.Sessions -> TvV1SessionsScreen(
            api = profileApi,
            canManageSessions = capabilities.hasPermission("session.self.manage"),
            onUnauthorized = onAuthenticationExpired,
            onBack = ::back,
        )
        TvV1CatalogPage.Statistics -> TvV1StatisticsScreen(
            api = legacy,
            onOpenWork = { id -> openWork(id.toString()) },
            onUnauthorized = onAuthenticationExpired,
            onBack = ::back,
        )
        TvV1CatalogPage.ProfileSelect -> TvV1ProfilesScreen(
            api = profileApi,
            onSwitched = onProfileChanged,
            onBack = ::back,
            onUnauthorized = onAuthenticationExpired,
        )
        TvV1CatalogPage.Search -> Row(modifier = androidx.compose.ui.Modifier.fillMaxSize()) {
            TvSidebar(
                selected = TvRoute.Home,
                focusMemory = focusMemory,
                onSelect = { route ->
                    when (route) {
                        TvRoute.Home -> openPage(TvV1CatalogPage.Home)
                        TvRoute.Watchlist -> openPage(TvV1CatalogPage.Watchlist)
                        TvRoute.Settings -> openPage(TvV1CatalogPage.Settings)
                        TvRoute.ProfileSelect -> openPage(TvV1CatalogPage.ProfileSelect)
                        else -> Unit
                    }
                },
            )
            Box(modifier = androidx.compose.ui.Modifier.weight(1f)) {
                if (error != null) {
                    TvV1ApiMessage("Suche", requireNotNull(error), { reload += 1 }, ::back)
                } else {
                    TvSearchScreen(
                        library = searchResults,
                        query = searchQuery,
                        selectedFilter = category,
                        serverOrigin = serverOrigin,
                        requestHeaders = authorization,
                        focusMemory = focusMemory,
                        onQueryChange = { searchQuery = it },
                        onFilterChange = { category = it },
                        onAnime = { card -> openWork(card.id) },
                        serverQueryResults = true,
                        pagingError = pagingError,
                        onOpenDiscover = {
                            selectedDiscoverItem = null
                            openPage(TvV1CatalogPage.Discover)
                        },
                        onLoadMore = if (searchNextPage != null && !fetchingNextPage) {
                            { loadNextPage(TvV1CatalogPage.Search) }
                        } else null,
                    )
                }
            }
        }
        TvV1CatalogPage.Discover -> TvV1DiscoverScreen(
            catalog = catalog,
            initialItem = selectedDiscoverItem,
            serverOrigin = serverOrigin,
            requestHeaders = authorization,
            onLocalWork = { id -> openWork(id.toString()) },
            onBack = ::back,
            onUnauthorized = onAuthenticationExpired,
        )
        TvV1CatalogPage.Details -> {
            when {
                error != null -> TvV1ApiMessage("Mediendetails", requireNotNull(error), { reload += 1 }, ::back)
                details != null -> {
                    Box(modifier = androidx.compose.ui.Modifier.fillMaxSize()) {
                        TvAnimeScreen(
                            anime = requireNotNull(details),
                            continueWatching = emptyList(),
                            focusMemory = focusMemory,
                            serverOrigin = serverOrigin,
                            requestHeaders = authorization,
                            onEpisode = { episode -> openEpisode(episode.id) },
                            onPlayEpisode = { episode ->
                                playAsset(detailAssetByUnit[episode.id], episode.id)
                            },
                            onBack = ::back,
                            isWatchlisted = isWatchlisted,
                            onOpenCredits = {
                                openPage(TvV1CatalogPage.Credits, workId = detailWorkId)
                            },
                            specialEpisodes = fractionalEpisodes,
                            onPlaySpecialEpisode = { episode ->
                                playAsset(episode.playableMediaAssetId(), episode.id.toString())
                            },
                            onToggleWatchlist = if (
                                capabilities.hasPermission("profile.watchlist.update") && !watchlistBusy
                            ) {{
                                val targetWork = requireNotNull(detailWorkId)
                                val targetValue = !isWatchlisted
                                watchlistBusy = true
                                scope.launch {
                                    try {
                                        check(catalog.setWatchlisted(targetWork, targetValue)) {
                                            "Jularr hat die Watchlist-Änderung nicht bestätigt."
                                        }
                                        isWatchlisted = targetValue
                                        watchlistError = null
                                    } catch (failure: AppApiException) {
                                        if (failure.statusCode == 401) onAuthenticationExpired()
                                        else watchlistError = "Watchlist: ${failure.code} (HTTP ${failure.statusCode})"
                                    } catch (failure: Exception) {
                                        watchlistError = failure.message ?: "Watchlist-Änderung fehlgeschlagen."
                                    } finally { watchlistBusy = false }
                                }
                            }} else null,
                            onPlayMovie = if (playableMovieAssets.isNotEmpty()) {
                                {
                                    if (playableMovieAssets.size == 1) {
                                        playAsset(playableMovieAssets.single().id)
                                    } else {
                                        openPage(TvV1CatalogPage.MovieEditions)
                                    }
                                }
                            } else null,
                        )
                        if (watchlistError != null) {
                            Text(
                                requireNotNull(watchlistError),
                                color = MaterialTheme.colorScheme.error,
                            )
                        }

                    }
                }
                !loading -> TvV1ApiMessage(
                    detailedTitle.ifEmpty { "Mediendetails" },
                    "Diese Medienart wird vom ursprünglichen TV-Videoplayer noch nicht unterstützt.",
                    onRetry = { reload += 1 },
                    onBack = ::back,
                )
                else -> TvV1Waiting("Mediendetails werden geladen …")
            }
        }
        TvV1CatalogPage.Episode -> {
            val episode = selectedWorkUnitId?.let(episodeByUnit::get)
            val work = details
            if (work == null || episode == null) {
                TvV1ApiMessage(
                    "Episodendetails", "Die Folge wurde nicht im aktuellen Work gefunden.",
                    onRetry = { back() }, onBack = ::back,
                )
            } else TvV1EpisodeDetailScreen(
                work = work,
                episode = episode,
                serverOrigin = serverOrigin,
                requestHeaders = authorization,
                onPlay = { playAsset(episode.playableMediaAssetId(), episode.id.toString()) },
                onBack = ::back,
            )
        }
        TvV1CatalogPage.Credits -> TvV1CreditsScreen(
            title = detailedTitle,
            credits = credits,
            loading = loading,
            error = error,
            serverOrigin = serverOrigin,
            requestHeaders = authorization,
            onPerson = { id ->
                selectedPersonId = id
                selectedPerson = null
                personWorks = emptyList()
                openPage(TvV1CatalogPage.Person, workId = detailWorkId, personId = id)
            },
            onRetry = { reload += 1 },
            onBack = ::back,
        )
        TvV1CatalogPage.Person -> TvV1PersonScreen(
            person = selectedPerson,
            works = personWorks,
            loading = loading,
            error = error,
            serverOrigin = serverOrigin,
            requestHeaders = authorization,
            onWork = { id -> openWork(id.toString()) },
            onRetry = { reload += 1 },
            onBack = ::back,
        )
        TvV1CatalogPage.MovieEditions -> TvCenteredPanel(
            title = "Filmversion wählen",
            description = detailedTitle,
        ) {
            playableMovieAssets.forEach { asset ->
                Button(onClick = { playAsset(asset.id) }) {
                    Text(
                        listOfNotNull(
                            asset.height?.let { "${it}p" },
                            asset.audioLanguages.takeIf { it.isNotEmpty() }?.joinToString(", "),
                            asset.sizeBytes.takeIf { it > 0 }?.let { "${it / 1_000_000} MB" },
                        ).joinToString(" · ").ifEmpty { "MediaAsset ${asset.id}" },
                    )
                }
            }
            Button(onClick = ::back) { Text("Zurück") }
        }
        TvV1CatalogPage.Player -> TvV1PlaybackHost(
            origin = serverOrigin,
            tokenStore = deviceSession.authenticatedTokenStore(),
            mediaAssetId = requireNotNull(selectedAssetId),
            title = detailedTitle,
            onExit = {
                back()
                reload += 1
            },
            onNavigateAsset = { asset ->
                // Neighbour IDs were looked up through /api/v1/playback/assets/{id}.
                // Never derive a WorkUnitId from the MediaAssetId or a fractional number.
                selectedAssetId = asset.mediaAssetId
                detailWorkId = asset.workId
                selectedWorkUnitId = null
                selectedEpisodeId = null
                detailedTitle = tvV1PlaybackAssetTitle(asset)
                navigation = TvV1AppController.verifiedNeighbour(
                    navigation, asset.mediaAssetId, asset,
                )
            },
            onUnauthorized = onAuthenticationExpired,
        )
        TvV1CatalogPage.PlaybackPending -> TvV1ApiMessage(
            "Kein abspielbares Medium",
            "Für diese Episode (WorkUnit ${selectedEpisodeId.orEmpty()}) meldet Jularr noch kein abspielbares MediaAsset.",
            onRetry = null,
            onBack = ::back,
        )
    }
}

/** PageResult<T> has no guaranteed total count: keep requesting until a short page. */
private suspend fun <T> loadCatalogPages(
    fetch: suspend (Int) -> de.juloc.jularr.core.api.catalog.TvPageV1<T>,
): List<T> {
    val items = mutableListOf<T>()
    for (page in 1..20) {
        val response = fetch(page)
        require(response.page == page) { "Unerwartete Jularr-Seitennummer." }
        items.addAll(response.items)
        if (response.items.size < 100) return items
    }
    error("Die Bibliothek überschreitet 2000 Einträge. Pagination muss erweitert werden.")
}

@Composable
private fun TvV1Waiting(message: String) {
    TvCenteredPanel("Jularr TV", message) {
        Text(message)
    }
}

@Composable
private fun TvV1ApiMessage(title: String, message: String, onRetry: (() -> Unit)?, onBack: () -> Unit) {
    TvCenteredPanel(title = title, description = message) {
        if (onRetry != null) Button(onClick = onRetry) { Text("Erneut versuchen") }
        Button(onClick = onBack) { Text("Zurück") }
    }
}
