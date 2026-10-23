package de.juloc.jularr.core.update

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class Sha256Test {

    @get:Rule
    val tempFolder = TemporaryFolder()

    private fun fileWithContents(contents: String): File =
        tempFolder.newFile().apply { writeText(contents, Charsets.UTF_8) }

    @Test
    fun computesKnownDigestOfEmptyInput() {
        val file = fileWithContents("")

        // The well-known SHA-256 digest of the empty string.
        assertEquals(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            Sha256.hex(file),
        )
    }

    @Test
    fun computesKnownDigestOfAbc() {
        val file = fileWithContents("abc")

        assertEquals(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            Sha256.hex(file),
        )
    }

    @Test
    fun parsesSha256sumStyleChecksumFile() {
        val expected = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"

        assertEquals(expected, Sha256.parseExpectedHex("$expected  Jularr-Mobile-1.0.0.apk\n"))
    }

    @Test
    fun parsesBareHexChecksum() {
        val expected = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"

        assertEquals(expected, Sha256.parseExpectedHex(expected))
    }

    @Test
    fun parseExpectedHexReturnsNullForGarbage() {
        assertNull(Sha256.parseExpectedHex(""))
        assertNull(Sha256.parseExpectedHex("not a checksum file"))
        assertNull(Sha256.parseExpectedHex("deadbeef"))
    }

    @Test
    fun matchesReturnsTrueForACorrectChecksum() {
        val file = fileWithContents("abc")
        val checksumFile =
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  Jularr-Mobile-1.0.0.apk"

        assertTrue(Sha256.matches(file, checksumFile))
    }

    @Test
    fun matchesReturnsFalseForATamperedFile() {
        val file = fileWithContents("tampered contents")
        val checksumFile =
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  Jularr-Mobile-1.0.0.apk"

        assertFalse(Sha256.matches(file, checksumFile))
    }

    @Test
    fun matchesReturnsFalseWhenChecksumFileIsUnparsable() {
        val file = fileWithContents("abc")

        assertFalse(Sha256.matches(file, "not a checksum file"))
    }
}
