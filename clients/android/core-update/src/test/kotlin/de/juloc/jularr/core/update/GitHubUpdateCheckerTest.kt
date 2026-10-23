package de.juloc.jularr.core.update

import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException

private class FakeGitHubReleasesApi(
    private val release: GitHubRelease? = null,
    private val failure: Throwable? = null,
) : GitHubReleasesApi {
    var requestedOwner: String? = null
    var requestedRepo: String? = null

    override suspend fun getLatestRelease(owner: String, repo: String): GitHubRelease {
        requestedOwner = owner
        requestedRepo = repo
        failure?.let { throw it }
        return release ?: throw GitHubReleaseNotFoundException("no release")
    }
}

private fun releaseWithMobileApk(
    version: String,
    draft: Boolean = false,
    prerelease: Boolean = false,
    includeChecksum: Boolean = true,
): GitHubRelease {
    val assets = buildList {
        add(
            GitHubReleaseAsset(
                name = "Jularr-Mobile-$version.apk",
                downloadUrl = "https://example.invalid/Jularr-Mobile-$version.apk",
                sizeBytes = 12_345L,
            ),
        )
        if (includeChecksum) {
            add(
                GitHubReleaseAsset(
                    name = "Jularr-Mobile-$version.apk.sha256",
                    downloadUrl = "https://example.invalid/Jularr-Mobile-$version.apk.sha256",
                    sizeBytes = 90L,
                ),
            )
        }
    }

    return GitHubRelease(
        tagName = "v$version",
        name = "Jularr $version",
        htmlUrl = "https://github.com/Juloc/Jularr/releases/tag/v$version",
        draft = draft,
        prerelease = prerelease,
        body = "Release notes for $version",
        assets = assets,
    )
}

class GitHubUpdateCheckerTest {

    @Test
    fun reportsUpdateAvailableWhenReleaseIsNewer() = runBlocking {
        val api = FakeGitHubReleasesApi(releaseWithMobileApk("0.2.0"))
        val checker = GitHubUpdateChecker(api = api)

        val result = checker.checkForUpdate("0.1.0", AppVariant.Mobile)

        val available = result as? UpdateCheckResult.UpdateAvailable
        assertTrue("expected UpdateAvailable but was $result", available != null)
        assertEquals("0.2.0", available!!.version)
        assertEquals("Jularr-Mobile-0.2.0.apk", available.apkAsset.name)
        assertEquals("Jularr-Mobile-0.2.0.apk.sha256", available.checksumAsset?.name)
        assertEquals("Juloc", api.requestedOwner)
        assertEquals("Jularr", api.requestedRepo)
    }

    @Test
    fun reportsUpToDateWhenReleaseMatchesInstalledVersion() = runBlocking {
        val api = FakeGitHubReleasesApi(releaseWithMobileApk("0.1.0"))
        val checker = GitHubUpdateChecker(api = api)

        val result = checker.checkForUpdate("0.1.0", AppVariant.Mobile)

        assertEquals(UpdateCheckResult.UpToDate, result)
    }

    @Test
    fun neverReportsAnUpdateForAnOlderRelease() = runBlocking {
        // Prevents downgrade installation: an installed version newer than the latest
        // published release (e.g. a local dev build) must never be offered "an update".
        val api = FakeGitHubReleasesApi(releaseWithMobileApk("0.1.0"))
        val checker = GitHubUpdateChecker(api = api)

        val result = checker.checkForUpdate("0.2.0", AppVariant.Mobile)

        assertEquals(UpdateCheckResult.UpToDate, result)
    }

    @Test
    fun ignoresDraftReleases() = runBlocking {
        val api = FakeGitHubReleasesApi(releaseWithMobileApk("0.2.0", draft = true))
        val checker = GitHubUpdateChecker(api = api)

        val result = checker.checkForUpdate("0.1.0", AppVariant.Mobile)

        assertEquals(UpdateCheckResult.UpToDate, result)
    }

    @Test
    fun ignoresPrereleaseReleases() = runBlocking {
        val api = FakeGitHubReleasesApi(releaseWithMobileApk("0.2.0", prerelease = true))
        val checker = GitHubUpdateChecker(api = api)

        val result = checker.checkForUpdate("0.1.0", AppVariant.Mobile)

        assertEquals(UpdateCheckResult.UpToDate, result)
    }

    @Test
    fun neverResolvesTheTvAssetForTheMobileVariant() = runBlocking {
        val release = GitHubRelease(
            tagName = "v0.2.0",
            name = "Jularr 0.2.0",
            htmlUrl = "https://github.com/Juloc/Jularr/releases/tag/v0.2.0",
            draft = false,
            prerelease = false,
            body = null,
            assets = listOf(
                GitHubReleaseAsset("Jularr-TV-0.2.0.apk", "https://example.invalid/tv.apk", 1L),
            ),
        )
        val checker = GitHubUpdateChecker(api = FakeGitHubReleasesApi(release))

        val result = checker.checkForUpdate("0.1.0", AppVariant.Mobile)

        val error = result as? UpdateCheckResult.Error
        assertTrue("expected Error but was $result", error != null)
    }

    @Test
    fun handlesNoPublishedReleaseGracefully() = runBlocking {
        val checker = GitHubUpdateChecker(api = FakeGitHubReleasesApi(release = null))

        val result = checker.checkForUpdate("0.1.0", AppVariant.Mobile)

        assertEquals(UpdateCheckResult.UpToDate, result)
    }

    @Test
    fun handlesNetworkFailureGracefully() = runBlocking {
        val checker = GitHubUpdateChecker(
            api = FakeGitHubReleasesApi(failure = IOException("no network")),
        )

        val result = checker.checkForUpdate("0.1.0", AppVariant.Mobile)

        val error = result as? UpdateCheckResult.Error
        assertTrue("expected Error but was $result", error != null)
        assertEquals("no network", error!!.message)
    }

    @Test
    fun handlesMissingAssetGracefully() = runBlocking {
        val release = releaseWithMobileApk("0.2.0").copy(assets = emptyList())
        val checker = GitHubUpdateChecker(api = FakeGitHubReleasesApi(release))

        val result = checker.checkForUpdate("0.1.0", AppVariant.Mobile)

        val error = result as? UpdateCheckResult.Error
        assertTrue("expected Error but was $result", error != null)
    }

    @Test
    fun handlesUnparsableCurrentVersionGracefully() = runBlocking {
        val checker = GitHubUpdateChecker(api = FakeGitHubReleasesApi(releaseWithMobileApk("0.2.0")))

        val result = checker.checkForUpdate("not-a-version", AppVariant.Mobile)

        val error = result as? UpdateCheckResult.Error
        assertTrue("expected Error but was $result", error != null)
    }

    @Test
    fun checksumIsNullWhenReleasePredatesChecksumPublishing() = runBlocking {
        val api = FakeGitHubReleasesApi(releaseWithMobileApk("0.2.0", includeChecksum = false))
        val checker = GitHubUpdateChecker(api = api)

        val result = checker.checkForUpdate("0.1.0", AppVariant.Mobile)

        val available = result as UpdateCheckResult.UpdateAvailable
        assertEquals(null, available.checksumAsset)
    }
}
