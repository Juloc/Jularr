package de.juloc.jularr.tv

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class TvSessionCookieStoreTest {
    @Test
    fun capturesOnlyCookiePairsForRequests() {
        val store = TvSessionCookieStore()
        store.accept(
            listOf(
                ".AspNetCore.Cookies=abc.def==; path=/; httponly; samesite=lax",
                "other=value; path=/",
            ),
        )

        assertEquals(
            mapOf("Cookie" to ".AspNetCore.Cookies=abc.def==; other=value"),
            store.requestHeaders(),
        )
    }

    @Test
    fun selectingAnotherAccountDoesNotPersistOrErasePreviouslySavedCookies() {
        val persisted = mutableListOf<Map<String, String>>()
        val store = TvSessionCookieStore { persisted += it.toMap() }
        store.accept(listOf("session=account-a; path=/"))
        store.loadCookies(emptyMap())
        store.loadCookies(mapOf("session" to "account-b"))

        assertEquals(1, persisted.size)
        assertEquals(mapOf("session" to "account-a"), persisted.single())
        assertEquals(mapOf("Cookie" to "session=account-b"), store.requestHeaders())
    }

    @Test
    fun expiredCookieIsRemoved() {
        val store = TvSessionCookieStore()
        store.accept(listOf(".AspNetCore.Cookies=session; path=/"))
        store.accept(listOf(".AspNetCore.Cookies=; expires=Thu, 01 Jan 1970 00:00:00 GMT; max-age=0"))

        assertTrue(store.requestHeaders().isEmpty())
        assertTrue(store.isEmpty())
    }
}
