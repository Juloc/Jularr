package de.juloc.jularr.tv

import de.juloc.jularr.core.model.MediaAvailability
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TvStorageRecoveryPolicyTest {
    @Test
    fun missingFileNeverRetriesOrOffersWake() {
        val decision = TvStorageRecoveryPolicy.decide(
            availability("file_missing", retryable = false, canWake = true),
            elapsedMs = 0,
        )

        assertEquals(TvStorageAction.STOP, decision.primaryAction)
        assertFalse(decision.canWake)
    }

    @Test
    fun offlineStorageRetriesOnlyInsideBoundedWindow() {
        val current = TvStorageRecoveryPolicy.decide(
            availability("source_offline", retryable = true),
            elapsedMs = 59_000,
        )
        val expired = TvStorageRecoveryPolicy.decide(
            availability("source_offline", retryable = true),
            elapsedMs = 60_000,
        )

        assertEquals(TvStorageAction.RETRY, current.primaryAction)
        assertEquals(TvStorageAction.STOP, expired.primaryAction)
    }

    @Test
    fun wakeIsOnlyExposedWhenServerExplicitlyProvidesIt() {
        val owner = TvStorageRecoveryPolicy.decide(
            availability("source_offline", retryable = true, canWake = true),
            elapsedMs = 0,
        )
        val user = TvStorageRecoveryPolicy.decide(
            availability("source_offline", retryable = true, canWake = false),
            elapsedMs = 0,
        )

        assertTrue(owner.canWake)
        assertFalse(user.canWake)
    }

    private fun availability(
        state: String,
        retryable: Boolean,
        canWake: Boolean = false,
    ) = MediaAvailability(
        state = state,
        retryable = retryable,
        retryAfterMs = 2_000,
        canWake = canWake,
        rootId = if (canWake) "root" else null,
        availabilityUrl = "/availability",
        wakeUrl = if (canWake) "/wake" else null,
    )
}
