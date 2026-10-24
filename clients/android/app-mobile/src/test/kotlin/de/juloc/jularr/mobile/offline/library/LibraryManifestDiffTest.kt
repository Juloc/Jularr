package de.juloc.jularr.mobile.offline.library

import de.juloc.jularr.core.model.ClientOfflineLibraryChapterRef
import de.juloc.jularr.core.model.ClientOfflineLibraryManifest
import de.juloc.jularr.mobile.offline.DownloadState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class LibraryManifestDiffTest {
    private val ownerKey = "owner-a"
    private val workId = "work-1"

    private fun ref(chapterId: String, hash: String, number: Int = 1) = ClientOfflineLibraryChapterRef(
        chapterId = chapterId,
        volumeId = "volume-1",
        number = number,
        title = "Chapter $number",
        hash = hash,
        hasContent = true,
        hasTranslation = false,
    )

    private fun manifest(vararg chapters: ClientOfflineLibraryChapterRef) = ClientOfflineLibraryManifest(
        workId = workId,
        schemaVersion = 1,
        contentVersion = "v1",
        title = "Work",
        author = null,
        description = null,
        coverAssetUrl = null,
        issuedAtUtc = "2026-09-26T00:00:00Z",
        volumes = emptyList(),
        chapters = chapters.toList(),
    )

    private fun stored(chapterId: String, hash: String, verifiedHash: String? = hash, state: DownloadState = DownloadState.READY) =
        LibraryChapterRecord(
            ownerKey = ownerKey,
            workId = workId,
            chapterId = chapterId,
            volumeId = "volume-1",
            number = 1,
            title = "Chapter",
            hash = hash,
            verifiedHash = verifiedHash,
            state = state,
        )

    @Test
    fun newChaptersAreQueuedForDownload() {
        val diffs = LibraryManifestDiff.diff(emptyList(), manifest(ref("c1", "hash-a")), wants = { true })

        assertEquals(listOf(ManifestChapterDiff.Download(ref("c1", "hash-a"))), diffs)
    }

    @Test
    fun unchangedVerifiedChaptersAreKeptNotRedownloaded() {
        val storedChapter = stored("c1", "hash-a")
        val diffs = LibraryManifestDiff.diff(listOf(storedChapter), manifest(ref("c1", "hash-a")), wants = { true })

        assertEquals(listOf(ManifestChapterDiff.Keep(storedChapter)), diffs)
    }

    @Test
    fun changedHashIsQueuedForRedownloadRegardlessOfPriorState() {
        val storedChapter = stored("c1", "hash-a", state = DownloadState.FAILED)
        val diffs = LibraryManifestDiff.diff(listOf(storedChapter), manifest(ref("c1", "hash-b")), wants = { true })

        assertEquals(listOf(ManifestChapterDiff.Download(ref("c1", "hash-b"))), diffs)
    }

    @Test
    fun inProgressChapterWithSameHashIsLeftAlone() {
        val downloading = stored("c1", "hash-a", verifiedHash = null, state = DownloadState.DOWNLOADING)
        val diffs = LibraryManifestDiff.diff(listOf(downloading), manifest(ref("c1", "hash-a")), wants = { true })

        assertEquals(listOf(ManifestChapterDiff.Keep(downloading)), diffs)
    }

    @Test
    fun chapterRemovedFromManifestIsQueuedForRemoval() {
        val storedChapter = stored("c1", "hash-a")
        val diffs = LibraryManifestDiff.diff(listOf(storedChapter), manifest(), wants = { true })

        assertEquals(listOf(ManifestChapterDiff.Remove(storedChapter)), diffs)
    }

    @Test
    fun deselectedChapterIsQueuedForRemovalEvenIfStillInManifest() {
        val storedChapter = stored("c1", "hash-a")
        val diffs = LibraryManifestDiff.diff(listOf(storedChapter), manifest(ref("c1", "hash-a")), wants = { false })

        assertEquals(listOf(ManifestChapterDiff.Remove(storedChapter)), diffs)
    }

    @Test
    fun onlyWantedNewChaptersAreDownloaded() {
        val diffs = LibraryManifestDiff.diff(
            emptyList(),
            manifest(ref("c1", "hash-a"), ref("c2", "hash-b", number = 2)),
            wants = { chapterId -> chapterId == "c1" },
        )

        assertEquals(listOf(ManifestChapterDiff.Download(ref("c1", "hash-a"))), diffs)
    }

    @Test
    fun hasChangedComparesContentVersion() {
        val manifest = manifest(ref("c1", "hash-a"))
        assertTrue(LibraryManifestDiff.hasChanged(null, manifest))
        assertTrue(LibraryManifestDiff.hasChanged("v0", manifest))
        assertEquals(false, LibraryManifestDiff.hasChanged("v1", manifest))
    }
}
