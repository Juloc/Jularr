package de.juloc.jularr.mobile.offline.library

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.work.CoroutineWorker
import androidx.work.ForegroundInfo
import androidx.work.WorkerParameters
import de.juloc.jularr.MainActivity
import de.juloc.jularr.core.api.JularrLibraryApi
import de.juloc.jularr.core.api.ClientApiHttpException
import de.juloc.jularr.mobile.offline.DownloadEvent
import de.juloc.jularr.mobile.offline.DownloadState
import de.juloc.jularr.mobile.offline.OfflineDownloads
import de.juloc.jularr.mobile.offline.OfflineNotifications
import de.juloc.jularr.mobile.offline.OfflineStoragePolicy
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.IOException

/**
 * Downloads one chapter's payload JSON and verifies it before marking it
 * ready. Chapters are treated as one atomic unit (not sub-file Range/resume
 * like episode media, #341): they are bounded-size text, and the *job* is the
 * resumable unit via WorkManager's own retry/backoff, exactly like the PWA
 * download queue (docs/OFFLINE_LIBRARY.md).
 */
class LibraryChapterDownloadWorker(
    context: Context,
    params: WorkerParameters,
) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val ownerKey = inputData.getString(KeyOwner) ?: return Result.failure()
        val chapterId = inputData.getString(KeyChapter) ?: return Result.failure()
        val downloads = LibraryDownloads.get(applicationContext)

        val record = downloads.findChapter(ownerKey, chapterId) ?: return Result.success()
        if (record.state != DownloadState.QUEUED && record.state != DownloadState.DOWNLOADING) {
            return Result.success()
        }

        val account = downloads.store.snapshot.account
        if (account == null || account.ownerKey != ownerKey || !account.signedIn) {
            // Signed out or another account: keep the record, continue after sign-in.
            return Result.success()
        }

        val active = if (record.state == DownloadState.QUEUED) {
            downloads.transitionChapter(record, DownloadEvent.Start) ?: return Result.success()
        } else {
            record
        }

        runCatching { setForeground(foregroundInfo(active)) }

        return try {
            withContext(Dispatchers.IO) { transfer(downloads, account.origin, active) }
        } catch (exception: CancellationException) {
            downloads.transitionChapter(active, DownloadEvent.Interrupted)
            throw exception
        }
    }

    private suspend fun transfer(
        downloads: LibraryDownloads,
        origin: String,
        chapter: LibraryChapterRecord,
    ): Result {
        val api: JularrLibraryApi = OfflineDownloads.backgroundApi(origin)

        val chapterPackage = try {
            api.getLibraryChapter(chapter.chapterId)
        } catch (exception: ClientApiHttpException) {
            return when (exception.statusCode) {
                401, 403 -> {
                    downloads.transitionChapter(chapter, DownloadEvent.Interrupted)
                    Result.success()
                }
                404 -> fail(downloads, chapter, "This chapter no longer exists on the server.")
                else -> if (exception.statusCode in 500..599 || exception.statusCode == 429) {
                    downloads.transitionChapter(chapter, DownloadEvent.Interrupted)
                    Result.retry()
                } else {
                    fail(downloads, chapter, exception.message)
                }
            }
        } catch (exception: IOException) {
            downloads.transitionChapter(chapter, DownloadEvent.Interrupted)
            return Result.retry()
        }

        val payload = chapterPackage.payload
        if (payload.hash != chapter.hash) {
            // The chapter changed again between the manifest and this fetch: retry later
            // rather than mark a mismatched hash as verified (docs/OFFLINE_LIBRARY.md).
            downloads.transitionChapter(chapter, DownloadEvent.Interrupted)
            return Result.retry()
        }

        val bytes = chapterPackage.json.toByteArray(Charsets.UTF_8)
        if (!OfflineStoragePolicy.hasRoomFor(bytes.size.toLong(), downloads.store.root.usableSpace)) {
            return fail(downloads, chapter, "The device ran out of free space.")
        }

        val target = downloads.store.chapterFile(chapter.ownerKey, chapter.chapterId)
        if (!LibraryContentIo.writeVerified(target, bytes)) {
            return fail(downloads, chapter, "The downloaded chapter could not be verified. Retry the download.")
        }

        val current = downloads.findChapter(chapter.ownerKey, chapter.chapterId)
        if (current == null || current.state != DownloadState.DOWNLOADING) {
            // Removed or paused while downloading.
            return Result.success()
        }

        downloads.transitionChapter(chapter, DownloadEvent.Verified) { it.copy(verifiedHash = payload.hash) }
        return Result.success()
    }

    private fun fail(downloads: LibraryDownloads, chapter: LibraryChapterRecord, reason: String?): Result {
        downloads.transitionChapter(chapter, DownloadEvent.Failed(reason ?: "The chapter download failed."))
        return Result.success()
    }

    private fun foregroundInfo(chapter: LibraryChapterRecord): ForegroundInfo {
        val notification = NotificationCompat.Builder(applicationContext, OfflineNotifications.ensureChannel(applicationContext))
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle("Downloading a book chapter")
            .setContentText(chapter.title)
            .setOnlyAlertOnce(true)
            .setOngoing(true)
            .setProgress(0, 0, true)
            .setContentIntent(LibraryNotifications.openLibraryDownloads(applicationContext))
            .build()
        val notificationId = ("library:" + chapter.chapterId).hashCode()

        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ForegroundInfo(notificationId, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            ForegroundInfo(notificationId, notification)
        }
    }

    companion object {
        const val KeyOwner = "ownerKey"
        const val KeyChapter = "chapterId"
    }
}

