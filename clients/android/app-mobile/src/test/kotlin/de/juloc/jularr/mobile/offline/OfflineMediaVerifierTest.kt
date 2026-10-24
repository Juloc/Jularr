package de.juloc.jularr.mobile.offline

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Test
import java.io.File

class OfflineMediaVerifierTest {
    private val files = mutableListOf<File>()

    @After
    fun cleanUp() {
        files.forEach { it.delete() }
    }

    // Same vectors as the server test OfflinePlaybackTests.FingerprintMatchesTheSharedClientVectors,
    // so client and server agree on the media identity.
    @Test
    fun fingerprintMatchesTheServerVectors() {
        assertEquals(
            "9bb6b1984c6fa246dd692298ea3af4ec7d98884b4a7efd50094a63ba81cfd47e",
            OfflineMediaVerifier.fingerprint(patternFile(1_000)),
        )
        assertEquals(
            "3b940a43c416bd37a9ca45d5f93b1c1dc20ce046fcca79a0dbd5389b5d7ed4a7",
            OfflineMediaVerifier.fingerprint(patternFile(100_000)),
        )
        assertEquals(
            "0380b2aa8688f6914fe14c854b25a82175708fd91a37f577b43ab5feb001a321",
            OfflineMediaVerifier.fingerprint(patternFile(200_000)),
        )
    }

    @Test
    fun verifiedOnlyWhenSizeAndFingerprintMatch() {
        val file = patternFile(200_000)
        val fingerprint = "0380b2aa8688f6914fe14c854b25a82175708fd91a37f577b43ab5feb001a321"

        assertEquals(
            VerificationResult.Verified,
            OfflineMediaVerifier.verify(file, 200_000, fingerprint, OfflineMediaVerifier.FingerprintAlgorithm),
        )
        assertEquals(
            VerificationResult.SizeMismatch(200_001, 200_000),
            OfflineMediaVerifier.verify(file, 200_001, fingerprint, OfflineMediaVerifier.FingerprintAlgorithm),
        )
        assertEquals(
            VerificationResult.UnsupportedAlgorithm("md5"),
            OfflineMediaVerifier.verify(file, 200_000, fingerprint, "md5"),
        )
    }

    @Test
    fun corruptedTailIsDetected() {
        val file = patternFile(200_000)
        val bytes = file.readBytes()
        bytes[bytes.size - 10] = (bytes[bytes.size - 10] + 1).toByte()
        file.writeBytes(bytes)

        assertEquals(
            VerificationResult.FingerprintMismatch,
            OfflineMediaVerifier.verify(
                file,
                200_000,
                "0380b2aa8688f6914fe14c854b25a82175708fd91a37f577b43ab5feb001a321",
                OfflineMediaVerifier.FingerprintAlgorithm,
            ),
        )
    }

    @Test
    fun missingFileIsASizeMismatch() {
        val missing = File.createTempFile("jularr-missing", ".bin").also { it.delete() }

        assertEquals(
            VerificationResult.SizeMismatch(10, -1),
            OfflineMediaVerifier.verify(missing, 10, "x", OfflineMediaVerifier.FingerprintAlgorithm),
        )
    }

    private fun patternFile(length: Int): File =
        File.createTempFile("jularr-offline", ".bin").also { file ->
            file.writeBytes(pattern(length))
            files += file
        }
}

internal fun pattern(length: Int): ByteArray =
    ByteArray(length) { index -> ((index * 31 + 7) % 256).toByte() }
