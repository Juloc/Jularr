package de.juloc.jularr.core.api

import de.juloc.jularr.core.model.DevicePairingClaim
import de.juloc.jularr.core.model.DevicePairingRequest
import de.juloc.jularr.core.model.DevicePairingStarted
import de.juloc.jularr.core.model.InstanceCapabilities

/**
 * The app services a client needs before it shows anything: pair the device, read what the
 * instance allows and keep the session alive.
 */
interface AppApiClient {
    /** Starts a pairing; the device shows the returned code. */
    suspend fun startPairing(request: DevicePairingRequest): DevicePairingStarted

    /** Fetches the session of a pairing; it succeeds once a signed-in profile confirmed the code. */
    suspend fun claimPairing(pairingId: String): DevicePairingClaim

    /** Reads the permissions and the modules of the paired device. */
    suspend fun capabilities(): InstanceCapabilities
}
