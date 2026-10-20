package de.juloc.jularr.core.api

/**
 * The versioned app services of the backend. A client signs in as a paired device and calls
 * these routes with its device token; it never scrapes the web pages and never uses a cookie.
 */
object AppApiRoutes {
    const val Base = "/api/v1"
    const val Pairings = "$Base/devices/pairings"
    const val Capabilities = "$Base/instance/capabilities"
    const val Authentication = "$Base/auth"
    const val InstancePresentation = "$Base/instance/presentation"
    const val Watchlist = "$Base/watchlist"
    const val Library = "$Base/library"
    const val Continue = "$Base/continue"
    const val HomeLayout = "$Base/home/layout"
    const val DiscoverScopes = "$Base/discover/scopes"
    const val DiscoverSearch = "$Base/discover/search"
    const val DiscoverSuggestions = "$Base/discover/suggestions"
    const val DiscoverShelves = "$Base/discover/shelves"
    const val Profiles = "$Base/profiles"

    /** The shared design tokens the instance serves; a client reads them from here. */
    const val DesignTokens = "/tokens.json"

    /** The server hands the device token in this header instead of a cookie. */
    const val DeviceTokenHeader = "Jularr-Device-Token"

    fun pairingClaim(pairingId: String): String = "$Pairings/$pairingId/claim"

    /** The server's texts of a locale; the client shows them instead of its own words. */
    fun uiTexts(uiLocaleId: Long): String = "$Base/localization/texts/$uiLocaleId"

    fun watchlistPage(page: Int, pageSize: Int): String = "$Watchlist?page=$page&pageSize=$pageSize"

    fun libraryPage(page: Int, pageSize: Int): String = "$Library?page=$page&pageSize=$pageSize"

    /** Switches the device to another profile of its account. */
    fun selectProfile(profileId: Long): String = "$Base/auth/profile/$profileId"

    /** The details of a title of the instance. */
    fun work(workId: Long): String = "$Base/works/$workId"

    /** Starts the playback of an asset; the answer carries the stream to read. */
    fun playbackSessions(mediaAssetId: Long): String = "$Base/playback/assets/$mediaAssetId/sessions"

    /** The progress of a running playback. */
    fun playbackSession(playbackSessionId: String): String = "$Base/playback/sessions/$playbackSessionId"
}
