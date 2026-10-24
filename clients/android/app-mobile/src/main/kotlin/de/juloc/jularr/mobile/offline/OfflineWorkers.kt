package de.juloc.jularr.mobile.offline

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.SystemClock
import androidx.core.app.NotificationChannelCompat
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.work.CoroutineWorker
import androidx.work.ForegroundInfo
import androidx.work.WorkerParameters
import de.juloc.jularr.MainActivity
import de.juloc.jularr.core.api.JularrOfflineApi
import de.juloc.jularr.core.api.ClientApiHttpException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.IOException
import java.net.URI

/**
 * Resumable transfer of one episode. WorkManager persists the request, so the
 * transfer continues after process death or a device restart from the
 * partial file length (HTTP Range + If-Range on the descriptor's ETag).
 */
class OfflineDownloadWorker(
    context: Context,
    params: WorkerParameters,
) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val ownerKey = inputData.getString(KeyOwner) ?: return Result.failure()
        val episodeId = inputData.getString(KeyEpisode) ?: return Result.failure()
        val downloads = OfflineDownloads.get(applicationContext)
        val store = downloads.store

        val record = downloads.find(ownerKey, episodeId) ?: return Result.success()
        if (record.state != DownloadState.QUEUED && record.state != DownloadState.DOWNLOADING) {
            return Result.success()
        }

        val account = store.snapshot.account
        if (account == null || account.ownerKey != ownerKey || !account.signedIn) {
            // Signed out or another account: keep the partial file, continue after sign-in.
            return Result.success()
        }

        val active = if (record.state == DownloadState.QUEUED) {
            downloads.transition(record, DownloadEvent.Start) ?: return Result.success()
        } else {
            record
        }

        runCatching { setForeground(foregroundInfo(active)) }

        return try {
            withContext(Dispatchers.IO) { transfer(downloads, account.origin, active) }
        } catch (exception: CancellationException) {
            downloads.transition(active, DownloadEvent.Interrupted)
            throw exception
        }
    }

    private fun transfer(
        downloads: OfflineDownloads,
        origin: String,
        download: OfflineDownload,
    ): Result {
        val store = downloads.store
        val partial = store.partialFile(download.ownerKey, download.episodeId)
        val url = URI(origin).resolve(download.contentUrl).toURL()
        var lastReport = 0L

        val outcome = OfflineMediaDownloader().transfer(
            url = url,
            headers = OfflineDownloads.backgroundHeaders(origin),
            target = partial,
            expectedSizeBytes = download.sizeBytes,
            expectedETag = download.eTag,
            shouldContinue = {
                !isStopped && downloads.find(download.ownerKey, download.episodeId)?.state == DownloadState.DOWNLOADING
            },
            onProgress = { bytes ->
                val now = SystemClock.elapsedRealtime()
                if (now - lastReport >= ProgressIntervalMs) {
                    lastReport = now
                    downloads.updateDownloadedBytes(download, bytes)
                }
            },
        )

        downloads.updateDownloadedBytes(download, if (partial.exists()) partial.length() else 0L)

        return when (outcome) {
            TransferOutcome.Complete -> finish(downloads, download)
            TransferOutcome.Stopped -> {
                // Paused or removed by the user, or stopped by the system (which reschedules us).
                downloads.transition(download, DownloadEvent.Interrupted)
                Result.success()
            }
            is TransferOutcome.Retry -> {
                downloads.transition(download, DownloadEvent.Interrupted)
                Result.retry()
            }
            TransferOutcome.SignedOut -> fail(downloads, download, "Sign in to Jularr to continue this download.", keepPartial = true)
            TransferOutcome.SourceChanged -> fail(downloads, download, "The episode file changed on the server. Retry to download the current version.", keepPartial = false)
            TransferOutcome.Missing -> fail(downloads, download, "The episode file is no longer available on the server.", keepPartial = false)
            TransferOutcome.DeviceFull -> fail(downloads, download, "The device ran out of free space.", keepPartial = true)
            is TransferOutcome.Failed -> fail(downloads, download, outcome.reason, keepPartial = false)
        }
    }

    private fun finish(downloads: OfflineDownloads, download: OfflineDownload): Result {
        val store = downloads.store
        val partial = store.partialFile(download.ownerKey, download.episodeId)
        val verification = OfflineMediaVerifier.verify(
            file = partial,
            expectedSizeBytes = download.sizeBytes,
            expectedFingerprint = download.fingerprint,
            algorithm = download.fingerprintAlgorithm,
        )

        if (verification != VerificationResult.Verified) {
            return fail(
                downloads,
                download,
                "The downloaded file did not match the server copy. Retry the download.",
                keepPartial = false,
            )
        }

        val current = downloads.find(download.ownerKey, download.episodeId)
        if (current == null) {
            // Removed while verifying.
            partial.delete()
            return Result.success()
        }

        if (current.state != DownloadState.DOWNLOADING) {
            // Paused while verifying: keep the complete partial file; resuming verifies again.
            return Result.success()
        }

        val media = store.mediaFile(download.ownerKey, download.episodeId)
        if (!partial.renameTo(media)) {
            return fail(downloads, download, "The download could not be stored on this device.", keepPartial = false)
        }

        downloads.transition(download, DownloadEvent.Verified) { it.copy(downloadedBytes = it.sizeBytes) }
        return Result.success()
    }

    private fun fail(
        downloads: OfflineDownloads,
        download: OfflineDownload,
        reason: String,
        keepPartial: Boolean,
    ): Result {
        if (!keepPartial) {
            downloads.store.partialFile(download.ownerKey, download.episodeId).delete()
        }
        downloads.transition(download, DownloadEvent.Failed(reason)) {
            it.copy(downloadedBytes = if (keepPartial) it.downloadedBytes else 0L)
        }
        return Result.success()
    }

    private fun foregroundInfo(download: OfflineDownload): ForegroundInfo {
        val notification = NotificationCompat.Builder(applicationContext, OfflineNotifications.ensureChannel(applicationContext))
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle("Downloading ${download.animeTitle}")
            .setContentText("S${download.seasonNumber} · E${download.episodeNumber} · ${download.episodeTitle}")
            .setOnlyAlertOnce(true)
            .setOngoing(true)
            .setProgress(0, 0, true)
            .setContentIntent(OfflineNotifications.openDownloads(applicationContext))
            .build()
        val notificationId = download.episodeId.hashCode()

        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ForegroundInfo(notificationId, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            ForegroundInfo(notificationId, notification)
        }
    }

    companion object {
        const val KeyOwner = "ownerKey"
        const val KeyEpisode = "episodeId"
        private const val ProgressIntervalMs = 1_000L
    }
}

