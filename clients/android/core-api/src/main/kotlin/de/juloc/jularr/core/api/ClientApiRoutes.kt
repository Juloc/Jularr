package de.juloc.jularr.core.api

object ClientApiRoutes {
    const val ApiVersion = 2
    const val Base = "/api/client/v1"
    const val Capabilities = "$Base/capabilities"
    const val Login = "$Base/session/login"
    const val Logout = "$Base/session/logout"
    const val Me = "$Base/me"
    const val Library = "$Base/library"
    const val ContinueWatching = "$Base/continue-watching"
    const val PlaybackHistory = "$Base/me/playback-history"
    const val Watchlist = "$Base/watchlist"

    fun anime(animeId: String) = "$Base/anime/$animeId"
    fun episode(episodeId: String) = "$Base/episodes/$episodeId"
    fun progress(episodeId: String) = "$Base/episodes/$episodeId/progress"
    fun player(episodeId: String) = "$Base/episodes/$episodeId/player"
    fun offlineDownload(episodeId: String) = "$Base/episodes/$episodeId/offline-download"
    const val OfflineProgress = "$Base/offline/progress"

    fun offlineLibraryManifest(workId: String) = "$Base/offline-library/works/$workId/manifest"
    fun offlineLibraryChapter(chapterId: String) = "$Base/offline-library/chapters/$chapterId"
    fun offlineLibraryAsset(volumeId: String, asset: String) =
        "$Base/offline-library/assets/$volumeId/$asset"
    const val OfflineLibrarySync = "$Base/offline-library/sync"

    fun cues(
        episodeId: String,
        trackId: String? = null,
        fromMs: Int? = null,
        toMs: Int? = null,
    ): String {
        val query = buildList {
            trackId?.let { add("trackId=$it") }
            fromMs?.let { add("fromMs=$it") }
            toMs?.let { add("toMs=$it") }
        }.joinToString("&")

        return "$Base/episodes/$episodeId/cues" +
            if (query.isEmpty()) "" else "?$query"
    }

    fun media(mediaFileId: String) = "$Base/media/$mediaFileId/content"
    fun mediaAvailability(mediaFileId: String, fresh: Boolean = false) =
        "$Base/media/$mediaFileId/availability?fresh=$fresh"
    fun fallback(episodeId: String, startSeconds: Double? = null): String =
        "$Base/episodes/$episodeId/fallback" +
            (startSeconds?.let { "?mode=server&startSeconds=$it" } ?: "")

    fun hls(episodeId: String, startSeconds: Double? = null): String =
        "$Base/episodes/$episodeId/hls" +
            (startSeconds?.let { "?startSeconds=$it" } ?: "")
    fun term(termId: String) = "$Base/terms/$termId"
    fun rootAvailability(rootId: String) = "$Base/library-roots/$rootId/availability"
    fun testRoot(rootId: String) = "$Base/library-roots/$rootId/test"
    fun wakeRoot(rootId: String) = "$Base/library-roots/$rootId/wake"

    const val TtsPreferences = "$Base/me/tts-preferences"
    const val SpeechModels = "$Base/speech/models"

    const val PairingStart = "$Base/pairing/start"
    const val PairingPoll = "$Base/pairing/poll"
}
