package de.juloc.jularr.mobile.offline.library

import android.content.Context
import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.workDataOf
import de.juloc.jularr.core.api.JularrLibraryApi
import de.juloc.jularr.core.api.ClientApiHttpException
import de.juloc.jularr.core.model.ClientOfflineLibraryManifest
import de.juloc.jularr.core.model.OfflineLibraryBookmarkResult
import de.juloc.jularr.core.model.OfflineLibraryProgressResult
import de.juloc.jularr.mobile.offline.AccountCheck
import de.juloc.jularr.mobile.offline.AccountDecision
import de.juloc.jularr.mobile.offline.DownloadEvent
import de.juloc.jularr.mobile.offline.DownloadState
import de.juloc.jularr.mobile.offline.DownloadStateMachine
import de.juloc.jularr.mobile.offline.OfflineAccountPolicy
import de.juloc.jularr.mobile.offline.OfflineDownloads
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.withContext
import java.io.IOException
import java.net.URI
import java.util.concurrent.TimeUnit

sealed interface LibraryEnqueueResult {
    data object Queued : LibraryEnqueueResult
    data object UpToDate : LibraryEnqueueResult
    data class Rejected(val message: String) : LibraryEnqueueResult
}

/**
 * Entry point for the offline Book/Novel library on the phone: explicit user
 * actions, WorkManager scheduling for chapter downloads, the account
 * boundary and the reading-state sync queue. Deliberately mirrors
 * `OfflineDownloads` (bounded offline playback, #341) and reuses its
 * account/state-machine building blocks directly (`OfflineAccountPolicy`,
 * `DownloadStateMachine`) since neither is anime-specific; this file only
 * adds the parts that differ for a "work -> volume -> chapter" tree instead
 * of one media file: manifest diffing and differential per-chapter download.
 */
class LibraryDownloads private constructor(context: Context) {
    private val appContext = context.applicationContext
    val store: LibraryStore = LibraryStore.get(appContext)

    val state: StateFlow<LibrarySnapshot>
        get() = store.state

    private val workManager: WorkManager
        get() = WorkManager.getInstance(appContext)

    fun findBook(ownerKey: String, workId: String): LibraryBookRecord? =
        store.snapshot.books.firstOrNull { it.ownerKey == ownerKey && it.workId == workId }

    fun accessibleBook(origin: String, workId: String): LibraryBookRecord? =
        store.snapshot.accessibleBooks(origin).firstOrNull { it.workId == workId }

    fun chaptersOf(ownerKey: String, workId: String): List<LibraryChapterRecord> =
        store.snapshot.chaptersOf(ownerKey, workId)

    fun findChapter(ownerKey: String, chapterId: String): LibraryChapterRecord? =
        store.snapshot.chapters.firstOrNull { it.ownerKey == ownerKey && it.chapterId == chapterId }

    // ---- local, network-free reads (used by the WebView interception) -----------------

    /** Manifest JSON of a locally-stored book, or `null`. Never touches the network. */
    fun localManifestJson(ownerKey: String, workId: String): String? {
        val file = store.manifestFile(ownerKey, workId)
        return if (file.isFile) runCatching { file.readText(Charsets.UTF_8) }.getOrNull() else null
    }

    /** Chapter payload JSON of a verified, up-to-date local chapter, or `null`. */
    fun localChapterJson(ownerKey: String, chapterId: String): String? {
        val chapter = findChapter(ownerKey, chapterId)?.takeIf { it.isUpToDate } ?: return null
        val file = store.chapterFile(ownerKey, chapterId)
        return if (file.isFile) runCatching { file.readText(Charsets.UTF_8) }.getOrNull() else null
    }

    /** A downloaded asset file (cover/illustration), or `null`. */
    fun localAssetFile(ownerKey: String, asset: String): java.io.File? =
        store.assetFile(ownerKey, asset).takeIf { it.isFile }

