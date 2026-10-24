package de.juloc.jularr.core.api.tvlegacy

/** Additive read-only API for legacy TV details and aggregate activity panels. */
interface TvV1LegacyFeaturesClient {
    /** 0 days means all-time; totals/top works, not timestamped viewing events. */
    suspend fun statistics(days: Int = 30): TvV1ProfileStatistics

    /** Identity, progress resume and real previous/next playable asset IDs. */
    suspend fun playbackAsset(mediaAssetId: Long): TvV1PlaybackAssetInfo
}
