package de.juloc.jularr.core.api

import org.junit.Assert.assertEquals
import org.junit.Test

class AppApiRoutesTest {
    @Test
    fun routesUseTheVersionedAppPath() {
        assertEquals("/api/v1/devices/pairings", AppApiRoutes.Pairings)
        assertEquals("/api/v1/devices/pairings/pairing-1/claim", AppApiRoutes.pairingClaim("pairing-1"))
        assertEquals("/api/v1/instance/capabilities", AppApiRoutes.Capabilities)
    }
}
