package de.juloc.jularr.mobile.offline

import android.content.Context
import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.workDataOf
import de.juloc.jularr.core.api.JularrOfflineApi
import de.juloc.jularr.core.api.ClientApiHttpException
import de.juloc.jularr.core.api.OfflineDownloadJson
import de.juloc.jularr.core.model.EpisodeProgress
import de.juloc.jularr.core.model.OfflineDownloadDescriptor
import de.juloc.jularr.core.model.OfflineProgressResult
import de.juloc.jularr.mobile.ServerOrigin
import de.juloc.jularr.mobile.WebSession
import de.juloc.jularr.core.api.HttpJularrClientApi
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.withContext
import java.io.File
import java.io.IOException
import java.util.concurrent.TimeUnit

sealed interface EnqueueResult {
    data object Queued : EnqueueResult
    data object AlreadyPresent : EnqueueResult
    data class Rejected(val message: String) : EnqueueResult
}

/** A verified, ready download that can be played without the server. */
data class LocalEpisode(
    val download: OfflineDownload,
    val descriptor: OfflineDownloadDescriptor,
    val mediaFile: File,
    val pending: PendingProgress?,
) {
    val resumePositionMs: Long
        get() = OfflineProgressQueue.offlineResumePosition(download, pending)
}

/**
 * Entry point for managed offline downloads on the phone: explicit user
 * actions, WorkManager scheduling, the account boundary and the offline
 * progress sync queue.
 */
class OfflineDownloads private constructor(context: Context) {
    private val appContext = context.applicationContext
    val store: OfflineStore = OfflineStore.get(appContext)

    val state: StateFlow<OfflineSnapshot>
        get() = store.state

    private val workManager: WorkManager
        get() = WorkManager.getInstance(appContext)

    fun find(ownerKey: String, episodeId: String): OfflineDownload? =
        store.snapshot.downloads.firstOrNull { it.ownerKey == ownerKey && it.episodeId == episodeId }

    fun accessible(origin: String, episodeId: String): OfflineDownload? =
        store.snapshot.accessibleDownloads(origin).firstOrNull { it.episodeId == episodeId }

    /** Ready download of the accessible account, or `null`. Never touches the network. */
    fun readyEpisode(origin: String, episodeId: String): LocalEpisode? {
        val download = accessible(origin, episodeId)
            ?.takeIf { it.state == DownloadState.READY }
            ?: return null
        val media = store.mediaFile(download.ownerKey, download.episodeId)
        val descriptorFile = store.descriptorFile(download.ownerKey, download.episodeId)
        if (!media.isFile || media.length() != download.sizeBytes || !descriptorFile.isFile) {
            return null
        }

        val descriptor = runCatching {
            OfflineDownloadJson.parseDescriptor(descriptorFile.readText(Charsets.UTF_8))
        }.getOrNull() ?: return null

        val pending = store.snapshot.pending[download.ownerKey]
            .orEmpty()
            .firstOrNull { it.episodeId == episodeId }
        return LocalEpisode(download, descriptor, media, pending)
    }

    suspend fun enqueue(
        origin: String,
        episodeId: String,
        api: JularrOfflineApi,
    ): EnqueueResult = withContext(Dispatchers.IO) { enqueueOnIo(origin, episodeId, api) }

