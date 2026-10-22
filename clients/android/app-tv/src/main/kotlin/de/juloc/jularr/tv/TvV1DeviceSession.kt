package de.juloc.jularr.tv

import de.juloc.jularr.core.api.AppApiClient
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.DeviceTokenStore
import de.juloc.jularr.core.api.HttpAppApiClient
import de.juloc.jularr.core.model.DevicePairingClaim
import de.juloc.jularr.core.model.DevicePairingRequest
import de.juloc.jularr.core.model.DevicePairingStarted
import de.juloc.jularr.core.model.DevicePlatform
import de.juloc.jularr.core.model.InstanceCapabilities

/**
 * Clean-start device pairing for the original TV client.
 *
 * Pairing and bearer-token lifecycle are independent of the legacy /api/client/v1 interface.
 * The existing TV UI must be switched to these methods together with the catalog adapter;
 * never treat a V1 device token as an old session cookie.
 */
class TvV1DeviceSession(
    private val persistence: TvDeviceTokenPersistence,
    private val apiFactory: (String, DeviceTokenStore) -> AppApiClient = { origin, tokens ->
        HttpAppApiClient(origin, tokens)
    },
) {
    private var origin: String? = null
    private var tokens: DeviceTokenStore? = null
    private var api: AppApiClient? = null

    val hasDeviceToken: Boolean
        get() = tokens?.current != null

    /** Select one server, restoring only that server's encrypted device token. */
    fun selectServer(rawOrigin: String) {
        val normalized = TvServerOrigin.normalize(rawOrigin)
        if (origin == normalized && api != null) return

        val store = DeviceTokenStore(
            initialToken = persistence.loadToken(normalized),
            onChanged = { token -> persistence.storeToken(normalized, token) },
        )
        origin = normalized
        tokens = store
        api = apiFactory(normalized, store)
    }

    /** Verify a stored token; a revoked token is cleared by the HTTP client on 401. */
    suspend fun restore(): InstanceCapabilities? {
        if (!hasDeviceToken) return null
        return try {
            client().capabilities()
        } catch (failure: AppApiException) {
            if (failure.statusCode != 401) throw failure
            null
        }
    }

    /** Start a code-pairing. The six-digit code is public, but pairingId stays on the TV. */
    suspend fun startPairing(replacesPairingId: String? = null): DevicePairingStarted =
        client().startPairing(
            DevicePairingRequest(
                deviceName = "Jularr TV",
                devicePlatform = DevicePlatform.AndroidTv,
                replacesPairingId = replacesPairingId,
            ),
        )

    /** Claim at the server's polling interval. The token is persisted by the HTTP response. */
    suspend fun claimPairing(pairingId: String): DevicePairingClaim {
        val claim = client().claimPairing(pairingId)
        if (claim.isPaired) {
            // Verify the claimed session before declaring device sign-in complete.
            check(hasDeviceToken) { "Pairing returned no persisted device token." }
            client().capabilities()
        }
        return claim
    }

    /** Only clear this selected server's local credentials; never touch legacy cookies. */
    fun forgetDevice() {
        val currentOrigin = origin ?: return
        persistence.storeToken(currentOrigin, null)
        tokens = DeviceTokenStore(
            onChanged = { token -> persistence.storeToken(currentOrigin, token) },
        )
        api = apiFactory(currentOrigin, requireNotNull(tokens))
    }

    private fun client(): AppApiClient =
        api ?: error("Select a Jularr server before starting TV device pairing.")
}
