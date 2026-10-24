package de.juloc.jularr.core.update

import java.io.IOException

interface UpdateChecker {
    suspend fun checkForUpdate(currentVersionName: String, variant: AppVariant): UpdateCheckResult
}

/**
 * Queries the public, unauthenticated GitHub Releases API for the configured Jularr
 * repository and compares the newest stable release against the installed app version
 * for [variant]. Draft/pre-release releases are never surfaced (`/releases/latest`
 * already excludes them) and a release that is not newer than the installed version
 * never reports an update, which also rules out ever "upgrading" to an older build.
 */
class GitHubUpdateChecker(
    private val api: GitHubReleasesApi = HttpGitHubReleasesApi(),
    private val owner: String = "Juloc",
    private val repo: String = "Jularr",
) : UpdateChecker {

    override suspend fun checkForUpdate(
        currentVersionName: String,
        variant: AppVariant,
    ): UpdateCheckResult {
        val currentVersion = SemVer.parse(currentVersionName)
            ?: return UpdateCheckResult.Error("The installed app version could not be parsed.")

        val release = try {
            api.getLatestRelease(owner, repo)
        } catch (exception: GitHubReleaseNotFoundException) {
            return UpdateCheckResult.UpToDate
        } catch (exception: IOException) {
            return UpdateCheckResult.Error(
                exception.message ?: "Jularr's GitHub releases could not be reached.",
            )
        }

        if (release.draft || release.prerelease) {
            // Defensive only: /releases/latest already excludes drafts and pre-releases.
            return UpdateCheckResult.UpToDate
        }

        val apkAsset = UpdateAssetResolver.resolveApk(release, variant)
            ?: return UpdateCheckResult.Error(
                "The latest release has no ${variant.name} app package yet.",
            )
        val releaseVersionText = UpdateAssetResolver.versionFromAssetName(apkAsset, variant)
            ?: return UpdateCheckResult.Error("The release asset name could not be parsed.")
        val releaseVersion = SemVer.parse(releaseVersionText)
            ?: return UpdateCheckResult.Error("The release version could not be parsed.")

        if (releaseVersion <= currentVersion) {
            return UpdateCheckResult.UpToDate
        }

        return UpdateCheckResult.UpdateAvailable(
            release = release,
            version = releaseVersionText,
            apkAsset = apkAsset,
            checksumAsset = UpdateAssetResolver.resolveChecksum(release, apkAsset),
        )
    }
}
