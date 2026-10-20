package de.juloc.jularr.core.model

/**
 * One title the profile continues: the work, the asset to open and how far it is watched,
 * listened to or read.
 */
data class ContinueItemV1(
    val workId: Long,
    val mediaAssetId: Long,
    val mediaTypeId: Int,
    val title: String,
    val posterUrl: String?,
    val workUnitTypeId: Int?,
    val unitNumber: Double?,
    val parentUnitNumber: Double?,
    val positionMs: Long,
    val durationMs: Long?,
    val progression: Double?,
    val mediaAssetTypeId: Int,
) {
    companion object {
        /** The media asset types of the Backend. */
        const val AssetVideo = 1
        const val AssetAudio = 2
        const val AssetEbook = 3
        const val AssetComic = 4
        const val AssetGame = 5
    }
}
