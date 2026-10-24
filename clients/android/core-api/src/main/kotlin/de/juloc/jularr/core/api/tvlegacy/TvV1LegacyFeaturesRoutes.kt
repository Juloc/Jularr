package de.juloc.jularr.core.api.tvlegacy

/** Actual clean-start ServiceEndpoint declarations. Never falls back to /api/client/v1. */
object TvV1LegacyFeaturesRoutes {
    fun statistics(days: Int = 30): String {
        require(days in 0..3650) { "Statistics days must be 0..3650; 0 means all time." }
        return "/api/v1/statistics?days=$days"
    }

    fun playbackAsset(mediaAssetId: Long): String {
        require(mediaAssetId > 0) { "Use the real positive MediaAssetId." }
        return "/api/v1/playback/assets/$mediaAssetId"
    }
}
