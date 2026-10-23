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
    private var accountSlot: String? = null
    private var tokens: DeviceTokenStore? = null
    private var api: AppApiClient? = null

    val hasDeviceToken: Boolean
        get() = tokens?.current != null

    /**
     * Give the catalog and playback clients the *same* rotating token store as pairing.
     * No bearer token is passed to Compose or copied into the legacy cookie store.
     */
    fun authenticatedTokenStore(): DeviceTokenStore {
        check(hasDeviceToken) { "The TV must be paired before requesting media." }
        return requireNotNull(tokens)
    }

    /** Select the active account slot on one server, never another account's bearer. */
    fun selectServer(rawOrigin: String) {
        val normalized = TvServerOrigin.normalize(rawOrigin)
        val vault = persistence as? TvV1AccountPersistence
        val slot = vault?.ensureV1Account(normalized)
        if (origin == normalized && accountSlot == slot && api != null) return
        val store = if (vault != null && slot != null) {
            DeviceTokenStore(
                initialToken = vault.loadV1AccountToken(normalized, slot),
                onChanged = { token -> vault.storeV1AccountToken(normalized, slot, token) },
            )
        } else {
            DeviceTokenStore(
                initialToken = persistence.loadToken(normalized),
                onChanged = { token -> persistence.storeToken(normalized, token) },
            )
        }
        origin = normalized
        accountSlot = slot
        tokens = store
        api = apiFactory(normalized, store)
    }

    fun accounts(): List<TvV1SavedAccount> =
        (persistence as? TvV1AccountPersistence)?.let { vault ->
            origin?.let(vault::listV1Accounts)
        } ?: emptyList()

    fun selectAccount(id: String) {
        val server = requireNotNull(origin)
        val vault = persistence as? TvV1AccountPersistence
            ?: error("Multiple accounts are not supported by this credential store.")
        vault.activateV1Account(server, id)
        selectServer(server)
    }

    fun beginNewAccount() {
        val server = requireNotNull(origin)
        val vault = persistence as? TvV1AccountPersistence
            ?: error("Multiple accounts are not supported by this credential store.")
        vault.createV1Account(server)
        selectServer(server)
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
        val vault = persistence as? TvV1AccountPersistence
        val slot = accountSlot
        if (vault != null && slot != null) {
            vault.storeV1AccountToken(currentOrigin, slot, null)
        } else {
            persistence.storeToken(currentOrigin, null)
        }
        // Force a new token store, so no stale bearer is retained in memory.
        origin = null
        accountSlot = null
        tokens = null
        api = null
        selectServer(currentOrigin)
    }

    private fun client(): AppApiClient =
        api ?: error("Select a Jularr server before starting TV device pairing.")
}
