package de.juloc.jularr.tv

import de.juloc.jularr.core.api.catalog.TvWorkCardV1
import de.juloc.jularr.core.api.catalog.TvWorkDetailsV1
import de.juloc.jularr.core.api.catalog.TvWorkEpisodeV1
import de.juloc.jularr.core.api.catalog.TvWorkSeasonV1
import de.juloc.jularr.core.model.AnimeDetail
import de.juloc.jularr.core.model.AnimeSummary
import de.juloc.jularr.core.model.ClientLibrary
import de.juloc.jularr.core.model.EpisodeSummary
import de.juloc.jularr.core.model.Season
import de.juloc.jularr.core.model.WatchlistItem

/**
 * The original Compose TV screens accept legacy visual card types.
 * These mappings retain the real V1 WorkId as the card identity; callers must NEVER use
 * a WorkId or WorkUnitId as a MediaAssetId. No media availability or episode count is guessed.
 */
internal object TvV1LegacyUiModels {
    const val MOVIE = 1
    const val SERIES = 2
    const val ANIME = 1

    fun isVideo(mediaTypeId: Int): Boolean = mediaTypeId == MOVIE || mediaTypeId == SERIES

    /** No fake number of episodes: card-level V1 does not expose a real episode count. */
    fun videoLibrary(cards: List<TvWorkCardV1>): ClientLibrary = ClientLibrary(
        anime = cards.mapNotNull(::videoCard),
    )

    fun videoCard(card: TvWorkCardV1): AnimeSummary? {
        if (!isVideo(card.mediaTypeId)) return null
        val format = when {
            card.mediaTypeId == MOVIE && card.mediaSubtypeId == ANIME -> "ANIME_MOVIE"
            card.mediaTypeId == MOVIE -> "MOVIE"
            card.mediaSubtypeId == ANIME -> "ANIME"
            else -> "SERIES"
        }
        return AnimeSummary(
            id = card.id.toString(),
            title = card.title,
            localTitle = card.title,
            nativeTitle = null,
            coverImageUrl = card.posterUrl,
            bannerImageUrl = null,
            episodeCount = 0,
            seasonCount = 0,
            seasonYear = card.releaseYear,
            format = format,
        )
    }

    fun watchlist(cards: List<TvWorkCardV1>): List<WatchlistItem> =
        cards.map { card ->
            val kind = when {
                card.mediaSubtypeId == 2 -> "manga"
                card.mediaSubtypeId == 3 -> "light-novel"
                card.mediaSubtypeId == ANIME -> "anime"
                card.mediaTypeId == MOVIE -> "movie"
                card.mediaTypeId == SERIES -> "tv"
                card.mediaTypeId == 3 -> "book"
                card.mediaTypeId == 6 -> "music"
                card.mediaTypeId == 7 -> "game"
                else -> "unknown"
            }
            WatchlistItem(
                id = card.id.toString(),
                mediaType = kind,
                title = card.title,
                artworkUrl = card.posterUrl,
                availability = "in_library",
                detailsUrl = null,
                addedAtUtc = null, // Not provided by WorkCardV1.
                localMediaId = if (isVideo(card.mediaTypeId)) card.id.toString() else null,
                status = null, // No server status name is available from numeric enum alone.
                format = if (card.mediaTypeId == MOVIE) "MOVIE" else null,
            )
        }

    /**
     * The legacy TV detail grid has integer episode numbers only. Fractional WorkUnits
     * must be surfaced as unsupported, never truncated to an incorrect episode number.
     */
    fun unsupportedEpisodeCount(episodes: List<TvWorkEpisodeV1>): Int =
        episodes.count { it.number.toIntExactOrNull() == null }

    fun detail(
        work: TvWorkDetailsV1,
        seasons: List<TvWorkSeasonV1>,
        episodesBySeason: Map<Int, List<TvWorkEpisodeV1>>,
    ): AnimeDetail {
        require(isVideo(work.mediaTypeId)) { "The original TV player supports video works only." }
        val visualSeasons = seasons.map { season ->
            Season(
                number = season.seasonNumber,
                episodes = episodesBySeason[season.seasonNumber].orEmpty().mapNotNull { episode ->
                    val number = episode.number.toIntExactOrNull() ?: return@mapNotNull null
                    EpisodeSummary(
                        id = episode.id.toString(), // WorkUnitId; not MediaAssetId.
                        seasonNumber = episode.seasonNumber,
                        number = number,
                        title = episode.title ?: "Episode $number (ohne Titel)",
                        hasMedia = episode.mediaAssetId != null,
                        hasJapaneseLearningSubtitle = false, // No learning subtitle field in V1.
                    )
                },
            )
        }
        return AnimeDetail(
            id = work.id.toString(),
            title = work.canonicalTitle,
            localTitle = work.canonicalTitle,
            nativeTitle = work.originalTitle,
            description = work.overview,
            coverImageUrl = work.posterUrl,
            bannerImageUrl = work.backdropUrl,
            seasonYear = work.releaseYear,
            format = if (work.mediaTypeId == MOVIE) "MOVIE" else if (work.mediaSubtypeId == ANIME) "ANIME" else "SERIES",
            seasons = visualSeasons,
        )
    }

    private fun java.math.BigDecimal.toIntExactOrNull(): Int? =
        runCatching { intValueExact() }.getOrNull()
}