/**
 * Replays queued offline reading-progress and bookmark events through
 * `POST /offline-library/sync` once the server is reachable. The server's
 * forward-only/last-writer-wins rules make replays harmless.
 */
class LibrarySyncWorker(
    context: Context,
    params: WorkerParameters,
) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val ownerKey = inputData.getString(KeyOwner) ?: return Result.failure()
        val downloads = LibraryDownloads.get(applicationContext)

        repeat(MaxRounds) {
            val account = downloads.store.snapshot.account?.takeIf { it.ownerKey == ownerKey }
                ?: return Result.success()
            val progressQueue = downloads.store.snapshot.pendingProgress[ownerKey].orEmpty()
            val bookmarkQueue = downloads.store.snapshot.pendingBookmarks[ownerKey].orEmpty()
            if (progressQueue.isEmpty() && bookmarkQueue.isEmpty()) {
                return Result.success()
            }

            val api: JularrLibraryApi = OfflineDownloads.backgroundApi(account.origin)
            try {
                // Only the account that recorded the checkpoints may reconcile them.
                if (api.getMe().profileId != account.profileId) {
                    return Result.success()
                }

                val progressBatch = LibraryProgressQueue.batch(progressQueue, JularrLibraryApi.MaxSyncBatchItems)
                val bookmarkBatch = LibraryBookmarkQueue.batch(bookmarkQueue, JularrLibraryApi.MaxSyncBatchItems)
                val result = api.syncLibrary(
                    progress = progressBatch.map(LibraryProgressQueue::toEvent),
                    bookmarks = bookmarkBatch.map(LibraryBookmarkQueue::toEvent),
                )
                downloads.applyReconciliation(ownerKey, progressBatch, bookmarkBatch, result.progress, result.bookmarks)
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

        val snapshot = downloads.store.snapshot
        val drained = snapshot.pendingProgress[ownerKey].isNullOrEmpty() && snapshot.pendingBookmarks[ownerKey].isNullOrEmpty()
        return if (drained) Result.success() else Result.retry()
    }

    companion object {
        const val KeyOwner = "ownerKey"
        private const val MaxRounds = 5
    }
}

object LibraryNotifications {
    const val ActionOpenLibraryDownloads = "de.juloc.jularr.action.LIBRARY_DOWNLOADS"

    fun openLibraryDownloads(context: Context): PendingIntent =
        PendingIntent.getActivity(
            context,
            0,
            Intent(context, MainActivity::class.java)
                .setAction(ActionOpenLibraryDownloads)
                .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
}
