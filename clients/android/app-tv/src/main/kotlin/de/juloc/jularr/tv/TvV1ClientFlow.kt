package de.juloc.jularr.tv

import de.juloc.jularr.core.api.catalog.TvCatalogV1Client
import de.juloc.jularr.core.api.catalog.TvCatalogV1Filters
import de.juloc.jularr.core.api.catalog.TvContinueItemV1
import de.juloc.jularr.core.api.catalog.TvCollectionV1
import de.juloc.jularr.core.api.catalog.TvPageV1
import de.juloc.jularr.core.api.catalog.TvWorkAvailabilityV1
import de.juloc.jularr.core.api.catalog.TvWorkCardV1
import de.juloc.jularr.core.api.catalog.TvWorkEpisodeV1
import de.juloc.jularr.core.model.AnimeDetail
import de.juloc.jularr.core.model.ClientLibrary
import de.juloc.jularr.core.model.InstanceCapabilities
import de.juloc.jularr.core.model.WatchlistItem

/**
 * API-v1 data flow for the existing Android TV UI.
 *
 * Replaces the active read operations of the old cookie-based TvClientFlow.
 * The legacy models produced here are *presentation-only*: a WorkId remains
 * a WorkId, a WorkUnitId remains a WorkUnitId, and only a genuine MediaAssetId
 * can open a playback session. No /api/client/v1 calls or invented fields.
 */
