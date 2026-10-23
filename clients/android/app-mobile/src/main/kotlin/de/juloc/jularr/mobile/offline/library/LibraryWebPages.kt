package de.juloc.jularr.mobile.offline.library

/**
 * Detects a book/novel detail page URL so the native shell can show a
 * "Save offline" action on top of the WebView, mirroring how
 * `WebNavigationPolicy` already detects the episode Play route to open the
 * native player (`de.juloc.jularr.mobile.ServerOrigin`) — a URL-path match,
 * not a JS bridge. Both `/Novels/Work/{id}` and `/Books/Library/{id}` render
 * the same underlying work, just from different library sections.
 */
object LibraryWebPages {
    private val workPath = Regex(
        "^/(?:Novels/Work|Books/Library)/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/?$",
    )

    /** The work id if [path] is a book/novel detail page, `null` otherwise. */
    fun workId(path: String): String? =
        workPath.matchEntire(path)?.groupValues?.get(1)
}