    private suspend fun enqueueOnIo(
        origin: String,
        episodeId: String,
        api: JularrOfflineApi,
    ): EnqueueResult {
        verifyAccount(origin, api)
        val account = store.snapshot.account?.takeIf { it.signedIn && it.origin == origin }
            ?: return EnqueueResult.Rejected("Sign in to Jularr before downloading episodes.")

        val existing = find(account.ownerKey, episodeId)
        if (existing != null && existing.state != DownloadState.FAILED) {
            return EnqueueResult.AlreadyPresent
        }

        val offlinePackage = try {
            api.getOfflineDownload(episodeId)
        } catch (exception: ClientApiHttpException) {
            return EnqueueResult.Rejected(
                when (exception.statusCode) {
                    401, 403 -> "Sign in to Jularr before downloading episodes."
                    404 -> if (exception.code == null) {
                        "This Jularr server does not support downloads yet."
                    } else {
                        "This episode has no downloadable media file."
                    }
                    503 -> "The media storage is not available right now. Try again later."
                    else -> exception.message
                },
            )
        } catch (exception: IOException) {
            return EnqueueResult.Rejected("Jularr could not be reached.")
        }

        val descriptor = offlinePackage.descriptor
        val media = descriptor.media
        if (media.fingerprintAlgorithm != OfflineMediaVerifier.FingerprintAlgorithm) {
            return EnqueueResult.Rejected("Update the Jularr app to download from this server.")
        }

        val partial = store.partialFile(account.ownerKey, episodeId)
        val keepPartial = existing != null && existing.eTag == media.eTag && partial.isFile
        if (!keepPartial) {
            store.episodeDirectory(account.ownerKey, episodeId).deleteRecursively()
        }
        val alreadyOnDevice = if (keepPartial) partial.length() else 0L

        val snapshot = store.snapshot
        val committed = snapshot.downloads
            .filterNot { it.ownerKey == account.ownerKey && it.episodeId == episodeId }
            .sumOf { it.committedBytes }
        store.root.mkdirs()
        when (
            val decision = OfflineStoragePolicy.admit(
                requestBytes = media.sizeBytes,
                committedBytes = committed,
                limitBytes = snapshot.settings.limitBytes,
                freeDeviceBytes = store.root.usableSpace,
                alreadyOnDeviceBytes = alreadyOnDevice,
            )
        ) {
            StorageDecision.Allowed -> Unit
            is StorageDecision.ExceedsLimit -> return EnqueueResult.Rejected(
                "This episode needs ${OfflineStoragePolicy.formatBytes(decision.requiredBytes)}, but only " +
                    "${OfflineStoragePolicy.formatBytes(decision.availableBytes)} are left under the download limit.",
            )
            is StorageDecision.InsufficientDeviceSpace -> return EnqueueResult.Rejected(
                "Not enough free space on this device for this episode.",
            )
        }

        store.descriptorFile(account.ownerKey, episodeId).apply {
            parentFile?.mkdirs()
            writeText(offlinePackage.json, Charsets.UTF_8)
        }

        val record = OfflineDownload(
            ownerKey = account.ownerKey,
            episodeId = episodeId,
            animeTitle = descriptor.episode.animeTitle,
            episodeTitle = descriptor.episode.title,
            seasonNumber = descriptor.episode.seasonNumber,
            episodeNumber = descriptor.episode.number,
            contentUrl = media.contentUrl,
            sizeBytes = media.sizeBytes,
            eTag = media.eTag,
            fingerprint = media.fingerprint,
            fingerprintAlgorithm = media.fingerprintAlgorithm,
            state = DownloadState.QUEUED,
            downloadedBytes = alreadyOnDevice,
            createdAtMs = existing?.createdAtMs ?: System.currentTimeMillis(),
            resumePositionMs = resumeFrom(descriptor.progress),
            watched = descriptor.progress.isCompleted,
        )

        store.update { current ->
            current.copy(downloads = current.downloads.filterNot { it.sameAs(record) } + record)
        }
        schedule(record)
        return EnqueueResult.Queued
    }

    fun pause(download: OfflineDownload) {
        if (transition(download, DownloadEvent.Pause) != null) {
            workManager.cancelUniqueWork(workName(download))
        }
    }

    fun resume(download: OfflineDownload) {
        transition(download, DownloadEvent.Resume)?.let { schedule(it) }
    }

    fun remove(download: OfflineDownload) {
        workManager.cancelUniqueWork(workName(download))
        store.update { current ->
            current.copy(downloads = current.downloads.filterNot { it.sameAs(download) })
        }
        store.episodeDirectory(download.ownerKey, download.episodeId).deleteRecursively()
    }

    fun setLimit(limitBytes: Long) {
        store.update { it.copy(settings = it.settings.copy(limitBytes = limitBytes)) }
    }

    fun setWifiOnly(wifiOnly: Boolean) {
        val snapshot = store.update { it.copy(settings = it.settings.copy(wifiOnly = wifiOnly)) }
        snapshot.downloads
            .filter { it.state == DownloadState.QUEUED || it.state == DownloadState.DOWNLOADING }
            .forEach { schedule(it) }
    }

    /** Applies a state-machine event if it is legal for the stored state; returns the updated record. */
    fun transition(
        download: OfflineDownload,
        event: DownloadEvent,
        change: (OfflineDownload) -> OfflineDownload = { it },
    ): OfflineDownload? {
        var updated: OfflineDownload? = null
        store.update { current ->
            current.copy(
                downloads = current.downloads.map { stored ->
                    if (!stored.sameAs(download)) {
                        return@map stored
                    }

                    val next = DownloadStateMachine.next(stored.state, event) ?: return@map stored
                    change(stored).copy(
                        state = next,
                        failure = (event as? DownloadEvent.Failed)?.reason,
                    ).also { updated = it }
                },
            )
        }
        return updated
    }

