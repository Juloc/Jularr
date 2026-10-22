package de.juloc.jularr.tv

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.weight
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.catalog.HttpTvCatalogV1Client
import de.juloc.jularr.core.api.catalog.TvCatalogV1Client
import de.juloc.jularr.core.api.catalog.TvWorkCardV1
import de.juloc.jularr.core.api.catalog.TvWorkEpisodeV1
import de.juloc.jularr.core.model.AnimeDetail
import de.juloc.jularr.core.model.ClientLibrary
import de.juloc.jularr.core.model.InstanceCapabilities
import de.juloc.jularr.core.model.WatchlistItem
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay

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
) {
    val catalog: TvCatalogV1Client = remember(serverOrigin, deviceSession) {
        HttpTvCatalogV1Client(serverOrigin, deviceSession.authenticatedTokenStore())
    }
    val focusMemory = remember(serverOrigin) { TvFocusMemory() }
    var page by remember(serverOrigin) { mutableStateOf(TvV1CatalogPage.Home) }
    var searchQuery by remember(serverOrigin) { mutableStateOf("") }
    var category by remember(serverOrigin) { mutableStateOf(TvContentFilter.ALL) }
    var reload by remember(serverOrigin) { mutableIntStateOf(0) }
    var loading by remember(serverOrigin) { mutableStateOf(false) }
    var error by remember(serverOrigin) { mutableStateOf<String?>(null) }
    var library by remember(serverOrigin) { mutableStateOf(ClientLibrary(emptyList())) }
    var searchResults by remember(serverOrigin) { mutableStateOf(ClientLibrary(emptyList())) }
    var watchlist by remember(serverOrigin) { mutableStateOf<List<WatchlistItem>>(emptyList()) }
    var details by remember(serverOrigin) { mutableStateOf<AnimeDetail?>(null) }
    var detailedTitle by remember(serverOrigin) { mutableStateOf("") }
    var omittedFractionalEpisodes by remember(serverOrigin) { mutableIntStateOf(0) }
    var detailWorkId by remember(serverOrigin) { mutableStateOf<Long?>(null) }
    var detailAssetByUnit by remember(serverOrigin) { mutableStateOf<Map<String, Long?>>(emptyMap()) }
    var selectedEpisodeId by remember(serverOrigin) { mutableStateOf<String?>(null) }
    var selectedAssetId by remember(serverOrigin) { mutableStateOf<Long?>(null) }
    var returnPage by remember(serverOrigin) { mutableStateOf(TvV1CatalogPage.Home) }

    fun openWork(rawId: String) {
        val workId = rawId.toLongOrNull()
        if (workId == null || workId <= 0) {
            error = "Ungültige Work-ID der Jularr-Instanz."
            return
        }
        returnPage = if (page == TvV1CatalogPage.Watchlist) TvV1CatalogPage.Watchlist
            else if (page == TvV1CatalogPage.Search) TvV1CatalogPage.Search
            else TvV1CatalogPage.Home
        detailWorkId = workId
        details = null
        error = null
        page = TvV1CatalogPage.Details
    }

    fun back() {
        error = null
        page = when (page) {
            TvV1CatalogPage.Details -> returnPage
            TvV1CatalogPage.PlaybackPending -> TvV1CatalogPage.Details
            TvV1CatalogPage.Home -> TvV1CatalogPage.Home
            else -> TvV1CatalogPage.Home
        }
    }

    BackHandler(enabled = page != TvV1CatalogPage.Home) { back() }

    LaunchedEffect(serverOrigin, page, searchQuery, detailWorkId, reload) {
        error = null
        loading = true
        try {
            when (page) {
                TvV1CatalogPage.Home -> {
                    require(capabilities.hasModule("Library") && capabilities.hasPermission("profile.media.read")) {
                        "Bibliothek ist für dieses Profil nicht freigegeben."
                    }
                    library = TvV1LegacyUiModels.videoLibrary(loadCatalogPages { pageNumber ->
                        catalog.library(page = pageNumber, pageSize = 100)
                    })
                }
                TvV1CatalogPage.Search -> {
                    require(capabilities.hasModule("Library") && capabilities.hasPermission("profile.media.read")) {
                        "Suche ist für dieses Profil nicht freigegeben."
                    }
                    if (searchQuery.isNotBlank()) delay(300)
                    searchResults = TvV1LegacyUiModels.videoLibrary(loadCatalogPages { pageNumber ->
                        catalog.library(page = pageNumber, pageSize = 100, query = searchQuery.trim().ifEmpty { null })
                    })
                }
                TvV1CatalogPage.Watchlist -> {
                    require(capabilities.hasModule("Library") && capabilities.hasPermission("profile.media.read")) {
                        "Watchlist ist für dieses Profil nicht freigegeben."
                    }
                    watchlist = TvV1LegacyUiModels.watchlist(loadCatalogPages { pageNumber ->
                        catalog.watchlist(page = pageNumber, pageSize = 100)
                    })
                }
                TvV1CatalogPage.Details -> {
                    val workId = requireNotNull(detailWorkId)
                    require(capabilities.hasModule("Library") && capabilities.hasPermission("profile.media.read")) {
                        "Mediendetails sind für dieses Profil nicht freigegeben."
                    }
                    val work = catalog.work(workId)
                    detailedTitle = work.canonicalTitle
                    if (TvV1LegacyUiModels.isVideo(work.mediaTypeId)) {
                        val seasons = catalog.seasons(workId)
                        val episodesBySeason = seasons.associate { season ->
                            season.seasonNumber to catalog.episodes(workId, season.seasonNumber)
                        }
                        omittedFractionalEpisodes = episodesBySeason.values.sumOf(TvV1LegacyUiModels::unsupportedEpisodeCount)
                        detailAssetByUnit = episodesBySeason.values.flatten().associate { episode ->
                            episode.id.toString() to episode.mediaAssetId
                        }
                        details = TvV1LegacyUiModels.detail(work, seasons, episodesBySeason)
                    } else {
                        // The original TV detail grid is video-only; do not fake video episodes.
                        details = null
                        omittedFractionalEpisodes = 0
                    }
                }
                TvV1CatalogPage.PlaybackPending -> Unit
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
                            TvRoute.Home -> page = TvV1CatalogPage.Home
                            TvRoute.Watchlist -> page = TvV1CatalogPage.Watchlist
                            TvRoute.Settings, TvRoute.ProfileSelect -> {
                                error = "Profile und TV-Einstellungen werden im nächsten API-Schritt angebunden."
                            }
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
                            error = error ?: if (loading) "Bibliothek wird geladen …" else null,
                            focusMemory = focusMemory,
                            onOpenSearch = { page = TvV1CatalogPage.Search },
                            onAnime = { card -> openWork(card.id) },
                            onContinueWatching = { /* V1 playback integration pending */ },
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
                            )
                        }
                        else -> Unit
                    }
                }
            }
        }
        TvV1CatalogPage.Search -> {
            if (error != null) TvV1ApiMessage("Suche", requireNotNull(error), { reload += 1 }, ::back)
            else TvSearchScreen(
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
            )
        }
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
                            onEpisode = { episode ->
                                selectedEpisodeId = episode.id
                                selectedAssetId = detailAssetByUnit[episode.id]
                                page = TvV1CatalogPage.PlaybackPending
                            },
                            onPlayEpisode = { episode ->
                                selectedEpisodeId = episode.id
                                selectedAssetId = detailAssetByUnit[episode.id]
                                page = TvV1CatalogPage.PlaybackPending
                            },
                            onBack = ::back,
                        )
                        if (omittedFractionalEpisodes > 0) {
                            Text(
                                "${omittedFractionalEpisodes} Sonderfolgen mit Bruchnummer sind in dieser TV-Ansicht noch nicht darstellbar.",
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
        TvV1CatalogPage.PlaybackPending -> TvV1ApiMessage(
            "Wiedergabe noch nicht verbunden",
            if (selectedAssetId != null)
                "MediaAsset ${selectedAssetId} ist gefunden. Der alte Media3-Player wird im nächsten Schritt mit den /api/v1-Playback-Sessions verbunden."
            else
                "Für diese Episode (WorkUnit ${selectedEpisodeId.orEmpty()}) meldet Jularr noch kein abspielbares MediaAsset.",
            onRetry = null,
            onBack = ::back,
        )
    }
}

private enum class TvV1CatalogPage { Home, Search, Watchlist, Details, PlaybackPending }

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
