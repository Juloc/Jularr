package de.juloc.jularr.core.update

/**
 * Picks the APK (and its `.sha256` sibling) for this client's [AppVariant] from a
 * GitHub release, per the fixed naming convention in docs/ANDROID_CLIENTS.md §13:
 * `Jularr-Mobile-<version>.apk` / `Jularr-TV-<version>.apk` plus matching `.sha256`
 * files. Only assets whose name starts with the variant's own prefix are considered,
 * so a phone build can never resolve (and therefore never install) the TV package, or
 * vice versa.
 */
object UpdateAssetResolver {
    private const val ChecksumSuffix = ".sha256"
    private const val ApkSuffix = ".apk"

    fun resolveApk(release: GitHubRelease, variant: AppVariant): GitHubReleaseAsset? =
        release.assets.firstOrNull { asset ->
            asset.name.startsWith(variant.assetPrefix, ignoreCase = true) &&
                asset.name.endsWith(ApkSuffix, ignoreCase = true)
        }

    fun resolveChecksum(release: GitHubRelease, apkAsset: GitHubReleaseAsset): GitHubReleaseAsset? =
        release.assets.firstOrNull { asset ->
            asset.name.equals(apkAsset.name + ChecksumSuffix, ignoreCase = true)
        }

    /** Extracts the release version embedded in the asset's file name, e.g. "0.1.0-alpha.53". */
    fun versionFromAssetName(asset: GitHubReleaseAsset, variant: AppVariant): String? {
        if (!asset.name.startsWith(variant.assetPrefix, ignoreCase = true) ||
            !asset.name.endsWith(ApkSuffix, ignoreCase = true)
        ) {
            return null
        }

        return asset.name
            .drop(variant.assetPrefix.length)
            .dropLast(ApkSuffix.length)
            .takeIf { it.isNotBlank() }
    }
}
