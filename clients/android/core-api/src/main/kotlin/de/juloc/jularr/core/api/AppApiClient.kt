package de.juloc.jularr.core.api

import de.juloc.jularr.core.design.DesignTokens
import de.juloc.jularr.core.model.AuthenticationV1
import de.juloc.jularr.core.model.ContinueItemV1
import de.juloc.jularr.core.model.DevicePairingClaim
import de.juloc.jularr.core.model.DevicePairingRequest
import de.juloc.jularr.core.model.DevicePairingStarted
import de.juloc.jularr.core.model.DiscoverScopeV1
import de.juloc.jularr.core.model.HomeShelfV1
import de.juloc.jularr.core.model.InstanceCapabilities
import de.juloc.jularr.core.model.InstancePresentationV1
import de.juloc.jularr.core.model.ProfileSelectionV1
import de.juloc.jularr.core.model.WorkCardV1

/**
 * The app services a client needs: pair the device, read what the instance allows and keep the
 * session alive. Every answer comes from the versioned app services of the Backend.
 */
interface AppApiClient {
    /** Starts a pairing; the device shows the returned code. */
    suspend fun startPairing(request: DevicePairingRequest): DevicePairingStarted

    /** Fetches the session of a pairing; it succeeds once a signed-in profile confirmed the code. */
    suspend fun claimPairing(pairingId: String): DevicePairingClaim

    /** Reads the permissions and the modules of the paired device. */
    suspend fun capabilities(): InstanceCapabilities

    /** Reads the signed-in state: the account, the profile and the locale of the instance. */
    suspend fun authentication(): AuthenticationV1

    /** Reads the public display values of the instance. */
    suspend fun instancePresentation(): InstancePresentationV1

    /** Reads the server's texts of a locale; the client shows them instead of its own words. */
    suspend fun uiTexts(uiLocaleId: Long): Map<String, String>

    /** Reads one page of the profile's watchlist. */
    suspend fun watchlist(page: Int = 1, pageSize: Int = 25): List<WorkCardV1>

    /** Reads one page of the profile's library, the recent works. */
    suspend fun library(page: Int = 1, pageSize: Int = 25): List<WorkCardV1>

    /** Reads the shared design tokens the instance serves. */
    suspend fun designTokens(): DesignTokens

    /** Reads the titles the profile continues: watched, listened to and read. */
    suspend fun continueWatching(): List<ContinueItemV1>

    /** Reads the shelves of the profile's home page in their order, the hidden ones among them. */
    suspend fun homeLayout(): List<HomeShelfV1>

    /** Reads the media types the profile may browse. */
    suspend fun discoverScopes(): List<DiscoverScopeV1>

    /** Reads the profiles the account of the device may use. */
    suspend fun profiles(): List<ProfileSelectionV1>

    /** Switches the device to another profile; the answer carries a new device token. */
    suspend fun selectProfile(profileId: Long)
}
