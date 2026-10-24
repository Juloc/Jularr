package de.juloc.jularr.mobile

import de.juloc.jularr.core.model.MediaAvailability
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class StorageRecoveryPolicyTest {
    @Test
    fun offlineStorageRetriesButFileMissingDoesNotLoop() {
        assertTrue(StorageRecoveryPolicy.shouldAutomaticallyRetry(availability("source_offline")))
        assertFalse(StorageRecoveryPolicy.shouldAutomaticallyRetry(availability("file_missing")))
    }

    @Test
    fun ownerWakeIsVisibleOnlyWhenServerExplicitlyAllowsIt() {
        assertTrue(
            StorageRecoveryPolicy.canOfferWake(
                availability(
                    state = "source_offline",
                    canWake = true,
                    rootId = "root",
                    wakeUrl = "/api/client/v1/library-roots/root/wake",
                ),
            ),
        )

        assertFalse(
            StorageRecoveryPolicy.canOfferWake(
                availability(
                    state = "source_offline",
                    canWake = false,
                    rootId = null,
                    wakeUrl = null,
                ),
            ),
        )
    }

    private fun availability(
        state: String,
        canWake: Boolean = false,
        rootId: String? = null,
        wakeUrl: String? = null,
    ) = MediaAvailability(
        state = state,
        retryable = state != "file_missing",
        retryAfterMs = 1000,
        canWake = canWake,
        rootId = rootId,
        availabilityUrl = "/availability",
        wakeUrl = wakeUrl,
    )
}
