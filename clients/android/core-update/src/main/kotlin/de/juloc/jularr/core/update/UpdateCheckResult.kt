package de.juloc.jularr.core.update

/** Outcome of asking [UpdateChecker] whether a newer stable release exists. */
sealed interface UpdateCheckResult {
    /** The installed app is on the latest stable release (or newer). */
    data object UpToDate : UpdateCheckResult

    data class UpdateAvailable(
        val release: GitHubRelease,
        /** The version embedded in the release's own APK asset name, e.g. "0.1.0-alpha.53". */
        val version: String,
        val apkAsset: GitHubReleaseAsset,
        /** Null when the release predates checksum publishing; callers must handle this. */
        val checksumAsset: GitHubReleaseAsset?,
    ) : UpdateCheckResult

    /** No network, an unparsable version or a release missing this variant's asset. */
    data class Error(val message: String) : UpdateCheckResult
}