/**
 * Replays queued offline checkpoints through `POST /offline/progress` once the
 * server is reachable. The server's monotonic rules make replays harmless, so
 * a retry after a lost response cannot move progress backwards.
 */
class OfflineProgressSyncWorker(
    context: Context,
    params: WorkerParameters,
) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val ownerKey = inputData.getString(KeyOwner) ?: return Result.failure()
        val downloads = OfflineDownloads.get(applicationContext)

        repeat(MaxRounds) {
            val account = downloads.store.snapshot.account
                ?.takeIf { it.ownerKey == ownerKey }
                ?: return Result.success()
            val queue = downloads.store.snapshot.pending[ownerKey].orEmpty()
            if (queue.isEmpty()) {
                return Result.success()
            }

            val api: JularrOfflineApi = OfflineDownloads.backgroundApi(account.origin)
            try {
                // Only the account that recorded the checkpoints may reconcile them.
                if (api.getMe().profileId != account.profileId) {
                    return Result.success()
                }

                val batch = OfflineProgressQueue.batch(queue, JularrOfflineApi.MaxProgressBatchItems)
                val results = api.reconcileOfflineProgress(batch.map(OfflineProgressQueue::toItem))
                downloads.applyReconciliation(ownerKey, batch, results)
            } catch (exception: ClientApiHttpException) {
                return if (exception.statusCode in 400..499 && exception.statusCode != 401 && exception.statusCode != 429) {
                    Result.failure()
                } else {
                    Result.retry()
                }
            } catch (exception: IOException) {
                return Result.retry()
            }
        }

        return if (downloads.store.snapshot.pending[ownerKey].isNullOrEmpty()) Result.success() else Result.retry()
    }

    companion object {
        const val KeyOwner = "ownerKey"
        private const val MaxRounds = 5
    }
}

object OfflineNotifications {
    private const val ChannelId = "offline-downloads"
    const val ActionOpenDownloads = "de.juloc.jularr.action.DOWNLOADS"

    fun ensureChannel(context: Context): String {
        NotificationManagerCompat.from(context).createNotificationChannel(
            NotificationChannelCompat.Builder(ChannelId, NotificationManagerCompat.IMPORTANCE_LOW)
                .setName("Episode downloads")
                .build(),
        )
        return ChannelId
    }

    fun openDownloads(context: Context): PendingIntent =
        PendingIntent.getActivity(
            context,
            0,
            Intent(context, MainActivity::class.java)
                .setAction(ActionOpenDownloads)
                .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
}
