package de.juloc.jularr.mobile.offline.library

import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.nio.file.Files

class LibraryContentIoTest {
    private val directory = Files.createTempDirectory("jularr-library").toFile()

    @After
    fun cleanUp() {
        directory.deleteRecursively()
    }

    @Test
    fun writesAndFinalizesAVerifiedFile() {
        val target = File(directory, "chapter.json")
        val bytes = "{\"hash\":\"abc\"}".toByteArray(Charsets.UTF_8)

        assertTrue(LibraryContentIo.writeVerified(target, bytes))
        assertTrue(target.isFile)
        assertArrayEquals(bytes, target.readBytes())
        assertFalse(File(directory, "chapter.json.part").exists())
    }

    @Test
    fun replacesAnExistingFileAtomically() {
        val target = File(directory, "chapter.json")
        target.writeBytes("old".toByteArray())

        val newBytes = "new-content".toByteArray()
        assertTrue(LibraryContentIo.writeVerified(target, newBytes))
        assertArrayEquals(newBytes, target.readBytes())
    }

    @Test
    fun neverLeavesAPartialFileBehindOnSuccess() {
        val target = File(File(directory, "nested"), "chapter.json")
        assertTrue(LibraryContentIo.writeVerified(target, "content".toByteArray()))
        assertTrue(target.isFile)
    }

    @Test
    fun aFailedWriteDoesNotTouchAnExistingFile() {
        val target = File(directory, "chapter.json")
        val original = "original".toByteArray()
        target.writeBytes(original)

        // Occupy the "<file>.part" path with a directory so the write itself fails cleanly.
        File(directory, "chapter.json.part").mkdirs()

        assertFalse(LibraryContentIo.writeVerified(target, "new".toByteArray()))
        assertArrayEquals(original, target.readBytes())
    }
}
