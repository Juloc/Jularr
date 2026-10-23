package de.juloc.jularr.core.update

/** One asset (APK or checksum file) attached to a GitHub Release. */
data class GitHubReleaseAsset(
    val name: String,
    val downloadUrl: String,
    val sizeBytes: Long,
)

/** The subset of the public GitHub Releases API response this updater needs. */
data class GitHubRelease(
    val tagName: String,
    val name: String?,
    val htmlUrl: String,
    val draft: Boolean,
    val prerelease: Boolean,
    val body: String?,
    val assets: List<GitHubReleaseAsset>,
)
