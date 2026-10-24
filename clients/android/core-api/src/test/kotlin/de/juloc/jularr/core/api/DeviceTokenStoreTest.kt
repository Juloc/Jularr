package de.juloc.jularr.core.api

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class DeviceTokenStoreTest {
    @Test
    fun aRenewedTokenReplacesTheCurrentOne() {
        val store = DeviceTokenStore()

        store.update("token-1")
        store.update("token-2")

        assertEquals("token-2", store.current)
    }

    @Test
    fun anAnswerWithoutATokenKeepsTheCurrentOne() {
        val store = DeviceTokenStore()
        store.update("token-1")

        store.update(null)
        store.update("")

        assertEquals("token-1", store.current)
    }

    @Test
    fun aClearedStoreHasNoToken() {
        val store = DeviceTokenStore()
        store.update("token-1")

        store.clear()

        assertNull(store.current)
    }
    @Test
    fun restoredAndRotatedTokensArePersistedByTheCaller() {
        val saved = mutableListOf<String?>()
        val store = DeviceTokenStore(initialToken = "restored-token", onChanged = { saved.add(it) })
        assertEquals("restored-token", store.current)
        store.update("rotated-token")
        store.update("rotated-token")
        store.clear()
        assertEquals(listOf("rotated-token", null), saved)
        assertNull(store.current)
    }

}
