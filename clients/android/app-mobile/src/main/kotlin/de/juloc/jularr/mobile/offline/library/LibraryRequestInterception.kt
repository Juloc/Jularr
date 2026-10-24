package de.juloc.jularr.mobile.offline.library

/**
 * A synthesized local response for `WebResourceResponse`, without depending on
 * android.webkit here. A plain class (not `data class`): `ByteArray` equality
 * is never needed for this transient, one-shot value.
 */
class LibraryLocalResponse(
    val contentType: String,
    val bytes: ByteArray,
)

/** Which locally-downloaded resource, if any, a request path maps to. */
sealed interface LibraryInterceptTarget {
    data class Manifest(val workId: String) : LibraryInterceptTarget
    data class Chapter(val chapterId: String) : LibraryInterceptTarget
    data class Asset(val volumeId: String, val asset: String) : LibraryInterceptTarget
}

/**
 * Maps a same-origin request path onto the offline-library API shape
 * (`/api/client/v1/offline-library` endpoints, see docs/OFFLINE_LIBRARY.md)
 * without touching the network or android.webkit, so it is plain-JUnit testable. The
 * WebView shell uses this to decide whether `shouldInterceptRequest` should
 * answer from local storage instead of the network ("local source first";
 * see docs/ANDROID_CLIENTS.md §8.1) — every other request is left untouched
 * (returns `null`) and falls through to the normal network load. This is
 * deliberately narrow: it is not a general asset loader and it never runs
 * arbitrary JS or adds a JS bridge, only a fixed set of read-only content
 * paths already reachable (with the normal session cookie) by any signed-in
 * client.
 */
object LibraryRequestInterception {
    private const val Uuid = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"
    private val manifestPath = Regex("^/api/client/v1/offline-library/works/($Uuid)/manifest/?$")
    private val chapterPath = Regex("^/api/client/v1/offline-library/chapters/($Uuid)/?$")
    private val assetPath = Regex(
        "^/api/client/v1/offline-library/assets/($Uuid)/([a-f0-9]{32}\\.(?:jpg|png|gif|webp))$",
    )

    /**
     * [path] is the request URL's path component only; same-origin enforcement
     * is the caller's responsibility (the WebView already restricts navigation
     * to the configured server origin, but `shouldInterceptRequest` sees every
     * resource load including off-origin ones, so the caller must check
     * [de.juloc.jularr.mobile.ServerOrigin.isSameOrigin] before calling this).
     */
    fun match(path: String): LibraryInterceptTarget? {
        manifestPath.matchEntire(path)?.let { return LibraryInterceptTarget.Manifest(it.groupValues[1]) }
        chapterPath.matchEntire(path)?.let { return LibraryInterceptTarget.Chapter(it.groupValues[1]) }
        assetPath.matchEntire(path)?.let {
            return LibraryInterceptTarget.Asset(volumeId = it.groupValues[1], asset = it.groupValues[2])
        }
        return null
    }

    fun contentTypeFor(asset: String): String =
        when (asset.substringAfterLast('.', "").lowercase()) {
            "jpg", "jpeg" -> "image/jpeg"
            "png" -> "image/png"
            "gif" -> "image/gif"
            "webp" -> "image/webp"
            else -> "application/octet-stream"
        }
}
