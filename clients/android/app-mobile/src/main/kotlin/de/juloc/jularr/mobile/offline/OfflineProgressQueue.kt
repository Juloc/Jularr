package de.juloc.jularr.mobile.offline

import de.juloc.jularr.core.model.OfflineProgressItem

/**
 * Local sync queue for playback checkpoints that could not reach the server.
 * It is not a progress store: the server applies its monotonic reconciliation
 * rules and every server outcome removes the queued entry again.
 */
object OfflineProgressQueue {
    /**
     * Keeps one entry per episode: the latest position the user reached, with
     * a completed checkpoint staying completed (watched is sticky).
     */
    fun record(
        queue: List<PendingProgress>,
        checkpoint: PendingProgress,
    ): List<PendingProgress> {
        val previous = queue.firstOrNull { it.episodeId == checkpoint.episodeId }
        val merged = if (previous == null) {
            checkpoint
        } else {
            checkpoint.copy(
                completed = previous.completed || checkpoint.completed,
                durationMs = checkpoint.durationMs ?: previous.durationMs,
            )
        }

        return queue.filterNot { it.episodeId == checkpoint.episodeId } + merged
    }

    /**
     * Removes checkpoints the server has answered. An entry that changed after
     * the batch was taken (newer local playback) stays queued for the next run.
     */
    fun acknowledge(
        queue: List<PendingProgress>,
        sent: List<PendingProgress>,
        answeredEpisodeIds: Set<String>,
    ): List<PendingProgress> =
        queue.filterNot { entry ->
            entry.episodeId in answeredEpisodeIds && entry in sent
        }

    /**
     * A live checkpoint reached the server and supersedes a queued partial
     * checkpoint of the same episode. A queued completion is kept so the
     * watched state recorded offline is never lost.
     */
    fun supersededByLive(
        queue: List<PendingProgress>,
        episodeId: String,
    ): List<PendingProgress> =
        queue.filterNot { it.episodeId == episodeId && !it.completed }

    fun batch(queue: List<PendingProgress>, limit: Int): List<PendingProgress> =
        queue.take(limit)

    fun toItem(entry: PendingProgress) = OfflineProgressItem(
        episodeId = entry.episodeId,
        positionMs = entry.positionMs.coerceAtLeast(0),
        durationMs = entry.durationMs?.takeIf { it > 0 },
        completed = entry.completed,
    )

    /** Resume position for offline playback from the local copy plus a queued checkpoint. */
    fun offlineResumePosition(
        download: OfflineDownload,
        pending: PendingProgress?,
    ): Long =
        when {
            pending?.completed == true -> 0L
            pending != null -> pending.positionMs
            download.watched -> 0L
            else -> download.resumePositionMs
        }
}
