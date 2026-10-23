package de.juloc.jularr.tv

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class TvServerOriginTest {
    @Test
    fun normalizesServerOriginWithoutTrailingSlash() {
        assertEquals(
            "https://jularr.example",
            TvServerOrigin.normalize(" HTTPS://Jularr.Example/ "),
        )
        assertEquals(
            "http://192.168.1.5:8097",
            TvServerOrigin.normalize("http://192.168.1.5:8097"),
        )
    }

    @Test
    fun rejectsNonHttpOriginsAndEmbeddedCredentials() {
        assertThrows(IllegalArgumentException::class.java) {
            TvServerOrigin.normalize("file:///data/jularr")
        }
        assertThrows(IllegalArgumentException::class.java) {
            TvServerOrigin.normalize("https://user:password@example.test")
        }
    }

    @Test
    fun rejectsPathsQueriesAndFragments() {
        assertThrows(IllegalArgumentException::class.java) {
            TvServerOrigin.normalize("https://example.test/jularr")
        }
        assertThrows(IllegalArgumentException::class.java) {
            TvServerOrigin.normalize("https://example.test/?token=secret")
        }
        assertThrows(IllegalArgumentException::class.java) {
            TvServerOrigin.normalize("https://example.test/#player")
        }
    }
}