    fun updateDownloadedBytes(download: OfflineDownload, bytes: Long) {
        store.update { current ->
            current.copy(
                downloads = current.downloads.map { stored ->
                    if (stored.sameAs(download) && stored.state == DownloadState.DOWNLOADING) {
                        stored.copy(downloadedBytes = bytes)
                    } else {
                        stored
                    }
                },
            )
        }
    }

    /**
     * Checks which account the current Jularr session belongs to and applies
     * the account boundary. Network failures leave everything unchanged.
     */
    suspend fun verifyAccount(origin: String, api: JularrOfflineApi) {
        val check = try {
            AccountCheck.SignedIn(origin, api.getMe().profileId)
        } catch (exception: ClientApiHttpException) {
            if (exception.statusCode == 401) AccountCheck.SignedOut(origin) else return
        } catch (exception: IOException) {
            return
        }

        applyAccountCheck(check)
    }

    fun applyAccountCheck(check: AccountCheck) {
        when (val decision = OfflineAccountPolicy.decide(store.snapshot.account, check)) {
            AccountDecision.Keep -> Unit
            is AccountDecision.Adopt -> store.update { it.copy(account = decision.account) }
            is AccountDecision.Lock -> {
                store.update { it.copy(account = decision.account) }
                workManager.cancelAllWorkByTag(DownloadTag)
            }
            is AccountDecision.SwitchAndPurge -> {
                purgeOwner(decision.previousOwnerKey)
                store.update { it.copy(account = decision.account) }
            }
        }

        if (store.snapshot.account?.signedIn == true) {
            resumeInterruptedWork()
            store.snapshot.account?.let { scheduleSync(it.ownerKey) }
        }
    }

    /** Removes every download, queued checkpoint and the remembered account (for example on server change). */
    fun clearAll() {
        workManager.cancelAllWorkByTag(DownloadTag)
        workManager.cancelAllWorkByTag(SyncTag)
        store.update { OfflineSnapshot(settings = it.settings) }
        store.root.listFiles()
            ?.filter { it.isDirectory }
            ?.forEach { it.deleteRecursively() }
    }

    // ---- offline progress -------------------------------------------------------------

    /** Queues a checkpoint that could not be delivered live. Ignored without an accessible account. */
    fun recordProgress(
        origin: String,
        episodeId: String,
        positionMs: Long,
        durationMs: Long?,
        completed: Boolean,
    ) {
        val account = store.snapshot.account?.takeIf { it.signedIn && it.origin == origin } ?: return
        store.update { current ->
            val queue = current.pending[account.ownerKey].orEmpty()
            current.copy(
                pending = current.pending + (
                    account.ownerKey to OfflineProgressQueue.record(
                        queue,
                        PendingProgress(episodeId, positionMs, durationMs, completed),
                    )
                ),
                downloads = current.downloads.map { stored ->
                    if (stored.ownerKey == account.ownerKey && stored.episodeId == episodeId) {
                        stored.copy(
                            resumePositionMs = if (completed) 0 else positionMs,
                            watched = stored.watched || completed,
                        )
                    } else {
                        stored
                    }
                },
            )
        }
        scheduleSync(account.ownerKey)
    }

    /**
     * A live checkpoint reached the server: it is newer than a queued partial
     * offline checkpoint of that episode, which is therefore dropped. A queued
     * completion stays queued so the watched state is never lost. The local copy
     * of the canonical progress is refreshed.
     */
    fun onLiveProgress(origin: String, episodeId: String, progress: EpisodeProgress) {
        val account = store.snapshot.account?.takeIf { it.signedIn && it.origin == origin } ?: return
        store.update { current ->
            val queue = current.pending[account.ownerKey].orEmpty()
            current.copy(
                pending = current.pending + (
                    account.ownerKey to OfflineProgressQueue.supersededByLive(queue, episodeId)
                ),
                downloads = current.downloads.withProgress(account.ownerKey, episodeId, progress),
            )
        }
    }

    fun applyReconciliation(
        ownerKey: String,
        sent: List<PendingProgress>,
        results: List<OfflineProgressResult>,
    ) {
        store.update { current ->
            var downloads = current.downloads
            results.forEach { result ->
                result.progress?.let { downloads = downloads.withProgress(ownerKey, result.episodeId, it) }
            }
            val remaining = OfflineProgressQueue.acknowledge(
                current.pending[ownerKey].orEmpty(),
                sent,
                results.map { it.episodeId }.toSet(),
            )
            current.copy(
                pending = current.pending + (ownerKey to remaining),
                downloads = downloads,
            )
        }
    }

