package de.juloc.jularr.core.api

/**
 * The versioned app services of the backend. A client signs in as a paired device and calls
 * these routes with its device token; it never scrapes the web pages and never uses a cookie.
 */
object AppApiRoutes {
    const val Base = "/api/v1"
    const val Pairings = "$Base/devices/pairings"
    const val Capabilities = "$Base/instance/capabilities"
    const val InstancePresentation = "$Base/instance/presentation"
    const val Watchlist = "$Base/watchlist"

    /** The shared design tokens the instance serves; a client reads them from here. */
    const val DesignTokens = "/tokens.json"

    /** The server hands the device token in this header instead of a cookie. */
    const val DeviceTokenHeader = "Jularr-Device-Token"

    fun pairingClaim(pairingId: String): String = "$Pairings/$pairingId/claim"

    /** The server's texts of a locale; the client shows them instead of its own words. */
    fun uiTexts(uiLocaleId: Long): String = "$Base/localization/texts/$uiLocaleId"

    fun watchlistPage(page: Int, pageSize: Int): String = "$Watchlist?page=$page&pageSize=$pageSize"
}
