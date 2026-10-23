package de.juloc.jularr.core.update

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

sealed interface DownloadOutcome {
    data class Success(val file: File) : DownloadOutcome
    data class HttpError(val statusCode: Int) : DownloadOutcome
    data class NetworkError(val message: String) : DownloadOutcome
}

interface UpdateDownloader {
    /** Streams [url] to [destination], reporting bytes read so far and the total (when known). */
    suspend fun download(
        url: String,
        destination: File,
        onProgress: (bytesRead: Long, totalBytes: Long) -> Unit = { _, _ -> },
    ): DownloadOutcome

    /** Fetches a small text asset (a `.sha256` file) fully into memory. */
    suspend fun downloadText(url: String): Result<String>
}

/**
 * Plain HTTPS download, no credentials attached: [url] always comes from
 * [UpdateAssetResolver] against a GitHub release of the configured Jularr repository,
 * never from arbitrary UI/user input.
 */
class HttpUpdateDownloader : UpdateDownloader {

    override suspend fun download(
        url: String,
        destination: File,
        onProgress: (bytesRead: Long, totalBytes: Long) -> Unit,
    ): DownloadOutcome = withContext(Dispatchers.IO) {
        val connection = openConnection(url)
        try {
            val status = connection.responseCode
            if (status !in 200..299) {
                return@withContext DownloadOutcome.HttpError(status)
            }

            val total = connection.contentLengthLong
            destination.parentFile?.mkdirs()
            val tempFile = File(destination.parentFile, "${destination.name}.part")

            connection.inputStream.use { input ->
                tempFile.outputStream().use { output ->
                    val buffer = ByteArray(8192)
                    var readTotal = 0L
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        output.write(buffer, 0, read)
                        readTotal += read
                        onProgress(readTotal, total)
                    }
                }
            }

            destination.delete()
            if (!tempFile.renameTo(destination)) {
                tempFile.delete()
                return@withContext DownloadOutcome.NetworkError(
                    "The download could not be saved on this device.",
                )
            }

            DownloadOutcome.Success(destination)
        } catch (exception: IOException) {
            DownloadOutcome.NetworkError(exception.message ?: "The download could not be completed.")
        } finally {
            connection.disconnect()
        }
    }

    override suspend fun downloadText(url: String): Result<String> = withContext(Dispatchers.IO) {
        val connection = openConnection(url)
        try {
            val status = connection.responseCode
            if (status !in 200..299) {
                return@withContext Result.failure(IOException("HTTP $status"))
            }
            Result.success(connection.inputStream.bufferedReader(Charsets.UTF_8).use { it.readText() })
        } catch (exception: IOException) {
            Result.failure(exception)
        } finally {
            connection.disconnect()
        }
    }

    private fun openConnection(url: String): HttpURLConnection =
        (URL(url).openConnection() as HttpURLConnection).apply {
            requestMethod = "GET"
            connectTimeout = 15_000
            readTimeout = 30_000
            instanceFollowRedirects = true
            useCaches = false
        }
}
