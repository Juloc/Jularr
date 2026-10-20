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
    fun aStoredTokenSurvivesTheNextStart() {
        val storage = MemoryStorage()
        val store = DeviceTokenStore(storage)
        store.update("token-1")

        val next = DeviceTokenStore(storage)

        assertEquals("token-1", next.current)
        assertEquals("token-1", storage.read())
    }

    @Test
    fun aClearedStoreForgetsTheStoredToken() {
        val storage = MemoryStorage()
        val store = DeviceTokenStore(storage)
        store.update("token-1")

        store.clear()

        assertNull(storage.read())
    }

    private class MemoryStorage : DeviceTokenStorage {
        private var token: String? = null

        override fun read(): String? = token

        override fun write(token: String) {
            this.token = token
        }

        override fun clear() {
            token = null
        }
    }
}
