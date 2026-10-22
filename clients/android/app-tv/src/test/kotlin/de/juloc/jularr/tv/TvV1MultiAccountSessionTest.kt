package de.juloc.jularr.tv

import de.juloc.jularr.core.api.AppApiClient
import de.juloc.jularr.core.api.DeviceTokenStore
import de.juloc.jularr.core.model.DevicePairingClaim
import de.juloc.jularr.core.model.DevicePairingRequest
import de.juloc.jularr.core.model.DevicePairingStarted
import de.juloc.jularr.core.model.InstanceCapabilities
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class TvV1MultiAccountSessionTest {
    private class Vault : TvV1AccountPersistence {
        private data class Slot(val origin: String, val id: String, val label: String, var token: String?)
        private val saved = mutableListOf<Slot>()
        private val active = mutableMapOf<String, String>()
        override fun listV1Accounts(serverOrigin: String) = saved.filter { it.origin == serverOrigin }
            .map { TvV1SavedAccount(it.id, it.label, !it.token.isNullOrEmpty()) }
        override fun ensureV1Account(serverOrigin: String): String =
            active[serverOrigin] ?: createV1Account(serverOrigin)
        override fun createV1Account(serverOrigin: String): String {
            val id = UUID.randomUUID().toString()
            saved.add(Slot(serverOrigin, id, "Anmeldung ${saved.count { it.origin == serverOrigin }}", null))
            active[serverOrigin] = id
            return id
        }
        override fun activateV1Account(serverOrigin: String, id: String) {
            require(saved.any { it.origin == serverOrigin && it.id == id })
            active[serverOrigin] = id
        }
        override fun loadV1AccountToken(serverOrigin: String, id: String): String? =
            saved.single { it.origin == serverOrigin && it.id == id }.token
        override fun storeV1AccountToken(serverOrigin: String, id: String, token: String?) {
            saved.single { it.origin == serverOrigin && it.id == id }.token = token
        }
        override fun loadToken(serverOrigin: String): String? =
            loadV1AccountToken(serverOrigin, ensureV1Account(serverOrigin))
        override fun storeToken(serverOrigin: String, token: String?) =
            storeV1AccountToken(serverOrigin, ensureV1Account(serverOrigin), token)
    }

    private class FakeApi(private val token: DeviceTokenStore) : AppApiClient {
        override suspend fun startPairing(request: DevicePairingRequest) =
            DevicePairingStarted("pair", "123456", "2026-10-22T10:00:00Z")
        override suspend fun claimPairing(pairingId: String): DevicePairingClaim =
            DevicePairingClaim(isPaired = false)
        override suspend fun capabilities() =
            InstanceCapabilities(listOf("profile.playback"), listOf("Library", "Playback"))
    }

    @Test fun twoAccountsOnOneServerNeverShareCredentialsEvenWhenOldHttpCallRotatesLate() {
        val vault = Vault()
        val session = TvV1DeviceSession(vault) { _, token -> FakeApi(token) }
        session.selectServer("https://jularr.example")
        val first = session.accounts().single().id
        val oldHttpTokenStore = session.authenticatedTokenStore()
        oldHttpTokenStore.update("alice-device-token")
        session.beginNewAccount()
        val second = session.accounts().last().id
        assertNotEquals(first, second)
        assertFalse(session.hasDeviceToken)
        // A network response started before the account switch can only update
        // its CAPTURED original slot, never the selected account.
        oldHttpTokenStore.update("alice-rotated-late")
        assertNull(vault.loadV1AccountToken("https://jularr.example", second))
        session.selectAccount(first)
        assertEquals("alice-rotated-late", session.authenticatedTokenStore().current)
        session.selectAccount(second)
        assertFalse(session.hasDeviceToken)
    }

    @Test fun forgetDeviceClearsOnlySelectedAccountAndPreservesTheOther() {
        val vault = Vault()
        val session = TvV1DeviceSession(vault) { _, token -> FakeApi(token) }
        session.selectServer("https://jularr.example")
        val first = session.accounts().single().id
        session.authenticatedTokenStore().update("first-token")
        session.beginNewAccount()
        val second = session.accounts().last().id
        // Populate new slot after pairing.
        session.selectAccount(first)
        session.selectAccount(second)
        assertFalse(session.hasDeviceToken)
        // Simulate token persistence via currently selected slot.
        vault.storeV1AccountToken("https://jularr.example", second, "second-token")
        session.selectAccount(first)
        session.forgetDevice()
        assertFalse(session.hasDeviceToken)
        session.selectAccount(second)
        assertEquals("second-token", session.authenticatedTokenStore().current)
    }
}
