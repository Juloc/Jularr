package de.juloc.jularr.tv

import de.juloc.jularr.core.api.AppApiException
import kotlinx.coroutines.delay

/**
 * The clean-start playback endpoint itself sends a Wake-on-LAN packet only
 * when the mount access fails and WakeOnDemand/MAC are configured.
 *
 * We retry ONLY the explicit 'storage_waking' response. A missing, denied or
 * offline mount without configured WOL is returned to the user unchanged.
 * Cancellation is allowed to escape so Back/exit stops the retry loop.
 */
internal suspend fun <T> retryWhileStorageWaking(
    start: suspend () -> T,
    onWaiting: (Int, Int) -> Unit = { _, _ -> },
    maxAttempts: Int = 9,
    retryDelayMs: Long = 8_000L,
    wait: suspend (Long) -> Unit = { delay(it) },
): T {
    require(maxAttempts in 1..20)
    require(retryDelayMs in 0..30_000L)
    for (attempt in 1..maxAttempts) {
        try {
            return start()
        } catch (failure: AppApiException) {
            if (failure.code != "storage_waking" || attempt == maxAttempts) throw failure
            onWaiting(attempt, maxAttempts)
            wait(retryDelayMs)
        }
    }
    error("Unreachable playback retry state")
}
