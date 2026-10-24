package de.juloc.jularr.tv

import de.juloc.jularr.core.api.AppApiException
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class TvV1StorageWakeRetryTest {
    private fun waking() = AppApiException(503, "storage_waking", "error.storage_waking", null, "waking")
    private fun offline() = AppApiException(503, "storage_unavailable", "error.storage_unavailable", null, "offline")

    @Test fun retriesOnlyAfterServerActuallyAttemptedNasWake() = runBlocking {
        var attempts = 0
        val waitValues = mutableListOf<Long>()
        val got = retryWhileStorageWaking(
            start = { if (++attempts < 3) throw waking(); "stream" },
            onWaiting = { _, _ -> },
            retryDelayMs = 8_000,
            wait = { waitValues.add(it) },
        )
        assertEquals("stream", got)
        assertEquals(3, attempts)
        assertEquals(listOf(8000L, 8000L), waitValues)
    }

    @Test fun inaccessibleMountWithoutConfiguredWakeIsNotRetried() {
        var attempts = 0
        assertThrows(AppApiException::class.java) {
            runBlocking {
                retryWhileStorageWaking(
                    start = { attempts++; throw offline() },
                    wait = { error("Must not delay or retry") },
                )
            }
        }
        assertEquals(1, attempts)
    }

    @Test fun exhaustedWakeAttemptsReturnRealServerCode() {
        var attempts = 0
        val failure = assertThrows(AppApiException::class.java) {
            runBlocking {
                retryWhileStorageWaking(
                    start = { attempts++; throw waking() },
                    maxAttempts = 3, wait = {},
                )
            }
        }
        assertEquals("storage_waking", failure.code)
        assertEquals(3, attempts)
    }
}