    fun scheduleSync(ownerKey: String) {
        if (store.snapshot.pending[ownerKey].isNullOrEmpty()) {
            return
        }

        val request = OneTimeWorkRequestBuilder<OfflineProgressSyncWorker>()
            .setInputData(workDataOf(OfflineProgressSyncWorker.KeyOwner to ownerKey))
            .setConstraints(
                Constraints.Builder()
                    .setRequiredNetworkType(NetworkType.CONNECTED)
                    .build(),
            )
            .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 30, TimeUnit.SECONDS)
            .addTag(SyncTag)
            .build()
        workManager.enqueueUniqueWork("offline-progress:$ownerKey", ExistingWorkPolicy.KEEP, request)
    }

    // ---- internals --------------------------------------------------------------------

    private fun schedule(
        download: OfflineDownload,
        policy: ExistingWorkPolicy = ExistingWorkPolicy.REPLACE,
    ) {
        val wifiOnly = store.snapshot.settings.wifiOnly
        val request = OneTimeWorkRequestBuilder<OfflineDownloadWorker>()
            .setInputData(
                workDataOf(
                    OfflineDownloadWorker.KeyOwner to download.ownerKey,
                    OfflineDownloadWorker.KeyEpisode to download.episodeId,
                ),
            )
            .setConstraints(
                Constraints.Builder()
                    .setRequiredNetworkType(if (wifiOnly) NetworkType.UNMETERED else NetworkType.CONNECTED)
                    .setRequiresStorageNotLow(true)
                    .build(),
            )
            .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 30, TimeUnit.SECONDS)
            .addTag(DownloadTag)
            .build()
        workManager.enqueueUniqueWork(workName(download), policy, request)
    }

    private fun resumeInterruptedWork() {
        val account = store.snapshot.account ?: return
        store.snapshot.downloads
            .filter { it.ownerKey == account.ownerKey }
            .filter { it.state == DownloadState.QUEUED || it.state == DownloadState.DOWNLOADING }
            .forEach { schedule(it, ExistingWorkPolicy.KEEP) }
    }

    private fun purgeOwner(ownerKey: String) {
        store.snapshot.downloads
            .filter { it.ownerKey == ownerKey }
            .forEach { workManager.cancelUniqueWork(workName(it)) }
        workManager.cancelUniqueWork("offline-progress:$ownerKey")
        store.update { current ->
            current.copy(
                downloads = current.downloads.filterNot { it.ownerKey == ownerKey },
                pending = current.pending - ownerKey,
            )
        }
        store.ownerDirectory(ownerKey).deleteRecursively()
    }

    private fun List<OfflineDownload>.withProgress(
        ownerKey: String,
        episodeId: String,
        progress: EpisodeProgress,
    ): List<OfflineDownload> =
        map { stored ->
            if (stored.ownerKey == ownerKey && stored.episodeId == episodeId) {
                stored.copy(resumePositionMs = resumeFrom(progress), watched = progress.isCompleted)
            } else {
                stored
            }
        }

    companion object {
        const val DownloadTag = "offline-download"
        const val SyncTag = "offline-progress"

        @Volatile
        private var instance: OfflineDownloads? = null

        fun get(context: Context): OfflineDownloads =
            instance ?: synchronized(this) {
                instance ?: OfflineDownloads(context).also { instance = it }
            }

        fun workName(download: OfflineDownload): String =
            "offline-download:${download.ownerKey}:${download.episodeId}"

        /** Same resume rule as live playback: very early positions start from the beginning. */
        fun resumeFrom(progress: EpisodeProgress): Long =
            if (!progress.isCompleted && progress.positionMs >= 5_000) progress.positionMs else 0L

        /** HTTP client for background work, authenticated with the app's Jularr session cookie. */
        fun backgroundApi(origin: String): HttpJularrClientApi {
            val session = ServerOrigin.parse(origin).getOrNull()?.let(::WebSession)
            return HttpJularrClientApi(
                origin = origin,
                requestHeaders = { session?.requestHeaders().orEmpty() },
                responseCookieSink = { cookies -> session?.acceptResponseCookies(cookies) },
            )
        }

        fun backgroundHeaders(origin: String): Map<String, String> =
            ServerOrigin.parse(origin).getOrNull()?.let { WebSession(it).requestHeaders() }.orEmpty()
    }
}

private fun OfflineDownload.sameAs(other: OfflineDownload): Boolean =
    ownerKey == other.ownerKey && episodeId == other.episodeId
