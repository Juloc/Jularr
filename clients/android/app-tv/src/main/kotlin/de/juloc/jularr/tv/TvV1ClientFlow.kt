package de.juloc.jularr.tv

import de.juloc.jularr.core.api.catalog.TvCatalogV1Client
import de.juloc.jularr.core.api.catalog.TvCatalogV1Filters
import de.juloc.jularr.core.api.catalog.TvContinueItemV1
import de.juloc.jularr.core.api.catalog.TvPageV1
import de.juloc.jularr.core.api.catalog.TvWorkAvailabilityV1
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
    )

    data class Work(
        val title: String,
        val isWatchlisted: Boolean,
        val detail: AnimeDetail?,
        val episodeByUnit: Map<Long, TvWorkEpisodeV1>,
        val fractionalEpisodes: List<TvWorkEpisodeV1>,
        val assetByUnit: Map<String, Long?>,
        val availableMovieAssets: List<TvWorkAvailabilityV1>,
    )

    suspend fun home(): Home {
        ensureLibrary()
        val library = TvV1LegacyUiModels.videoLibrary(allPages { page ->
            api.libraryFiltered(
                page = page, pageSize = PageSize,
                filters = TvCatalogV1Filters(mediaTypeIds = listOf(1, 2)),
            )
        })
        val showContinue = if (capabilities.hasModule("HomeLayout")) {
            api.homeLayout().none { it.homeShelfTypeId == 1 && it.isHidden }
        } else true

        return Home(
            library = library,
            continueWatching = if (capabilities.hasModule("Playback") &&
                capabilities.hasPermission("profile.playback")
            ) {
                // Reading progress or games must never be passed into a TV video player.
                api.continueWatching().filter { it.mediaAssetTypeId == 1 }
            } else emptyList(),
            showContinueWatching = showContinue,
        )
    }

    suspend fun search(query: String): ClientLibrary {
        ensureLibrary()
        return TvV1LegacyUiModels.videoLibrary(allPages { page ->
            api.libraryFiltered(
                page = page, pageSize = PageSize,
                filters = TvCatalogV1Filters(mediaTypeIds = listOf(1, 2)),
                query = query.trim().ifEmpty { null },
            )
        })
    }

    suspend fun watchlist(): List<WatchlistItem> {
        ensureLibrary()
        return TvV1LegacyUiModels.watchlist(allPages { page ->
            api.watchlist(page = page, pageSize = PageSize)
        })
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
            )
        }

        val movieAssets = if (work.mediaTypeId == TvV1LegacyUiModels.MOVIE) {
            allPages { page -> api.availability(workId, page = page, pageSize = PageSize) }
                .filter { it.playableMediaAssetId() != null }
        } else emptyList()
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
            availableMovieAssets = movieAssets,
        )
    }

    private fun ensureLibrary() {
        check(capabilities.hasModule("Library") && capabilities.hasPermission("profile.media.read")) {
            "Die Bibliothek ist für dieses TV-Profil nicht freigegeben."
        }
    }

    private suspend fun <T> allPages(
        fetch: suspend (Int) -> TvPageV1<T>,
    ): List<T> {
        val all = ArrayList<T>()
        for (page in 1..MaxPages) {
            val result = fetch(page)
            check(result.page == page) { "Jularr returned a mismatched page number." }
            check(result.pageSize == PageSize) { "Jularr returned an unexpected page size." }
            check(result.items.size <= PageSize) { "Jularr returned too many items per page." }
            all.addAll(result.items)
            if (result.items.size < PageSize) return all
        }
        error("Mehr als $MaxPages Bibliotheksseiten: Listenansicht benötigt weitere Pagination.")
    }

    private companion object {
        const val PageSize = 100
        const val MaxPages = 20
    }
}
