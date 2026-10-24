package de.juloc.jularr.mobile.offline

sealed interface DownloadEvent {
    /** The worker started transferring bytes. */
    data object Start : DownloadEvent

    /** The user paused the download. */
    data object Pause : DownloadEvent

    /** The user resumed a paused download. */
    data object Resume : DownloadEvent

    /** The transfer stopped for a transient reason (network, process death) and will be retried. */
    data object Interrupted : DownloadEvent

    /** Size and fingerprint matched the server identity. */
    data object Verified : DownloadEvent

    data class Failed(val reason: String) : DownloadEvent

    /** The user retried a failed download. */
    data object Retry : DownloadEvent
}

/**
 * Allowed download state transitions. Returns `null` for transitions that
 * must be ignored, for example a late worker progress update after the user
 * paused, or anything but removal once a download is ready.
 */
object DownloadStateMachine {
    fun next(state: DownloadState, event: DownloadEvent): DownloadState? =
        when (state) {
            DownloadState.QUEUED -> when (event) {
                DownloadEvent.Start -> DownloadState.DOWNLOADING
                DownloadEvent.Pause -> DownloadState.PAUSED
                is DownloadEvent.Failed -> DownloadState.FAILED
                else -> null
            }

            DownloadState.DOWNLOADING -> when (event) {
                DownloadEvent.Pause -> DownloadState.PAUSED
                DownloadEvent.Interrupted -> DownloadState.QUEUED
                DownloadEvent.Verified -> DownloadState.READY
                is DownloadEvent.Failed -> DownloadState.FAILED
                else -> null
            }

            DownloadState.PAUSED -> when (event) {
                DownloadEvent.Resume -> DownloadState.QUEUED
                else -> null
            }

            DownloadState.FAILED -> when (event) {
                DownloadEvent.Retry -> DownloadState.QUEUED
                else -> null
            }

            DownloadState.READY -> null
        }
}
