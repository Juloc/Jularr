package de.juloc.jularr.mobile.offline.library

import de.juloc.jularr.mobile.offline.DownloadState
import de.juloc.jularr.mobile.offline.OfflineAccount
import org.junit.Assert.assertEquals
import org.junit.Test

class LibraryStateCodecTest {
    @Test
    fun roundTripsBooksChaptersSettingsAndQueues() {
        val ownerKey = "owner-a"
        val book = LibraryBookRecord(
            ownerKey = ownerKey,
            workId = "work-1",
            title = "My Book",
            author = "An Author",
            coverAssetUrl = "/api/client/v1/offline-library/assets/vol/abc.jpg",
            contentVersion = "v1",
            selectedChapterIds = setOf("chapter-1"),
            wholeBook = false,
            assetFileNames = setOf("abc.jpg"),
            createdAtMs = 123,
        )
        val chapter = LibraryChapterRecord(
            ownerKey = ownerKey,
            workId = "work-1",
            chapterId = "chapter-1",
            volumeId = "volume-1",
            number = 1,
            title = "Chapter One",
            hash = "hash-a",
            verifiedHash = "hash-a",
            state = DownloadState.READY,
            failure = null,
            createdAtMs = 42,
        )
        val progress = LibraryPendingProgress(
            workId = "work-1",
            chapterId = "chapter-1",
            positionPermille = 500,
            anchorLanguage = "ja",
            anchorParagraphIndex = 3,
            anchorOffset = 12,
            clientEventId = "event-1",
            clientTimestampUtc = "2026-09-26T00:00:00Z",
        )
        val bookmark = LibraryPendingBookmark(
            bookmarkId = "bookmark-1",
            type = "upsert",
            workId = "work-1",
            chapterId = "chapter-1",
            language = "ja",
            positionPermille = 250,
            paragraphIndex = 1,
            characterOffset = 5,
            anchorText = "text",
            label = "label",
            style = "highlight",
            color = "#ff0000",
            clientEventId = "event-2",
            clientTimestampUtc = "2026-09-26T00:01:00Z",
        )

        val snapshot = LibrarySnapshot(
            account = OfflineAccount("https://jularr.example", "profile-a", signedIn = true),
            settings = LibrarySettings(wifiOnly = false),
            books = listOf(book),
            chapters = listOf(chapter),
            pendingProgress = mapOf(ownerKey to listOf(progress)),
            pendingBookmarks = mapOf(ownerKey to listOf(bookmark)),
        )

        assertEquals(snapshot, LibraryStateCodec.decode(LibraryStateCodec.encode(snapshot)))
    }

    @Test
    fun wholeBookHasNoSelection() {
        val book = LibraryBookRecord(
            ownerKey = "owner-a",
            workId = "work-1",
            title = "Book",
            author = null,
            coverAssetUrl = null,
            contentVersion = "v1",
        )
        val snapshot = LibrarySnapshot(books = listOf(book))

        val decoded = LibraryStateCodec.decode(LibraryStateCodec.encode(snapshot))
        assertEquals(null, decoded.books.single().selectedChapterIds)
        assertEquals(true, decoded.books.single().wholeBook)
    }

    @Test
    fun unknownVersionStartsEmptyInsteadOfGuessing() {
        assertEquals(LibrarySnapshot(), LibraryStateCodec.decode("""{"version":99,"books":[]}"""))
    }
}