    /**
     * Resolves a same-origin request path against locally downloaded content
     * for the signed-in account of [origin], or `null` to fall through to the
     * network. Used by the WebView's `shouldInterceptRequest` so the same web
     * reader can render offline content ("local source first"; see
     * docs/ANDROID_CLIENTS.md §8.1 and docs/OFFLINE_LIBRARY.md). Same-origin
     * enforcement is the caller's responsibility.
     */
    fun resolveLocalRequest(origin: String, path: String): LibraryLocalResponse? {
        val account = store.snapshot.account?.takeIf { it.signedIn && it.origin == origin } ?: return null
        return when (val target = LibraryRequestInterception.match(path)) {
            is LibraryInterceptTarget.Manifest ->
                localManifestJson(account.ownerKey, target.workId)?.let {
                    LibraryLocalResponse("application/json", it.toByteArray(Charsets.UTF_8))
                }
            is LibraryInterceptTarget.Chapter ->
                localChapterJson(account.ownerKey, target.chapterId)?.let {
                    LibraryLocalResponse("application/json", it.toByteArray(Charsets.UTF_8))
                }
            is LibraryInterceptTarget.Asset ->
                localAssetFile(account.ownerKey, target.asset)?.let { file ->
                    LibraryLocalResponse(LibraryRequestInterception.contentTypeFor(target.asset), file.readBytes())
                }
            null -> null
        }
    }

    // ---- downloads ----------------------------------------------------------------------

    suspend fun enqueueBook(
        origin: String,
        workId: String,
        api: JularrLibraryApi,
        chapterIds: Set<String>? = null,
    ): LibraryEnqueueResult = withContext(Dispatchers.IO) { enqueueBookOnIo(origin, workId, api, chapterIds) }

    private suspend fun enqueueBookOnIo(
        origin: String,
        workId: String,
        api: JularrLibraryApi,
        chapterIds: Set<String>?,
    ): LibraryEnqueueResult {
        verifyAccount(origin, api)
        val account = store.snapshot.account?.takeIf { it.signedIn && it.origin == origin }
            ?: return LibraryEnqueueResult.Rejected("Sign in to Jularr before downloading books.")

        val manifestPackage = try {
            api.getLibraryManifest(workId)
        } catch (exception: ClientApiHttpException) {
            return LibraryEnqueueResult.Rejected(
                when (exception.statusCode) {
                    401, 403 -> "Sign in to Jularr before downloading books."
                    404 -> "This book does not exist on this server."
                    else -> exception.message
                },
            )
        } catch (exception: IOException) {
            return LibraryEnqueueResult.Rejected("Jularr could not be reached.")
        }

        val manifest = manifestPackage.manifest
        val existingBook = findBook(account.ownerKey, workId)
        val wholeBook = chapterIds == null && (existingBook?.wholeBook ?: true)
        val selection = chapterIds ?: existingBook?.selectedChapterIds
        val wants: (String) -> Boolean = { chapterId -> wholeBook || selection?.contains(chapterId) == true }

        LibraryContentIo.writeVerified(
            store.manifestFile(account.ownerKey, workId),
            manifestPackage.json.toByteArray(Charsets.UTF_8),
        )

        val diffs = LibraryManifestDiff.diff(chaptersOf(account.ownerKey, workId), manifest, wants)
        val anyChange = diffs.any { it !is ManifestChapterDiff.Keep }

        // Removed chapter files are deleted outside the store transform, matching OfflineDownloads.remove.
        diffs.filterIsInstance<ManifestChapterDiff.Remove>().forEach { diff ->
            store.chapterFile(diff.chapter.ownerKey, diff.chapter.chapterId).delete()
        }

        store.update { current ->
            var chapters = current.chapters
            diffs.forEach { diff ->
                when (diff) {
                    is ManifestChapterDiff.Keep -> Unit
                    is ManifestChapterDiff.Download -> {
                        val record = LibraryChapterRecord(
                            ownerKey = account.ownerKey,
                            workId = workId,
                            chapterId = diff.ref.chapterId,
                            volumeId = diff.ref.volumeId,
                            number = diff.ref.number,
                            title = diff.ref.title,
                            hash = diff.ref.hash,
                            verifiedHash = null,
                            state = DownloadState.QUEUED,
                            createdAtMs = chapters.firstOrNull {
                                it.ownerKey == account.ownerKey && it.chapterId == diff.ref.chapterId
                            }?.createdAtMs ?: System.currentTimeMillis(),
                        )
                        chapters = chapters.filterNot {
                            it.ownerKey == account.ownerKey && it.chapterId == diff.ref.chapterId
                        } + record
                    }
                    is ManifestChapterDiff.Remove -> {
                        chapters = chapters.filterNot {
                            it.ownerKey == diff.chapter.ownerKey && it.chapterId == diff.chapter.chapterId
                        }
                    }
                }
            }

            val book = LibraryBookRecord(
                ownerKey = account.ownerKey,
                workId = workId,
                title = manifest.title,
                author = manifest.author,
                coverAssetUrl = manifest.coverAssetUrl,
                contentVersion = manifest.contentVersion,
                selectedChapterIds = selection,
                wholeBook = wholeBook,
                assetFileNames = existingBook?.assetFileNames ?: emptySet(),
                createdAtMs = existingBook?.createdAtMs ?: System.currentTimeMillis(),
            )

            current.copy(
                books = current.books.filterNot { it.ownerKey == account.ownerKey && it.workId == workId } + book,
                chapters = chapters,
            )
        }

        chaptersOf(account.ownerKey, workId)
            .filter { it.state == DownloadState.QUEUED }
            .forEach { schedule(it) }

        // Cover/volume-cover assets: best effort, never gates "available offline".
        runCatching { downloadMissingAssets(account.origin, account.ownerKey, workId, manifest) }

        return if (!anyChange) LibraryEnqueueResult.UpToDate else LibraryEnqueueResult.Queued
    }

