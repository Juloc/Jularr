package de.juloc.jularr.core.update

import java.io.File

sealed interface UpdateDownloadResult {
    data class Success(val file: File) : UpdateDownloadResult
    data object ChecksumMismatch : UpdateDownloadResult
    data class Failed(val message: String) : UpdateDownloadResult
}

/**
 * The one updater implementation app-mobile and app-tv both call, so the check/download/
 * verify sequence is never duplicated between the two clients (docs/ANDROID_CLIENTS.md
 * §"Architecture"). Deliberately stateless/Context-free so it is trivially unit-testable;
 * callers (a Compose screen with its own `rememberCoroutineScope`) own any UI state.
 */
class UpdateManager(
    private val checker: UpdateChecker = GitHubUpdateChecker(),
    private val downloader: UpdateDownloader = HttpUpdateDownloader(),
) {
    suspend fun checkForUpdate(currentVersionName: String, variant: AppVariant): UpdateCheckResult =
        checker.checkForUpdate(currentVersionName, variant)

    /**
     * Downloads the release APK to [destination] and verifies it against the release's
     * `.sha256` asset when one was published. A failed or unverifiable download never
     * leaves a partial/unverified file behind for [de.juloc.jularr.core.update.UpdateInstall]
     * to pick up.
     */
    suspend fun downloadAndVerify(
        info: UpdateCheckResult.UpdateAvailable,
        destination: File,
        onProgress: (bytesRead: Long, totalBytes: Long) -> Unit = { _, _ -> },
    ): UpdateDownloadResult {
        val outcome = downloader.download(info.apkAsset.downloadUrl, destination, onProgress)
        if (outcome !is DownloadOutcome.Success) {
            destination.delete()
            return UpdateDownloadResult.Failed(describeFailure(outcome))
        }

        val checksumAsset = info.checksumAsset
        if (checksumAsset != null) {
            val checksumText = downloader.downloadText(checksumAsset.downloadUrl).getOrNull()
            if (checksumText == null || !Sha256.matches(destination, checksumText)) {
                destination.delete()
                return UpdateDownloadResult.ChecksumMismatch
            }
        }

        return UpdateDownloadResult.Success(destination)
    }

    private fun describeFailure(outcome: DownloadOutcome): String = when (outcome) {
        is DownloadOutcome.HttpError -> "The update download failed (HTTP ${outcome.statusCode})."
        is DownloadOutcome.NetworkError -> outcome.message
        is DownloadOutcome.Success -> ""
    }
}
