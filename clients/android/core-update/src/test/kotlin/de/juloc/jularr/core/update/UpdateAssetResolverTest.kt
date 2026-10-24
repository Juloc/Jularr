package de.juloc.jularr.core.update

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class UpdateAssetResolverTest {

    private fun release(vararg assetNames: String) = GitHubRelease(
        tagName = "v0.1.0-alpha.53",
        name = "Jularr 0.1.0-alpha.53",
        htmlUrl = "https://github.com/Juloc/Jularr/releases/tag/v0.1.0-alpha.53",
        draft = false,
        prerelease = false,
        body = "Release notes",
        assets = assetNames.map { name ->
            GitHubReleaseAsset(name = name, downloadUrl = "https://example.invalid/$name", sizeBytes = 1_000L)
        },
    )

    @Test
    fun resolvesMobileApkAndIgnoresTvAsset() {
        val release = release(
            "Jularr-Mobile-0.1.0-alpha.53.apk",
            "Jularr-Mobile-0.1.0-alpha.53.apk.sha256",
            "Jularr-TV-0.1.0-alpha.53.apk",
            "Jularr-TV-0.1.0-alpha.53.apk.sha256",
        )

        val apk = UpdateAssetResolver.resolveApk(release, AppVariant.Mobile)

        assertEquals("Jularr-Mobile-0.1.0-alpha.53.apk", apk?.name)
    }

    @Test
    fun resolvesTvApkAndIgnoresMobileAsset() {
        val release = release(
            "Jularr-Mobile-0.1.0-alpha.53.apk",
            "Jularr-TV-0.1.0-alpha.53.apk",
        )

        val apk = UpdateAssetResolver.resolveApk(release, AppVariant.Tv)

        assertEquals("Jularr-TV-0.1.0-alpha.53.apk", apk?.name)
    }

    @Test
    fun neverResolvesTheOtherVariantsApkWhenOnlyItExists() {
        val release = release("Jularr-TV-0.1.0-alpha.53.apk", "Jularr-TV-0.1.0-alpha.53.apk.sha256")

        assertNull(UpdateAssetResolver.resolveApk(release, AppVariant.Mobile))
    }

    @Test
    fun resolvesMatchingChecksumAsset() {
        val release = release(
            "Jularr-Mobile-0.1.0-alpha.53.apk",
            "Jularr-Mobile-0.1.0-alpha.53.apk.sha256",
            "Jularr-TV-0.1.0-alpha.53.apk.sha256",
        )
        val apk = UpdateAssetResolver.resolveApk(release, AppVariant.Mobile)!!

        val checksum = UpdateAssetResolver.resolveChecksum(release, apk)

        assertEquals("Jularr-Mobile-0.1.0-alpha.53.apk.sha256", checksum?.name)
    }

    @Test
    fun checksumIsNullWhenReleaseHasNoSha256Asset() {
        val release = release("Jularr-Mobile-0.1.0-alpha.53.apk")
        val apk = UpdateAssetResolver.resolveApk(release, AppVariant.Mobile)!!

        assertNull(UpdateAssetResolver.resolveChecksum(release, apk))
    }

    @Test
    fun apkIsNullWhenReleaseHasNoMatchingAsset() {
        val release = release("some-other-file.txt")

        assertNull(UpdateAssetResolver.resolveApk(release, AppVariant.Mobile))
        assertNull(UpdateAssetResolver.resolveApk(release, AppVariant.Tv))
    }

    @Test
    fun extractsVersionFromAssetName() {
        val mobileAsset = GitHubReleaseAsset(
            name = "Jularr-Mobile-0.1.0-alpha.53.apk",
            downloadUrl = "https://example.invalid/a",
            sizeBytes = 1L,
        )
        val tvAsset = GitHubReleaseAsset(
            name = "Jularr-TV-1.2.3.apk",
            downloadUrl = "https://example.invalid/b",
            sizeBytes = 1L,
        )

        assertEquals(
            "0.1.0-alpha.53",
            UpdateAssetResolver.versionFromAssetName(mobileAsset, AppVariant.Mobile),
        )
        assertEquals(
            "1.2.3",
            UpdateAssetResolver.versionFromAssetName(tvAsset, AppVariant.Tv),
        )
    }

    @Test
    fun versionFromAssetNameIsNullForTheWrongVariant() {
        val tvAsset = GitHubReleaseAsset(
            name = "Jularr-TV-1.2.3.apk",
            downloadUrl = "https://example.invalid/b",
            sizeBytes = 1L,
        )

        assertNull(UpdateAssetResolver.versionFromAssetName(tvAsset, AppVariant.Mobile))
    }
}