    private suspend fun downloadMissingAssets(
        origin: String,
        ownerKey: String,
        workId: String,
        manifest: ClientOfflineLibraryManifest,
    ) {
        val assetUrls = buildSet {
            manifest.coverAssetUrl?.let { add(it) }
            manifest.volumes.forEach { volume -> volume.coverAssetUrl?.let { add(it) } }
        }
        if (assetUrls.isEmpty()) {
            return
        }

        val book = findBook(ownerKey, workId) ?: return
        val headers = OfflineDownloads.backgroundHeaders(origin)
        val downloaded = mutableSetOf<String>()

        assetUrls.forEach { relativeUrl ->
            val assetName = relativeUrl.substringAfterLast('/')
            if (assetName.isBlank() || assetName in book.assetFileNames) {
                return@forEach
            }
            val target = store.assetFile(ownerKey, assetName)
            if (target.isFile) {
                downloaded += assetName
                return@forEach
            }
            runCatching {
                val url = URI(origin).resolve(relativeUrl).toURL()
                LibraryContentFetcher.get(url, headers)
            }.getOrNull()?.let { bytes ->
                if (LibraryContentIo.writeVerified(target, bytes)) {
                    downloaded += assetName
                }
            }
        }

        if (downloaded.isNotEmpty()) {
            store.update { current ->
                current.copy(
                    books = current.books.map { stored ->
                        if (stored.ownerKey == ownerKey && stored.workId == workId) {
                            stored.copy(assetFileNames = stored.assetFileNames + downloaded)
                        } else {
                            stored
                        }
                    },
                )
            }
        }
    }

    fun pauseChapter(chapter: LibraryChapterRecord) {
        if (transitionChapter(chapter, DownloadEvent.Pause) != null) {
            workManager.cancelUniqueWork(chapterWorkName(chapter))
        }
    }

    fun resumeChapter(chapter: LibraryChapterRecord) {
        transitionChapter(chapter, DownloadEvent.Resume)?.let { schedule(it) }
    }

    fun retryChapter(chapter: LibraryChapterRecord) {
        transitionChapter(chapter, DownloadEvent.Retry)?.let { schedule(it) }
    }

