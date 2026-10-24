package de.juloc.jularr.mobile.offline.library

import de.juloc.jularr.mobile.offline.DownloadState
import de.juloc.jularr.mobile.offline.OfflineAccount
import de.juloc.jularr.mobile.offline.OfflineOwner
import org.junit.Assert.assertEquals
import org.junit.Test

class LibraryBookStatusTest {
    private fun chapter(state: DownloadState, hash: String = "h", verifiedHash: String? = hash) =
        LibraryChapterRecord(
            ownerKey = "owner",
            workId = "work-1",
            chapterId = "chapter-${state.name}-$hash-$verifiedHash",
            volumeId = "volume-1",
            number = 1,
            title = "Chapter",
            hash = hash,
            verifiedHash = verifiedHash,
            state = state,
        )

    @Test
    fun emptyBookIsQueued() {
        assertEquals(BookDownloadStatus.QUEUED, LibraryBookStatus.of(emptyList()))
    }

    @Test
    fun allVerifiedChaptersMeansAvailable() {
        val chapters = listOf(chapter(DownloadState.READY), chapter(DownloadState.READY))
        assertEquals(BookDownloadStatus.AVAILABLE, LibraryBookStatus.of(chapters))
    }

    @Test
    fun anyDownloadingChapterMeansDownloading() {
        val chapters = listOf(chapter(DownloadState.READY), chapter(DownloadState.DOWNLOADING))
        assertEquals(BookDownloadStatus.DOWNLOADING, LibraryBookStatus.of(chapters))
    }

    @Test
    fun aFailedChapterWithNoActiveTransferMeansFailed() {
        val chapters = listOf(chapter(DownloadState.READY), chapter(DownloadState.FAILED))
        assertEquals(BookDownloadStatus.FAILED, LibraryBookStatus.of(chapters))
    }

    @Test
    fun readyButStaleHashMeansUpdateAvailable() {
        val chapters = listOf(chapter(DownloadState.READY, hash = "new", verifiedHash = "old"))
        assertEquals(BookDownloadStatus.UPDATE_AVAILABLE, LibraryBookStatus.of(chapters))
    }

    @Test
    fun pausedTakesPriorityOverQueuedWhenNoTransferIsActive() {
        val chapters = listOf(chapter(DownloadState.QUEUED), chapter(DownloadState.PAUSED))
        assertEquals(BookDownloadStatus.PAUSED, LibraryBookStatus.of(chapters))
    }
}

class LibrarySnapshotIsolationTest {
    private val origin = "https://jularr.example"

    private fun book(ownerKey: String, workId: String = "work-1") = LibraryBookRecord(
        ownerKey = ownerKey,
        workId = workId,
        title = "Book",
        author = null,
        coverAssetUrl = null,
        contentVersion = "v1",
    )

    @Test
    fun accessibleBooksRequireTheSignedInOwner() {
        val account = OfflineAccount(origin, "profile-a", signedIn = true)
        val mine = book(OfflineOwner.key(origin, "profile-a"))
        val other = book(OfflineOwner.key(origin, "profile-b"), workId = "work-2")
        val snapshot = LibrarySnapshot(account = account, books = listOf(mine, other))

        assertEquals(listOf(mine), snapshot.accessibleBooks(origin))
        assertEquals(emptyList<LibraryBookRecord>(), snapshot.accessibleBooks("https://other.example"))
        assertEquals(
            emptyList<LibraryBookRecord>(),
            snapshot.copy(account = account.copy(signedIn = false)).accessibleBooks(origin),
        )
    }

    @Test
    fun chaptersOfIsScopedToOwnerAndWork() {
        val ownerA = OfflineOwner.key(origin, "profile-a")
        val ownerB = OfflineOwner.key(origin, "profile-b")
        val mine = LibraryChapterRecord(ownerA, "work-1", "c1", "v1", 1, "Chapter", "hash")
        val otherWork = LibraryChapterRecord(ownerA, "work-2", "c2", "v1", 1, "Chapter", "hash")
        val otherOwner = LibraryChapterRecord(ownerB, "work-1", "c3", "v1", 1, "Chapter", "hash")
        val snapshot = LibrarySnapshot(chapters = listOf(mine, otherWork, otherOwner))

        assertEquals(listOf(mine), snapshot.chaptersOf(ownerA, "work-1"))
    }
}
