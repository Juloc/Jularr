package de.juloc.jularr.mobile.offline

import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL

enum class RangeDecision {
    /** 206 for the requested offset and the expected file version: append. */
    APPEND,

    /** Full 200 response for the expected version: rewrite from byte zero. */
    RESTART,

    /** The requested offset already covers the whole file. */
    ALREADY_COMPLETE,

    /** The server file changed since the descriptor was issued. */
    SOURCE_CHANGED,

    SIGNED_OUT,

    /** Transient server/storage condition (NAS asleep, 5xx, rate limit). */
    RETRY_LATER,

    MISSING,

    FAILED,
}

/**
 * Pure interpretation of a resumed `GET` with `Range` + `If-Range`. Bytes of
 * two different file versions are never spliced together: every response must
 * carry the descriptor's strong ETag.
 */
object RangeResponsePolicy {
    fun decide(
        requestedOffset: Long,
        expectedSizeBytes: Long,
        expectedETag: String,
        status: Int,
        eTag: String?,
        contentRangeStart: Long?,
    ): RangeDecision {
        val sameVersion = eTag != null && normalize(eTag) == normalize(expectedETag)

        return when (status) {
            HttpURLConnection.HTTP_PARTIAL -> when {
                !sameVersion -> RangeDecision.SOURCE_CHANGED
                contentRangeStart != requestedOffset -> RangeDecision.FAILED
                else -> RangeDecision.APPEND
            }

            HttpURLConnection.HTTP_OK -> if (sameVersion) RangeDecision.RESTART else RangeDecision.SOURCE_CHANGED

            416 -> if (requestedOffset == expectedSizeBytes) {
                RangeDecision.ALREADY_COMPLETE
            } else {
                RangeDecision.SOURCE_CHANGED
            }

            HttpURLConnection.HTTP_UNAUTHORIZED,
            HttpURLConnection.HTTP_FORBIDDEN,
            -> RangeDecision.SIGNED_OUT

            HttpURLConnection.HTTP_NOT_FOUND -> RangeDecision.MISSING

            429,
            HttpURLConnection.HTTP_INTERNAL_ERROR,
            HttpURLConnection.HTTP_BAD_GATEWAY,
            HttpURLConnection.HTTP_UNAVAILABLE,
            HttpURLConnection.HTTP_GATEWAY_TIMEOUT,
            -> RangeDecision.RETRY_LATER

            else -> RangeDecision.FAILED
        }
    }

    /** Parses the first byte position of `Content-Range: bytes 100-199/200`. */
    fun contentRangeStart(header: String?): Long? =
        header
            ?.trim()
            ?.removePrefix("bytes")
            ?.trim()
            ?.substringBefore('-')
            ?.toLongOrNull()

    private fun normalize(eTag: String): String =
        eTag.trim().removePrefix("W/")
}

sealed interface TransferOutcome {
    /** The target file now holds exactly the expected number of bytes. */
    data object Complete : TransferOutcome

    /** The caller asked to stop (pause, cancel, worker stopped). */
    data object Stopped : TransferOutcome

    data class Retry(val reason: String) : TransferOutcome
    data object SourceChanged : TransferOutcome
    data object SignedOut : TransferOutcome
    data object Missing : TransferOutcome
    data object DeviceFull : TransferOutcome
    data class Failed(val reason: String) : TransferOutcome
}

/**
 * Resumable HTTP transfer of one media file into an app-private partial file.
 * The partial file length is the only resume state, so a transfer survives
 * process death and device restarts.
 */
class OfflineMediaDownloader(
    private val openConnection: (URL) -> HttpURLConnection = { it.openConnection() as HttpURLConnection },
    private val freeBytes: (File) -> Long = { it.usableSpace },
) {
    fun transfer(
        url: URL,
        headers: Map<String, String>,
        target: File,
        expectedSizeBytes: Long,
        expectedETag: String,
        shouldContinue: () -> Boolean,
        onProgress: (Long) -> Unit,
    ): TransferOutcome {
        target.parentFile?.mkdirs()
        if (target.exists() && target.length() > expectedSizeBytes) {
            target.delete()
        }

        val offset = if (target.exists()) target.length() else 0L
        if (offset == expectedSizeBytes && expectedSizeBytes > 0) {
            return TransferOutcome.Complete
        }

        val connection = openConnection(url).apply {
            requestMethod = "GET"
            connectTimeout = 15_000
            readTimeout = 30_000
            instanceFollowRedirects = false
            useCaches = false
            setRequestProperty("Accept-Encoding", "identity")
            setRequestProperty("X-Jularr-Client", "android")
            headers.forEach { (name, value) ->
                if (name.isNotBlank() && value.isNotBlank()) {
                    setRequestProperty(name, value)
                }
            }
            if (offset > 0) {
                setRequestProperty("Range", "bytes=$offset-")
                setRequestProperty("If-Range", expectedETag)
            }
        }

        try {
            val status = connection.responseCode
            val decision = RangeResponsePolicy.decide(
                requestedOffset = offset,
                expectedSizeBytes = expectedSizeBytes,
                expectedETag = expectedETag,
                status = status,
                eTag = connection.getHeaderField("ETag"),
                contentRangeStart = RangeResponsePolicy.contentRangeStart(
                    connection.getHeaderField("Content-Range"),
                ),
            )

            return when (decision) {
                RangeDecision.APPEND -> copy(connection.inputStream, target, append = true, offset, expectedSizeBytes, shouldContinue, onProgress)
                RangeDecision.RESTART -> copy(connection.inputStream, target, append = false, 0L, expectedSizeBytes, shouldContinue, onProgress)
                RangeDecision.ALREADY_COMPLETE -> TransferOutcome.Complete
                RangeDecision.SOURCE_CHANGED -> TransferOutcome.SourceChanged
                RangeDecision.SIGNED_OUT -> TransferOutcome.SignedOut
                RangeDecision.RETRY_LATER -> TransferOutcome.Retry("The Jularr media storage is not available right now.")
                RangeDecision.MISSING -> TransferOutcome.Missing
                RangeDecision.FAILED -> TransferOutcome.Failed("The server answered the download with HTTP $status.")
            }
        } catch (exception: IOException) {
            return TransferOutcome.Retry(exception.message ?: "The connection to Jularr was interrupted.")
        } finally {
            connection.disconnect()
        }
    }

    private fun copy(
        input: InputStream,
        target: File,
        append: Boolean,
        startOffset: Long,
        expectedSizeBytes: Long,
        shouldContinue: () -> Boolean,
        onProgress: (Long) -> Unit,
    ): TransferOutcome {
        var written = startOffset
        val buffer = ByteArray(BufferBytes)

        input.use { source ->
            FileOutputStream(target, append).use { output ->
                while (true) {
                    if (!shouldContinue()) {
                        output.fd.sync()
                        return TransferOutcome.Stopped
                    }

                    val read = source.read(buffer)
                    if (read < 0) {
                        break
                    }

                    if (written + read > expectedSizeBytes) {
                        return TransferOutcome.Failed("The server sent more data than the announced file size.")
                    }

                    if (!OfflineStoragePolicy.hasRoomFor(read.toLong(), freeBytes(target.parentFile ?: target))) {
                        output.fd.sync()
                        return TransferOutcome.DeviceFull
                    }

                    output.write(buffer, 0, read)
                    written += read
                    onProgress(written)
                }
                output.fd.sync()
            }
        }

        return if (written == expectedSizeBytes) {
            TransferOutcome.Complete
        } else {
            TransferOutcome.Retry("The connection closed before the download finished.")
        }
    }

    private companion object {
        const val BufferBytes = 256 * 1024
    }
}
