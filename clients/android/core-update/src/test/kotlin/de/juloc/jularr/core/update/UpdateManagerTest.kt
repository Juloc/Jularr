package de.juloc.jularr.core.update

import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

private class FakeUpdateDownloader(
    private val fileContents: String = "abc",
    private val checksumText: Result<String>? = null,
    private val outcome: DownloadOutcome? = null,
) : UpdateDownloader {
    override suspend fun download(
        url: String,
        destination: File,
        onProgress: (bytesRead: Long, totalBytes: Long) -> Unit,
    ): DownloadOutcome {
        if (outcome != null) return outcome
        destination.parentFile?.mkdirs()
        destination.writeText(fileContents, Charsets.UTF_8)
        onProgress(fileContents.length.toLong(), fileContents.length.toLong())
        return DownloadOutcome.Success(destination)
    }

    override suspend fun downloadText(url: String): Result<String> =
        checksumText ?: Result.success(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  Jularr-Mobile-1.0.0.apk",
        )
}

private fun updateInfo(checksumAsset: GitHubReleaseAsset?) = UpdateCheckResult.UpdateAvailable(
    release = GitHubRelease(
        tagName = "v0.2.0",
        name = "Jularr 0.2.0",
        htmlUrl = "https://github.com/Juloc/Jularr/releases/tag/v0.2.0",
        draft = false,
        prerelease = false,
        body = "Notes",
        assets = emptyList(),
    ),
    version = "0.2.0",
    apkAsset = GitHubReleaseAsset(
        name = "Jularr-Mobile-0.2.0.apk",
        downloadUrl = "https://example.invalid/Jularr-Mobile-0.2.0.apk",
        sizeBytes = 3L,
    ),
    checksumAsset = checksumAsset,
)

class UpdateManagerTest {

    @get:Rule
    val tempFolder = TemporaryFolder()

    @Test
    fun succeedsWhenChecksumMatches() = runBlocking {
        val checksumAsset = GitHubReleaseAsset(
            name = "Jularr-Mobile-0.2.0.apk.sha256",
            downloadUrl = "https://example.invalid/Jularr-Mobile-0.2.0.apk.sha256",
            sizeBytes = 90L,
        )
        val manager = UpdateManager(downloader = FakeUpdateDownloader(fileContents = "abc"))
        val destination = File(tempFolder.newFolder(), "update.apk")

        val result = manager.downloadAndVerify(updateInfo(checksumAsset), destination)

        assertTrue("expected Success but was $result", result is UpdateDownloadResult.Success)
        assertTrue(destination.isFile)
    }

    @Test
    fun deletesFileAndReportsMismatchWhenChecksumIsWrong() = runBlocking {
        val checksumAsset = GitHubReleaseAsset(
            name = "Jularr-Mobile-0.2.0.apk.sha256",
            downloadUrl = "https://example.invalid/Jularr-Mobile-0.2.0.apk.sha256",
            sizeBytes = 90L,
        )
        val manager = UpdateManager(
            downloader = FakeUpdateDownloader(
                fileContents = "tampered",
                checksumText = Result.success(
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  Jularr-Mobile-1.0.0.apk",
                ),
            ),
        )
        val destination = File(tempFolder.newFolder(), "update.apk")

        val result = manager.downloadAndVerify(updateInfo(checksumAsset), destination)

        assertEquals(UpdateDownloadResult.ChecksumMismatch, result)
        assertFalse(destination.exists())
    }

    @Test
    fun succeedsWithoutChecksumAssetWhenReleasePredatesChecksums() = runBlocking {
        val manager = UpdateManager(downloader = FakeUpdateDownloader(fileContents = "abc"))
        val destination = File(tempFolder.newFolder(), "update.apk")

        val result = manager.downloadAndVerify(updateInfo(checksumAsset = null), destination)

        assertTrue("expected Success but was $result", result is UpdateDownloadResult.Success)
    }

    @Test
    fun reportsHttpFailureAndLeavesNoFileBehind() = runBlocking {
        val manager = UpdateManager(
            downloader = FakeUpdateDownloader(outcome = DownloadOutcome.HttpError(404)),
        )
        val destination = File(tempFolder.newFolder(), "update.apk")

        val result = manager.downloadAndVerify(updateInfo(checksumAsset = null), destination)

        val failed = result as? UpdateDownloadResult.Failed
        assertTrue("expected Failed but was $result", failed != null)
        assertTrue(failed!!.message.contains("404"))
        assertFalse(destination.exists())
    }

    @Test
    fun reportsNetworkFailureGracefully() = runBlocking {
        val manager = UpdateManager(
            downloader = FakeUpdateDownloader(
                outcome = DownloadOutcome.NetworkError("no network"),
            ),
        )
        val destination = File(tempFolder.newFolder(), "update.apk")

        val result = manager.downloadAndVerify(updateInfo(checksumAsset = null), destination)

        assertEquals(UpdateDownloadResult.Failed("no network"), result)
    }
}
