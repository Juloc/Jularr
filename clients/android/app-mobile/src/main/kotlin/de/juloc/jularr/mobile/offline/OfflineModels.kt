package de.juloc.jularr.mobile.offline

import java.security.MessageDigest

enum class DownloadState {
    QUEUED,
    DOWNLOADING,
    PAUSED,
    READY,
    FAILED,
}

/**
 * Local record of one managed download. Media identity (size, ETag,
 * fingerprint) is copied from the server descriptor and only used to verify
 * the local copy; the server remains its owner.
 */
data class OfflineDownload(
    val ownerKey: String,
    val episodeId: String,
    val animeTitle: String,
    val episodeTitle: String,
    val seasonNumber: Int,
    val episodeNumber: Int,
    val contentUrl: String,
    val sizeBytes: Long,
    val eTag: String,
    val fingerprint: String,
    val fingerprintAlgorithm: String,
    val state: DownloadState,
    val downloadedBytes: Long = 0,
    val failure: String? = null,
    val createdAtMs: Long = 0,
    /** Local copy of the last known canonical progress, used only to resume offline. */
    val resumePositionMs: Long = 0,
    val watched: Boolean = false,
) {
    val progressPercent: Int
        get() = if (sizeBytes <= 0) 0 else ((downloadedBytes * 100) / sizeBytes).toInt().coerceIn(0, 100)

    /** Bytes this download occupies on the device right now. */
    val bytesOnDevice: Long
        get() = if (state == DownloadState.READY) sizeBytes else downloadedBytes

    /** Bytes this download will occupy once finished; failed downloads only keep their partial file. */
    val committedBytes: Long
        get() = if (state == DownloadState.FAILED) downloadedBytes else sizeBytes
}

/** The account whose downloads are currently accessible on this device. */
data class OfflineAccount(
    val origin: String,
    val profileId: String,
    val signedIn: Boolean,
) {
    val ownerKey: String
        get() = OfflineOwner.key(origin, profileId)
}

data class OfflineSettings(
    val limitBytes: Long = OfflineStoragePolicy.DefaultLimitBytes,
    val wifiOnly: Boolean = true,
)

/** One queued offline checkpoint. The queue holds at most one entry per episode. */
data class PendingProgress(
    val episodeId: String,
    val positionMs: Long,
    val durationMs: Long?,
    val completed: Boolean,
)

data class OfflineSnapshot(
    val account: OfflineAccount? = null,
    val settings: OfflineSettings = OfflineSettings(),
    val downloads: List<OfflineDownload> = emptyList(),
    val pending: Map<String, List<PendingProgress>> = emptyMap(),
) {
    /** Downloads of the signed-in account for [origin]; empty while signed out or for another server. */
    fun accessibleDownloads(origin: String): List<OfflineDownload> {
        val current = account?.takeIf { it.signedIn && it.origin == origin } ?: return emptyList()
        return downloads.filter { it.ownerKey == current.ownerKey }
    }
}

object OfflineOwner {
    /** Stable, path-safe owner key for one account on one server. */
    fun key(origin: String, profileId: String): String {
        val digest = MessageDigest.getInstance("SHA-256")
            .digest("$origin\n$profileId".toByteArray(Charsets.UTF_8))
        return digest.joinToString("") { "%02x".format(it) }.take(32)
    }
}
