package de.juloc.jularr.tv

import de.juloc.jularr.core.api.AppApiClient
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.DeviceTokenStore
import de.juloc.jularr.core.model.DevicePairingClaim
import de.juloc.jularr.core.model.DevicePairingRequest
import de.juloc.jularr.core.model.DevicePairingStarted
import de.juloc.jularr.core.model.DevicePlatform
import de.juloc.jularr.core.model.InstanceCapabilities
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TvV1DeviceSessionTest {
    private class MemoryVault : TvDeviceTokenPersistence {
        val values = mutableMapOf<String, String>()
        override fun loadToken(serverOrigin: String): String? = values[serverOrigin]
        override fun storeToken(serverOrigin: String, token: String?) {
            if (token == null) values.remove(serverOrigin) else values[serverOrigin] = token
        }
    }

    private class FakeApi(val tokens: DeviceTokenStore) : AppApiClient {
        var approved = false
        var seenRequest: DevicePairingRequest? = null
        var capabilitiesCalled = 0
        var revoked = false

        override suspend fun startPairing(request: DevicePairingRequest): DevicePairingStarted {
            seenRequest = request
            return DevicePairingStarted("pairing-1", "123456", "2026-10-22T10:00:00Z")
        }

        override suspend fun claimPairing(pairingId: String): DevicePairingClaim {
            if (!approved) return DevicePairingClaim(isPaired = false)
            tokens.update("device-token")
            return DevicePairingClaim(isPaired = true, deviceToken = "device-token")
        }

        override suspend fun capabilities(): InstanceCapabilities {
            capabilitiesCalled += 1
            if (revoked) {
                tokens.clear()
                throw AppApiException(401, "invalid_token", "error.invalid_token", null, "Unauthorized")
            }
            return InstanceCapabilities(listOf("profile.playback"), listOf("Devices", "Playback"))
        }
    }

    @Test
    fun theDevicePairsThroughTheV1ContractAndEncryptableVaultBoundary() = runBlocking {
        val vault = MemoryVault()
        lateinit var fake: FakeApi
        val flow = TvV1DeviceSession(vault) { _, store -> FakeApi(store).also { fake = it } }
        flow.selectServer("https://EXAMPLE.com/")
        assertNull(flow.restore())

        val pairing = flow.startPairing()
        assertEquals("123456", pairing.code)
        assertEquals(DevicePlatform.AndroidTv, fake.seenRequest?.devicePlatform)
        assertFalse(flow.claimPairing(pairing.pairingId).isPaired)
        fake.approved = true
        assertTrue(flow.claimPairing(pairing.pairingId).isPaired)
        assertEquals("device-token", vault.loadToken("https://example.com"))
        assertEquals(1, fake.capabilitiesCalled)
        assertTrue(flow.hasDeviceToken)
    }

    @Test
    fun aSavedTokenIsRestoredOnlyForTheMatchingServerAndRevocationClearsIt() = runBlocking {
        val vault = MemoryVault()
        vault.storeToken("https://one.example", "saved-token")
        lateinit var fake: FakeApi
        val flow = TvV1DeviceSession(vault) { _, store -> FakeApi(store).also { fake = it } }
        flow.selectServer("https://one.example")
        assertTrue(flow.hasDeviceToken)
        assertTrue(requireNotNull(flow.restore()).hasPermission("profile.playback"))
        fake.revoked = true
        assertNull(flow.restore())
        assertFalse(flow.hasDeviceToken)
        assertNull(vault.loadToken("https://one.example"))

        flow.selectServer("https://two.example")
        assertFalse(flow.hasDeviceToken)
        assertNull(flow.restore())
    }

    @Test
    fun forgettingOneDeviceDoesNotRemoveOtherServerTokens() {
        val vault = MemoryVault()
        vault.storeToken("https://one.example", "first")
        vault.storeToken("https://two.example", "second")
        val flow = TvV1DeviceSession(vault) { _, store -> FakeApi(store) }
        flow.selectServer("https://one.example")
        flow.forgetDevice()
        assertFalse(flow.hasDeviceToken)
        assertNull(vault.loadToken("https://one.example"))
        assertEquals("second", vault.loadToken("https://two.example"))
    }
}
