package de.juloc.jularr.core.api

/**
 * The versioned app services of the backend. A client signs in as a paired device and calls
 * these routes with its device token; it never scrapes the web pages and never uses a cookie.
 */
object AppApiRoutes {
    const val Base = "/api/v1"
    const val Pairings = "$Base/devices/pairings"
    const val Capabilities = "$Base/instance/capabilities"

    /** The server hands the device token in this header instead of a cookie. */
    const val DeviceTokenHeader = "Jularr-Device-Token"

    fun pairingClaim(pairingId: String): String = "$Pairings/$pairingId/claim"
}