internal class TvV1ClientFlow(
    private val api: TvCatalogV1Client,
    private val capabilities: InstanceCapabilities,
) {
    data class Home(
        val library: ClientLibrary,
        val continueWatching: List<TvContinueItemV1>,
        val showContinueWatching: Boolean,
        val hasMoreVideo: Boolean = false,
        val shelfOrder: List<Int> = listOf(1, 4, 5),
    )

    data class PageSlice<T>(val items: List<T>, val hasMore: Boolean)

    data class Work(
        val title: String,
        val isWatchlisted: Boolean,
        val detail: AnimeDetail?,
        val episodeByUnit: Map<Long, TvWorkEpisodeV1>,
        val fractionalEpisodes: List<TvWorkEpisodeV1>,
        val assetByUnit: Map<String, Long?>,
        val availableMovieAssets: List<TvWorkAvailabilityV1>,
        val hasMoreMovieAssets: Boolean = false,
        val isRequestable: Boolean = false,
    )

    suspend fun home(): Home {
        ensureLibrary()
        val video = videoPage(1)
        val library = TvV1LegacyUiModels.videoLibrary(video.items)
        // Match the Web Home: 1=Continue, 4=Recently Added, 5=real
        // provider-backed Discover recommendations (trending/new/top/upcoming).
        // Audio/reading shelves are not video rows.
        val configured = if (capabilities.hasModule("HomeLayout")) api.homeLayout()
            .filterNot { it.isHidden }.map { it.homeShelfTypeId }
        else listOf(1, 4, 5)
        val videoShelfOrder = configured.filter { it == 1 || it == 4 || it == 5 }.distinct()
        val showContinue = 1 in videoShelfOrder

        return Home(
            library = library,
            continueWatching = if (capabilities.hasModule("Playback") &&
                capabilities.hasPermission("profile.playback")
            ) {
                // Reading progress or games must never be passed into a TV video player.
                api.continueWatching().filter { it.mediaAssetTypeId == 1 }
            } else emptyList(),
            showContinueWatching = showContinue,
            hasMoreVideo = video.hasMore,
            shelfOrder = videoShelfOrder,
        )
    }

    suspend fun search(query: String): ClientLibrary {
        ensureLibrary()
        return TvV1LegacyUiModels.videoLibrary(videoPage(1, query).items)
    }

    suspend fun watchlist(): List<WatchlistItem> {
        ensureLibrary()
        return TvV1LegacyUiModels.watchlist(watchlistPage(1).items)
    }

    /** Request additional genuine server pages only when the viewer asks for them. */
    suspend fun videoPage(page: Int, query: String? = null): PageSlice<TvWorkCardV1> {
        ensureLibrary()
        require(page >= 1)
        val result = api.libraryFiltered(
            page = page, pageSize = PageSize,
            filters = TvCatalogV1Filters(
                mediaTypeIds = listOf(1, 2),
                mediaSubtypeIds = listOf(1), // Named Anime subtype is excluded by type-only SQL filters.
            ),
            query = query?.trim()?.ifEmpty { null },
        )
        check(result.page == page && result.pageSize == PageSize)
        return PageSlice(result.items, result.items.size == PageSize)
    }

    /** Collections are optional. Never use the Watchlist permission as a substitute. */
    fun canReadCollections(): Boolean =
        capabilities.hasModule("Library") &&
            capabilities.hasModule("Collections") &&
            capabilities.hasPermission("profile.collections")

    suspend fun collections(): List<TvCollectionV1> {
        check(canReadCollections()) { "This profile cannot access collections." }
        return api.collections().filter { it.id > 0 && it.name.isNotBlank() }
    }

    suspend fun collectionPage(collectionId: Long, page: Int): PageSlice<TvWorkCardV1> {
        check(canReadCollections()) { "This profile cannot access collections." }
        require(collectionId > 0 && page >= 1)
        val result = api.collectionWorks(collectionId, page, PageSize)
        check(result.page == page && result.pageSize == PageSize)
        return PageSlice(
            result.items.filter { TvV1LegacyUiModels.isVideo(it.mediaTypeId) },
            result.items.size == PageSize,
        )
    }

    /** Server-sorted video-only favorites; never pretend partial pages are globally sorted. */
    suspend fun watchlistPage(page: Int, sortTypeId: Int = 1): PageSlice<TvWorkCardV1> {
        ensureLibrary()
        require(page >= 1 && sortTypeId in listOf(1, 2))
        val result = api.watchlistFiltered(
            page = page, pageSize = PageSize,
            filters = TvCatalogV1Filters(
                mediaTypeIds = listOf(1, 2),
                mediaSubtypeIds = listOf(1),
            ),
            sortTypeId = sortTypeId,
        )
        check(result.page == page && result.pageSize == PageSize)
        return PageSlice(result.items, result.items.size == PageSize)
    }

    suspend fun work(workId: Long): Work {
        ensureLibrary()
        require(workId > 0)
        val work = api.work(workId)
        check(work.id == workId) { "Work response does not match the requested WorkId." }
        if (!TvV1LegacyUiModels.isVideo(work.mediaTypeId)) {
            return Work(
                title = work.canonicalTitle, isWatchlisted = work.isWatchlisted,
                detail = null, episodeByUnit = emptyMap(), fractionalEpisodes = emptyList(),
                assetByUnit = emptyMap(), availableMovieAssets = emptyList(),
                isRequestable = work.isRequestable,
            )
        }

        val moviePage = if (work.mediaTypeId == TvV1LegacyUiModels.MOVIE) {
            movieAssetsPage(workId, 1)
        } else PageSlice(emptyList(), false)
        val seasons = api.seasons(workId)
        val bySeason = seasons.associate { season ->
            season.seasonNumber to api.episodes(workId, season.seasonNumber)
        }
        val episodes = bySeason.values.flatten()
        val units = episodes.associateBy { it.id }
        check(units.size == episodes.size) { "The server returned duplicate WorkUnitIds." }

        return Work(
            title = work.canonicalTitle,
            isWatchlisted = work.isWatchlisted,
            detail = TvV1LegacyUiModels.detail(work, seasons, bySeason),
            episodeByUnit = units,
            fractionalEpisodes = episodes.filter { runCatching { it.number.intValueExact() }.isFailure },
            assetByUnit = episodes.associate { it.id.toString() to it.mediaAssetId },
            availableMovieAssets = moviePage.items,
            hasMoreMovieAssets = moviePage.hasMore,
            isRequestable = work.isRequestable,
        )
    }

    private fun ensureLibrary() {
        check(capabilities.hasModule("Library") && capabilities.hasPermission("profile.media.read")) {
            "Die Bibliothek ist für dieses TV-Profil nicht freigegeben."
        }
    }

    /** Availability is paged independently from title metadata so a large film library never stalls Details. */
    suspend fun movieAssetsPage(workId: Long, page: Int): PageSlice<TvWorkAvailabilityV1> {
        ensureLibrary()
        require(workId > 0 && page >= 1)
        val result = api.availability(workId, page = page, pageSize = PageSize)
        check(result.page == page && result.pageSize == PageSize)
        check(result.items.size <= PageSize) { "Jularr returned too many movie assets in one page." }
        return PageSlice(
            items = result.items.filter { it.playableMediaAssetId() != null },
            hasMore = result.items.size == PageSize,
        )
    }

    private companion object {
        const val PageSize = 100
    }
}
