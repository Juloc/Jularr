package de.juloc.jularr.mobile.offline

import org.junit.Assert.assertEquals
import org.junit.Test

class OfflineStateCodecTest {
    @Test
    fun roundTripsDownloadsAccountSettingsAndQueue() {
        val download = sampleDownload(state = DownloadState.FAILED, downloadedBytes = 42)
            .copy(failure = "The device ran out of free space.", createdAtMs = 1234)
        val snapshot = OfflineSnapshot(
            account = OfflineAccount("https://jularr.example", "profile-a", signedIn = false),
            settings = OfflineSettings(limitBytes = 5L * 1024 * 1024 * 1024, wifiOnly = false),
            downloads = listOf(download, sampleDownload(episodeId = "episode-2")),
            pending = mapOf(
                download.ownerKey to listOf(
                    PendingProgress("episode-1", 60_000, null, false),
                    PendingProgress("episode-2", 1_400_000, 1_400_000, true),
                ),
            ),
        )

        assertEquals(snapshot, OfflineStateCodec.decode(OfflineStateCodec.encode(snapshot)))
    }

    @Test
    fun unknownVersionStartsEmptyInsteadOfGuessing() {
        assertEquals(OfflineSnapshot(), OfflineStateCodec.decode("""{"version":99,"downloads":[]}"""))
    }
}
