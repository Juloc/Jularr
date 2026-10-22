package de.juloc.jularr.core.api.catalog

import java.math.BigDecimal

/**
 * API-v1 identities are deliberately distinct: a Work is not a WorkUnit and neither is
 * a MediaAsset. Enum values stay numeric until mapped against the actual server enums.
 * Nullable metadata is never replaced with fabricated titles, covers or availability.
 */
data class TvPageV1<T>(val items: List<T>, val page: Int, val pageSize: Int)

data class TvWorkCardV1(
    val id: Long,
    val mediaTypeId: Int,
    val mediaSubtypeId: Int?,
    val title: String,
    val releaseYear: Int?,
    val posterUrl: String?,
    val rating: Double?,
    val mediaStateTypeId: Int,
    val isWatchlisted: Boolean,
    val availabilityStateId: Int,
    val availableUnits: Int,
    val monitoredUnits: Int,
    val audioLanguages: List<String>,
    val subtitleLanguages: List<String>,
    val progressStateId: Int,
    val progressPercent: Int,
)

data class TvContinueItemV1(
    val workId: Long,
    val mediaAssetId: Long,
    val mediaAssetTypeId: Int,
    val mediaTypeId: Int,
    val mediaSubtypeId: Int?,
    val title: String,
    val releaseYear: Int?,
    val posterUrl: String?,
    val workUnitTypeId: Int?,
    val unitNumber: BigDecimal?,
    val parentUnitNumber: BigDecimal?,
    val positionMs: Long,
    val durationMs: Long?,
    val progression: BigDecimal?,
)

/** WorkDetailsV1: its id is always a WorkId, not a playback MediaAssetId. */
data class TvWorkDetailsV1(
    val id: Long,
    val mediaTypeId: Int,
    val mediaSubtypeId: Int?,
    val canonicalTitle: String,
    val isWatchlisted: Boolean,
    val releaseYear: Int?,
    val overview: String?,
    val posterUrl: String?,
    val backdropUrl: String?,
    val plexUrl: String?,
    val platforms: List<String>,
    val franchiseId: Long?,
    val franchiseName: String?,
    val isRequestable: Boolean,
    val requestStatusTypeId: Int?,
    val runtimeMinutes: Int?,
    val genres: String?,
    val trailerKey: String?,
    val studio: String?,
    val availabilityStateId: Int,
    val availableUnits: Int,
    val monitoredUnits: Int,
    val progressStateId: Int,
    val progressPercent: Int,
    val rating: Double?,
    val ratingSource: String?,
    val originalTitle: String?,
    val releasedOn: String?,
    val originalLanguage: String?,
    val franchisePosition: BigDecimal?,
)

data class TvWorkSeasonV1(
    val id: Long, // WorkUnitId of the season
    val seasonNumber: Int,
    val title: String?,
    val imageUrl: String?,
    val releaseYear: Int?,
    val episodeCount: Int,
    val availableCount: Int,
    val watchedCount: Int,
)

/** id is WorkUnitId. Only mediaAssetId may start a playback session. */
data class TvWorkEpisodeV1(
    val id: Long,
    val seasonNumber: Int,
    val number: BigDecimal,
    val title: String?,
    val overview: String?,
    val imageUrl: String?,
    val runtimeMinutes: Int?,
    val releasedOn: String?,
    val mediaAssetId: Long?,
    val resumePositionMs: Long?,
    val durationMs: Long?,
    val isCompleted: Boolean,
    val mediaStateTypeId: Int,
    val isUpcoming: Boolean,
    val groupName: String?,
) {
    fun playableMediaAssetId(): Long? = mediaAssetId
}

data class TvWorkAvailabilityV1(
    val id: Long, // MediaAssetId
    val workVersionId: Long,
    val workEditionId: Long,
    val workUnitId: Long?,
    val mediaAssetTypeId: Int,
    val workUnitTypeId: Int?,
    val unitNumber: BigDecimal?,
    val parentUnitNumber: BigDecimal?,
    val fileCount: Long,
    val sizeBytes: Long,
    val durationMs: Long?,
    val isAvailable: Boolean,
    val height: Int?,
    val audioLanguages: List<String>,
    val subtitleLanguages: List<String>,
    val resumePositionMs: Long?,
    val isCompleted: Boolean,
    val isEmulated: Boolean,
    val groupName: String?,
    val unitTitle: String?,
    val hasProgress: Boolean,
    val platformName: String?,
    val unitImageUrl: String?,
    val discNumber: Int?,
) {
    fun playableMediaAssetId(): Long? = id.takeIf { isAvailable && fileCount > 0L && mediaAssetTypeId == 1 }
}

data class TvHomeShelfV1(val homeShelfTypeId: Int, val isHidden: Boolean)

data class TvDiscoverScopeV1(
    val slug: String,
    val mediaTypeId: Int,
    val mediaSubtypeId: Int?,
    val isCatalog: Boolean,
    val feeds: List<Int>,
)

/** workId is null for catalog-only entries: tmdbId is not interchangeable with WorkId. */
data class TvDiscoverItemV1(
    val mediaTypeId: Int,
    val tmdbId: Int?,
    val workId: Long?,
    val title: String,
    val releaseYear: Int?,
    val overview: String?,
    val posterUrl: String?,
    val backdropUrl: String?,
    val mediaStateTypeId: Int,
    val plexUrl: String?,
    val rating: Double?,
    val trailerKey: String?,
)

data class TvDiscoverCatalogItemV1(
    val namespace: String,
    val externalId: String,
    val title: String,
    val releaseYear: Int?,
    val author: String?,
    val workId: Long?,
    val posterUrl: String?,
    val rating: Double?,
    val mediaStateTypeId: Int?,
)

data class TvDiscoverShelfV1(
    val scopeSlug: String,
    val discoverFeedTypeId: Int,
    val items: List<TvDiscoverItemV1>,
    val catalogItems: List<TvDiscoverCatalogItemV1>,
)
