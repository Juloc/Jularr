package de.juloc.jularr.core.api.playbackextras

/** Existing clean-start ServiceEndpoint attributes, never client-specific alternative paths. */
object TvV1PlaybackExtrasRoutes {
    private const val base = "/api/v1/playback"
    const val Preferences = "$base/preferences"
    const val Speed = "$base/preferences/speed"

    private fun asset(assetId: Long): String {
        require(assetId > 0) { "Use a real MediaAssetId." }
        return "$base/assets/$assetId"
    }
    fun markers(assetId: Long): String = "${asset(assetId)}/markers"
    fun chapters(assetId: Long): String = "${asset(assetId)}/chapters"
    fun trickplay(assetId: Long): String = "${asset(assetId)}/trickplay"
    fun trickplaySheet(assetId: Long, sheetIndex: Int): String {
        require(sheetIndex >= 1) { "Trickplay sheet number starts at 1." }
        return "${trickplay(assetId)}/$sheetIndex"
    }
    fun tracks(assetId: Long): String = "${asset(assetId)}/tracks"

    /** Backend PlaybackTracks.ParseKey recognises only s{streamIndex} and f{storedFileId}. */
    fun subtitle(assetId: Long, key: String): String {
        require(Regex("^(s(0|[1-9][0-9]*)|f[1-9][0-9]*)$").matches(key)) { "Invalid subtitle track key." }
        return "${asset(assetId)}/subtitles/$key"
    }
}