    /** Removes one chapter and narrows the book's selection so it is not silently re-added later. */
    fun removeChapter(chapter: LibraryChapterRecord) {
        workManager.cancelUniqueWork(chapterWorkName(chapter))
        store.chapterFile(chapter.ownerKey, chapter.chapterId).delete()
        store.update { current ->
            val remainingSelection = current.chapters
                .filter { it.ownerKey == chapter.ownerKey && it.workId == chapter.workId }
                .map { it.chapterId }
                .toMutableSet()
                .apply { remove(chapter.chapterId) }
            current.copy(
                chapters = current.chapters.filterNot {
                    it.ownerKey == chapter.ownerKey && it.chapterId == chapter.chapterId
                },
                books = current.books.map { stored ->
                    if (stored.ownerKey == chapter.ownerKey && stored.workId == chapter.workId) {
                        stored.copy(wholeBook = false, selectedChapterIds = remainingSelection)
                    } else {
                        stored
                    }
                },
            )
        }
    }

    fun removeBook(book: LibraryBookRecord) {
        chaptersOf(book.ownerKey, book.workId).forEach { chapter ->
            workManager.cancelUniqueWork(chapterWorkName(chapter))
            store.chapterFile(chapter.ownerKey, chapter.chapterId).delete()
        }
        book.assetFileNames.forEach { asset -> store.assetFile(book.ownerKey, asset).delete() }
        store.manifestFile(book.ownerKey, book.workId).delete()
        store.update { current ->
            current.copy(
                books = current.books.filterNot { it.ownerKey == book.ownerKey && it.workId == book.workId },
                chapters = current.chapters.filterNot { it.ownerKey == book.ownerKey && it.workId == book.workId },
            )
        }
    }

    fun setWifiOnly(wifiOnly: Boolean) {
        val snapshot = store.update { it.copy(settings = it.settings.copy(wifiOnly = wifiOnly)) }
        snapshot.chapters
            .filter { it.state == DownloadState.QUEUED || it.state == DownloadState.DOWNLOADING }
            .forEach { schedule(it) }
    }

