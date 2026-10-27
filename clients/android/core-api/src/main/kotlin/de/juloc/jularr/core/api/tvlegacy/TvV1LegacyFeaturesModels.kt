package de.juloc.jularr.core.api.tvlegacy

import java.math.BigDecimal

/**
 * Read-only aggregate user activity. This is NOT a chronological playback-history list.
 * Field names and dimensions match ProfileStatisticsV1 from clean-start.
 */
data class TvV1ProfileStatistics(
    val watchedMs: Long,
    val sessions: Long,
    val gamePlayedMs: Long,
    val completed: List<TvV1CompletedByMediaType>,
    val topWorks: List<TvV1TopWorkTime>,
) {
    init {
        require(watchedMs >= 0 && sessions >= 0 && gamePlayedMs >= 0)
    }
}

data class TvV1CompletedByMediaType(
    val mediaTypeId: Int,
    val count: Long,
) {
    init {
        require(mediaTypeId > 0 && count >= 0)
    }
}

data class TvV1TopWorkTime(
    val workId: Long,
    val title: String,
    val durationMs: Long,
) {
    init {
        require(workId > 0 && durationMs >= 0)
    }
}

/**
 * GET /api/v1/playback/assets/{mediaAssetId}.
 * Neighbours are MediaAssetIds, not WorkUnitIds. This endpoint does not probe NAS.
 */
data class TvV1PlaybackAssetInfo(
    val mediaAssetId: Long,
    val workId: Long,
    val canonicalTitle: String,
    val mediaAssetTypeId: Int,
    val workUnitTypeId: Int?,
    val unitNumber: BigDecimal?,
    val parentUnitNumber: BigDecimal?,
    val unitTitle: String?,
    val resumePositionMs: Long?,
    val previousMediaAssetId: Long?,
    val nextMediaAssetId: Long?,
) {
    init {
        require(mediaAssetId > 0 && workId > 0 && mediaAssetTypeId > 0)
        require(resumePositionMs == null || resumePositionMs >= 0)
        require(previousMediaAssetId == null || previousMediaAssetId > 0)
        require(nextMediaAssetId == null || nextMediaAssetId > 0)
    }
}
