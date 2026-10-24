package de.juloc.jularr.mobile.offline

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class OfflineStoragePolicyTest {
    private val gib = 1024L * 1024L * 1024L

    @Test
    fun admitsWithinLimitAndFreeSpace() {
        assertEquals(
            StorageDecision.Allowed,
            OfflineStoragePolicy.admit(
                requestBytes = 2 * gib,
                committedBytes = 7 * gib,
                limitBytes = 10 * gib,
                freeDeviceBytes = 20 * gib,
            ),
        )
    }

    @Test
    fun rejectsWhenTheDeviceLimitWouldBeExceeded() {
        val decision = OfflineStoragePolicy.admit(
            requestBytes = 2 * gib,
            committedBytes = 9 * gib,
            limitBytes = 10 * gib,
            freeDeviceBytes = 100 * gib,
        )

        assertEquals(StorageDecision.ExceedsLimit(2 * gib, 1 * gib), decision)
    }

    @Test
    fun keepsADeviceSafetyMargin() {
        val decision = OfflineStoragePolicy.admit(
            requestBytes = 2 * gib,
            committedBytes = 0,
            limitBytes = 10 * gib,
            freeDeviceBytes = 2 * gib + OfflineStoragePolicy.DeviceSafetyMarginBytes - 1,
        )

        assertTrue(decision is StorageDecision.InsufficientDeviceSpace)
    }

    @Test
    fun resumedPartialFilesNeedNoAdditionalDeviceSpace() {
        val decision = OfflineStoragePolicy.admit(
            requestBytes = 2 * gib,
            committedBytes = 0,
            limitBytes = 10 * gib,
            freeDeviceBytes = OfflineStoragePolicy.DeviceSafetyMarginBytes + gib,
            alreadyOnDeviceBytes = gib,
        )

        assertEquals(StorageDecision.Allowed, decision)
    }

    @Test
    fun overCommittedLimitNeverReportsNegativeAvailability() {
        val decision = OfflineStoragePolicy.admit(
            requestBytes = gib,
            committedBytes = 12 * gib,
            limitBytes = 10 * gib,
            freeDeviceBytes = 100 * gib,
        )

        assertEquals(StorageDecision.ExceedsLimit(gib, 0), decision)
    }

    @Test
    fun committedBytesCountFullSizeUnlessFailed() {
        val download = sampleDownload(state = DownloadState.DOWNLOADING, downloadedBytes = 100, sizeBytes = 1_000)
        assertEquals(1_000, download.committedBytes)
        assertEquals(100, download.bytesOnDevice)
        assertEquals(100, download.copy(state = DownloadState.FAILED).committedBytes)
        assertEquals(1_000, download.copy(state = DownloadState.READY).bytesOnDevice)
    }

    @Test
    fun writesStopBeforeTheSafetyMargin() {
        assertTrue(OfflineStoragePolicy.hasRoomFor(1, OfflineStoragePolicy.DeviceSafetyMarginBytes + 1))
        assertFalse(OfflineStoragePolicy.hasRoomFor(2, OfflineStoragePolicy.DeviceSafetyMarginBytes + 1))
    }
}

internal fun sampleDownload(
    state: DownloadState = DownloadState.READY,
    episodeId: String = "episode-1",
    sizeBytes: Long = 1_000,
    downloadedBytes: Long = 0,
    resumePositionMs: Long = 0,
    watched: Boolean = false,
) = OfflineDownload(
    ownerKey = OfflineOwner.key("https://jularr.example", "profile-a"),
    episodeId = episodeId,
    animeTitle = "Anime",
    episodeTitle = "Episode",
    seasonNumber = 1,
    episodeNumber = 1,
    contentUrl = "/api/client/v1/offline/media/m/content",
    sizeBytes = sizeBytes,
    eTag = "\"3e8-1\"",
    fingerprint = "abc",
    fingerprintAlgorithm = OfflineMediaVerifier.FingerprintAlgorithm,
    state = state,
    downloadedBytes = downloadedBytes,
    resumePositionMs = resumePositionMs,
    watched = watched,
)