    /** Applies a state-machine event if it is legal for the stored state; returns the updated record. */
    fun transitionChapter(
        chapter: LibraryChapterRecord,
        event: DownloadEvent,
        change: (LibraryChapterRecord) -> LibraryChapterRecord = { it },
    ): LibraryChapterRecord? {
        var updated: LibraryChapterRecord? = null
        store.update { current ->
            current.copy(
                chapters = current.chapters.map { stored ->
                    if (stored.ownerKey != chapter.ownerKey || stored.chapterId != chapter.chapterId) {
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

    // ---- account boundary (reuses OfflineAccountPolicy, #341) --------------------------

    suspend fun verifyAccount(origin: String, api: JularrLibraryApi) {
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

    /** Removes every book, chapter, queued event and the remembered account (for example on server change). */
    fun clearAll() {
        workManager.cancelAllWorkByTag(DownloadTag)
        workManager.cancelAllWorkByTag(SyncTag)
        store.update { LibrarySnapshot(settings = it.settings) }
        store.root.listFiles()?.filter { it.isDirectory }?.forEach { it.deleteRecursively() }
    }

    // ---- reading-state sync queue --------------------------------------------------------

    fun recordProgress(origin: String, entry: LibraryPendingProgress) {
        val account = store.snapshot.account?.takeIf { it.signedIn && it.origin == origin } ?: return
        store.update { current ->
            val queue = current.pendingProgress[account.ownerKey].orEmpty()
            current.copy(
                pendingProgress = current.pendingProgress + (account.ownerKey to LibraryProgressQueue.record(queue, entry)),
            )
        }
        scheduleSync(account.ownerKey)
    }

    fun recordBookmark(origin: String, entry: LibraryPendingBookmark) {
        val account = store.snapshot.account?.takeIf { it.signedIn && it.origin == origin } ?: return
        store.update { current ->
            val queue = current.pendingBookmarks[account.ownerKey].orEmpty()
            current.copy(
                pendingBookmarks = current.pendingBookmarks + (account.ownerKey to LibraryBookmarkQueue.record(queue, entry)),
            )
        }
        scheduleSync(account.ownerKey)
    }

    fun applyReconciliation(
        ownerKey: String,
        sentProgress: List<LibraryPendingProgress>,
        sentBookmarks: List<LibraryPendingBookmark>,
        progressResults: List<OfflineLibraryProgressResult>,
        bookmarkResults: List<OfflineLibraryBookmarkResult>,
    ) {
        store.update { current ->
            current.copy(
                pendingProgress = current.pendingProgress + (
                    ownerKey to LibraryProgressQueue.acknowledge(
                        current.pendingProgress[ownerKey].orEmpty(),
                        sentProgress,
                        progressResults.workIds(),
                    )
                ),
                pendingBookmarks = current.pendingBookmarks + (
                    ownerKey to LibraryBookmarkQueue.acknowledge(
                        current.pendingBookmarks[ownerKey].orEmpty(),
                        sentBookmarks,
                        bookmarkResults.bookmarkIds(),
                    )
                ),
            )
        }
    }

    fun scheduleSync(ownerKey: String) {
        val snapshot = store.snapshot
        if (snapshot.pendingProgress[ownerKey].isNullOrEmpty() && snapshot.pendingBookmarks[ownerKey].isNullOrEmpty()) {
            return
        }

        val request = OneTimeWorkRequestBuilder<LibrarySyncWorker>()
            .setInputData(workDataOf(LibrarySyncWorker.KeyOwner to ownerKey))
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
            .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 30, TimeUnit.SECONDS)
            .addTag(SyncTag)
            .build()
        workManager.enqueueUniqueWork("library-sync:$ownerKey", ExistingWorkPolicy.KEEP, request)
    }

    // ---- internals ------------------------------------------------------------------------

    private fun schedule(chapter: LibraryChapterRecord, policy: ExistingWorkPolicy = ExistingWorkPolicy.REPLACE) {
        val wifiOnly = store.snapshot.settings.wifiOnly
        val request = OneTimeWorkRequestBuilder<LibraryChapterDownloadWorker>()
            .setInputData(
                workDataOf(
                    LibraryChapterDownloadWorker.KeyOwner to chapter.ownerKey,
                    LibraryChapterDownloadWorker.KeyChapter to chapter.chapterId,
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
        workManager.enqueueUniqueWork(chapterWorkName(chapter), policy, request)
    }

    private fun resumeInterruptedWork() {
        val account = store.snapshot.account ?: return
        store.snapshot.chapters
            .filter { it.ownerKey == account.ownerKey }
            .filter { it.state == DownloadState.QUEUED || it.state == DownloadState.DOWNLOADING }
            .forEach { schedule(it, ExistingWorkPolicy.KEEP) }
    }

    private fun purgeOwner(ownerKey: String) {
        store.snapshot.chapters
            .filter { it.ownerKey == ownerKey }
            .forEach { workManager.cancelUniqueWork(chapterWorkName(it)) }
        workManager.cancelUniqueWork("library-sync:$ownerKey")
        store.update { current ->
            current.copy(
                books = current.books.filterNot { it.ownerKey == ownerKey },
                chapters = current.chapters.filterNot { it.ownerKey == ownerKey },
                pendingProgress = current.pendingProgress - ownerKey,
                pendingBookmarks = current.pendingBookmarks - ownerKey,
            )
        }
        store.ownerDirectory(ownerKey).deleteRecursively()
    }

    companion object {
        const val DownloadTag = "library-download"
        const val SyncTag = "library-sync"

        @Volatile
        private var instance: LibraryDownloads? = null

        fun get(context: Context): LibraryDownloads =
            instance ?: synchronized(this) {
                instance ?: LibraryDownloads(context).also { instance = it }
            }

        fun chapterWorkName(chapter: LibraryChapterRecord): String =
            "library-chapter:${chapter.ownerKey}:${chapter.chapterId}"
    }
}
