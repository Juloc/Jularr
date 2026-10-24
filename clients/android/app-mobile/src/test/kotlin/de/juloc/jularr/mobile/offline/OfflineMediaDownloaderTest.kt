package de.juloc.jularr.mobile.offline

import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.File
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.nio.file.Files

class OfflineMediaDownloaderTest {
    private val eTag = "\"7d0-abc\""
    private val full = pattern(2_000)
    private val directory = Files.createTempDirectory("jularr-download").toFile()
    private val target = File(directory, "media.part")
    private val url = URL("https://jularr.example/api/client/v1/offline/media/m/content")

    @After
    fun cleanUp() {
        directory.deleteRecursively()
    }

    @Test
    fun freshDownloadWritesTheWholeFile() {
        val connection = FakeConnection(url, 200, mapOf("ETag" to eTag), full)

        val outcome = downloader(connection).transfer(url, emptyMap(), target, 2_000, eTag, { true }, {})

        assertEquals(TransferOutcome.Complete, outcome)
        assertArrayEquals(full, target.readBytes())
        assertNull(connection.sentRange)
    }

    @Test
    fun resumeAppendsFromThePartialLengthWithIfRange() {
        target.writeBytes(full.copyOfRange(0, 1_200))
        val connection = FakeConnection(
            url,
            206,
            mapOf("ETag" to eTag, "Content-Range" to "bytes 1200-1999/2000"),
            full.copyOfRange(1_200, 2_000),
        )

        val outcome = downloader(connection).transfer(url, mapOf("Cookie" to "a=b"), target, 2_000, eTag, { true }, {})

        assertEquals(TransferOutcome.Complete, outcome)
        assertArrayEquals(full, target.readBytes())
        assertEquals("bytes=1200-", connection.sentRange)
        assertEquals(eTag, connection.sentIfRange)
        assertEquals("a=b", connection.sentCookie)
    }

    @Test
    fun fullResponseForTheSameVersionRestartsFromZero() {
        target.writeBytes(ByteArray(500) { 1 })
        val connection = FakeConnection(url, 200, mapOf("ETag" to eTag), full)

        val outcome = downloader(connection).transfer(url, emptyMap(), target, 2_000, eTag, { true }, {})

        assertEquals(TransferOutcome.Complete, outcome)
        assertArrayEquals(full, target.readBytes())
    }

    @Test
    fun changedServerFileIsNeverSpliced() {
        target.writeBytes(full.copyOfRange(0, 1_000))
        val connection = FakeConnection(url, 200, mapOf("ETag" to "\"other\""), pattern(3_000))

        val outcome = downloader(connection).transfer(url, emptyMap(), target, 2_000, eTag, { true }, {})

        assertEquals(TransferOutcome.SourceChanged, outcome)
        assertEquals(1_000, target.length())
    }

    @Test
    fun stoppingKeepsThePartialFileForResume() {
        val connection = FakeConnection(url, 200, mapOf("ETag" to eTag), full)

        val outcome = downloader(connection).transfer(url, emptyMap(), target, 2_000, eTag, { false }, {})

        assertEquals(TransferOutcome.Stopped, outcome)
        assertTrue(target.exists())
    }

    @Test
    fun earlyEndOfStreamIsRetried() {
        val connection = FakeConnection(url, 200, mapOf("ETag" to eTag), full.copyOfRange(0, 1_500))

        val outcome = downloader(connection).transfer(url, emptyMap(), target, 2_000, eTag, { true }, {})

        assertTrue(outcome is TransferOutcome.Retry)
        assertEquals(1_500, target.length())
    }

    @Test
    fun deviceSafetyMarginStopsTheTransfer() {
        val connection = FakeConnection(url, 200, mapOf("ETag" to eTag), full)
        val downloader = OfflineMediaDownloader(
            openConnection = { connection },
            freeBytes = { OfflineStoragePolicy.DeviceSafetyMarginBytes },
        )

        assertEquals(
            TransferOutcome.DeviceFull,
            downloader.transfer(url, emptyMap(), target, 2_000, eTag, { true }, {}),
        )
    }

    @Test
    fun serverAndAuthFailuresMapToRetryOrSignedOut() {
        assertTrue(
            downloader(FakeConnection(url, 503, emptyMap(), ByteArray(0)))
                .transfer(url, emptyMap(), target, 2_000, eTag, { true }, {}) is TransferOutcome.Retry,
        )
        assertEquals(
            TransferOutcome.SignedOut,
            downloader(FakeConnection(url, 401, emptyMap(), ByteArray(0)))
                .transfer(url, emptyMap(), target, 2_000, eTag, { true }, {}),
        )
    }

    @Test
    fun alreadyCompletePartialNeedsNoRequest() {
        target.writeBytes(full)
        var opened = false
        val downloader = OfflineMediaDownloader(openConnection = {
            opened = true
            FakeConnection(url, 500, emptyMap(), ByteArray(0))
        })

        assertEquals(
            TransferOutcome.Complete,
            downloader.transfer(url, emptyMap(), target, 2_000, eTag, { true }, {}),
        )
        assertEquals(false, opened)
    }

    private fun downloader(connection: FakeConnection) =
        OfflineMediaDownloader(openConnection = { connection }, freeBytes = { Long.MAX_VALUE / 2 })

    private class FakeConnection(
        url: URL,
        private val status: Int,
        private val headers: Map<String, String>,
        private val body: ByteArray,
    ) : HttpURLConnection(url) {
        var sentRange: String? = null
        var sentIfRange: String? = null
        var sentCookie: String? = null

        override fun setRequestProperty(key: String, value: String?) {
            when (key) {
                "Range" -> sentRange = value
                "If-Range" -> sentIfRange = value
                "Cookie" -> sentCookie = value
            }
        }

        override fun connect() = Unit
        override fun disconnect() = Unit
        override fun usingProxy(): Boolean = false
        override fun getResponseCode(): Int = status
        override fun getHeaderField(name: String?): String? =
            headers.entries.firstOrNull { it.key.equals(name, ignoreCase = true) }?.value
        override fun getInputStream(): InputStream = ByteArrayInputStream(body)
    }
}
