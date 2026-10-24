package de.juloc.jularr.mobile.offline

import java.io.File
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest

sealed interface VerificationResult {
    data object Verified : VerificationResult
    data class SizeMismatch(val expected: Long, val actual: Long) : VerificationResult
    data object FingerprintMismatch : VerificationResult
    data class UnsupportedAlgorithm(val algorithm: String) : VerificationResult
}

/**
 * Verifies a downloaded file against the server identity before it is marked
 * ready. Uses the same bounded fingerprint as the server media inventory:
 * SHA-256 over the little-endian length, the first 64 KiB and the last 64 KiB.
 */
object OfflineMediaVerifier {
    const val FingerprintAlgorithm = "sha256-length-head-tail-64k"
    private const val ChunkBytes = 64 * 1024

    fun verify(
        file: File,
        expectedSizeBytes: Long,
        expectedFingerprint: String,
        algorithm: String,
    ): VerificationResult {
        if (algorithm != FingerprintAlgorithm) {
            return VerificationResult.UnsupportedAlgorithm(algorithm)
        }

        val actualSize = if (file.exists()) file.length() else -1L
        if (actualSize != expectedSizeBytes) {
            return VerificationResult.SizeMismatch(expectedSizeBytes, actualSize)
        }

        return if (fingerprint(file).equals(expectedFingerprint, ignoreCase = true)) {
            VerificationResult.Verified
        } else {
            VerificationResult.FingerprintMismatch
        }
    }

    fun fingerprint(file: File): String {
        RandomAccessFile(file, "r").use { input ->
            val length = input.length()
            val digest = MessageDigest.getInstance("SHA-256")
            digest.update(
                ByteBuffer.allocate(Long.SIZE_BYTES)
                    .order(ByteOrder.LITTLE_ENDIAN)
                    .putLong(length)
                    .array(),
            )

            val buffer = ByteArray(ChunkBytes)
            appendRange(input, digest, buffer, 0, length)
            if (length > ChunkBytes) {
                val tailOffset = maxOf(ChunkBytes.toLong(), length - ChunkBytes)
                appendRange(input, digest, buffer, tailOffset, length)
            }

            return digest.digest().joinToString("") { "%02x".format(it) }
        }
    }

    private fun appendRange(
        input: RandomAccessFile,
        digest: MessageDigest,
        buffer: ByteArray,
        offset: Long,
        length: Long,
    ) {
        val remaining = minOf(buffer.size.toLong(), length - offset).toInt()
        input.seek(offset)
        var filled = 0
        while (filled < remaining) {
            val read = input.read(buffer, filled, remaining - filled)
            if (read <= 0) {
                break
            }
            filled += read
        }
        digest.update(buffer, 0, filled)
    }
}
