package de.juloc.jularr.core.update

import java.io.File
import java.io.InputStream
import java.security.MessageDigest

/** SHA-256 verification of a downloaded APK against its release `.sha256` sibling. */
object Sha256 {
    private const val BufferSize = 8192
    private const val HexDigestLength = 64

    fun hex(file: File): String = file.inputStream().use { hex(it) }

    fun hex(stream: InputStream): String {
        val digest = MessageDigest.getInstance("SHA-256")
        val buffer = ByteArray(BufferSize)
        while (true) {
            val read = stream.read(buffer)
            if (read < 0) break
            digest.update(buffer, 0, read)
        }
        return digest.digest().joinToString("") { byte -> "%02x".format(byte) }
    }

    /**
     * Parses a `sha256sum`-style checksum file — `"<hex>  Jularr-Mobile-1.0.0.apk"` (the
     * exact format the release workflow's `sha256sum` produces) or a bare hex digest.
     * Returns `null` if no 64-character hex digest can be found.
     */
    fun parseExpectedHex(checksumFileContents: String): String? {
        val firstToken = checksumFileContents.trim().split(Regex("\\s+")).firstOrNull() ?: return null
        val isHex = firstToken.length == HexDigestLength && firstToken.all { it.isHexDigit() }
        return firstToken.takeIf { isHex }?.lowercase()
    }

    /** Computes [file]'s digest and compares it against the parsed expected value. */
    fun matches(file: File, checksumFileContents: String): Boolean {
        val expected = parseExpectedHex(checksumFileContents) ?: return false
        return hex(file).equals(expected, ignoreCase = true)
    }

    private fun Char.isHexDigit(): Boolean =
        this in '0'..'9' || this in 'a'..'f' || this in 'A'..'F'
}
