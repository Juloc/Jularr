package de.juloc.jularr.mobile.offline.library

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class LibraryRequestInterceptionTest {
    private val workId = "3fa85f64-5717-4562-b3fc-2c963f66afa6"
    private val chapterId = "3fa85f64-5717-4562-b3fc-2c963f66afa7"
    private val volumeId = "3fa85f64-5717-4562-b3fc-2c963f66afa8"
    private val asset = "0123456789abcdef0123456789abcdef.jpg"

    @Test
    fun matchesTheManifestPath() {
        val target = LibraryRequestInterception.match("/api/client/v1/offline-library/works/$workId/manifest")
        assertEquals(LibraryInterceptTarget.Manifest(workId), target)
    }

    @Test
    fun matchesTheChapterPath() {
        val target = LibraryRequestInterception.match("/api/client/v1/offline-library/chapters/$chapterId")
        assertEquals(LibraryInterceptTarget.Chapter(chapterId), target)
    }

    @Test
    fun matchesTheAssetPath() {
        val target = LibraryRequestInterception.match("/api/client/v1/offline-library/assets/$volumeId/$asset")
        assertEquals(LibraryInterceptTarget.Asset(volumeId, asset), target)
    }

    @Test
    fun rejectsAssetNamesOutsideTheContentAddressedPattern() {
        assertNull(
            LibraryRequestInterception.match("/api/client/v1/offline-library/assets/$volumeId/../../etc/passwd"),
        )
        assertNull(
            LibraryRequestInterception.match("/api/client/v1/offline-library/assets/$volumeId/not-a-hash.jpg"),
        )
        assertNull(
            LibraryRequestInterception.match("/api/client/v1/offline-library/assets/$volumeId/$asset.exe"),
        )
    }

    @Test
    fun doesNotMatchUnrelatedPaths() {
        assertNull(LibraryRequestInterception.match("/api/client/v1/offline-library/works/$workId/sync"))
        assertNull(LibraryRequestInterception.match("/Novels/Read/$chapterId"))
        assertNull(LibraryRequestInterception.match("/api/client/v1/offline-library/sync"))
    }

    @Test
    fun rejectsMalformedIds() {
        assertNull(LibraryRequestInterception.match("/api/client/v1/offline-library/works/not-a-uuid/manifest"))
    }

    @Test
    fun contentTypeIsDerivedFromTheExtension() {
        assertEquals("image/jpeg", LibraryRequestInterception.contentTypeFor("abc.jpg"))
        assertEquals("image/png", LibraryRequestInterception.contentTypeFor("abc.png"))
        assertEquals("image/webp", LibraryRequestInterception.contentTypeFor("abc.webp"))
        assertEquals("application/octet-stream", LibraryRequestInterception.contentTypeFor("abc.bin"))
    }
}

class LibraryWebPagesTest {
    private val workId = "3fa85f64-5717-4562-b3fc-2c963f66afa6"

    @Test
    fun detectsTheNovelWorkPage() {
        assertEquals(workId, LibraryWebPages.workId("/Novels/Work/$workId"))
    }

    @Test
    fun detectsTheBooksLibraryDetailPage() {
        assertEquals(workId, LibraryWebPages.workId("/Books/Library/$workId"))
    }

    @Test
    fun doesNotMatchOtherPages() {
        assertNull(LibraryWebPages.workId("/Novels/Read/$workId"))
        assertNull(LibraryWebPages.workId("/Books/Library"))
    }
}
