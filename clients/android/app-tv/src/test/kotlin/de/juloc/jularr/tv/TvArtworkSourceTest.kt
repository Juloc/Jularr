package de.juloc.jularr.tv

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TvArtworkSourceTest {
    @Test
    fun relativeArtworkUsesServerOriginAndAuthentication() {
        val source = resolveArtworkSource(
            "https://jularr.example:8443",
            "/artwork/anime/123/poster?v=1",
        )!!

        assertEquals(
            "https://jularr.example:8443/artwork/anime/123/poster?v=1",
            source.url,
        )
        assertTrue(source.authenticated)
    }

    @Test
    fun absoluteSameOriginArtworkUsesAuthentication() {
        val source = resolveArtworkSource(
            "https://jularr.example",
            "https://jularr.example/artwork/anime/123/poster",
        )!!

        assertTrue(source.authenticated)
    }

    @Test
    fun externalProviderArtworkNeverReceivesJularrAuthentication() {
        val source = resolveArtworkSource(
            "https://jularr.example",
            "https://cdn.example.test/poster.jpg",
        )!!

        assertFalse(source.authenticated)
    }

    @Test
    fun differentPortIsNotSameOrigin() {
        val source = resolveArtworkSource(
            "https://jularr.example:8443",
            "https://jularr.example/poster.jpg",
        )!!

        assertFalse(source.authenticated)
    }

    @Test
    fun blankArtworkIsIgnored() {
        assertNull(
            resolveArtworkSource(
                "https://jularr.example",
                "   ",
            ),
        )
    }
}
