package de.juloc.jularr.core.player

import de.juloc.jularr.core.model.ClientCapabilities
import de.juloc.jularr.core.model.ClientFeatureFlags
import de.juloc.jularr.core.model.CompatibilityFallback
import de.juloc.jularr.core.model.MediaAvailability
import de.juloc.jularr.core.model.PlaybackOption
import de.juloc.jularr.core.model.PlayerBootstrap
import de.juloc.jularr.core.model.PlayerEpisode
import de.juloc.jularr.core.model.PlayerMedia
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class PlaybackSelectorTest {
    @Test
    fun directPlaybackWinsWhenDeviceSupportsOriginal() {
        assertEquals(
            PlaybackTransport.DIRECT,
            PlaybackSelector.select(
                capabilities(hls = false, liveMp4 = true),
                bootstrap(),
                DevicePlaybackSupport(directContainerAndCodecSupported = true),
            ),
        )
    }

    @Test
    fun liveMp4IsUsedWhenDirectIsUnsupportedAndCurrentServerAdvertisesIt() {
        assertEquals(
            PlaybackTransport.LIVE_MP4_FALLBACK,
            PlaybackSelector.select(
                capabilities(hls = false, liveMp4 = true),
                bootstrap(),
                DevicePlaybackSupport(directContainerAndCodecSupported = false),
            ),
        )
    }

    @Test
    fun hlsIsPreferredWhenServerAdvertisesSeekableHlsFallback() {
        assertEquals(
            PlaybackTransport.HLS_FALLBACK,
            PlaybackSelector.select(
                capabilities(hls = true, liveMp4 = true),
                bootstrap(fallbackKind = "hls"),
                DevicePlaybackSupport(directContainerAndCodecSupported = false),
            ),
        )
    }

    @Test
    fun storageFailureNeverFallsThroughToCodecFallback() {
        val exception = assertThrows(StorageUnavailableException::class.java) {
            PlaybackSelector.select(
                capabilities(hls = true, liveMp4 = true),
                bootstrap(storageState = "source_offline"),
                DevicePlaybackSupport(directContainerAndCodecSupported = false),
            )
        }

        assertEquals("source_offline", exception.state)
        assertEquals(true, exception.retryable)
    }

    private fun capabilities(
        hls: Boolean,
        liveMp4: Boolean,
    ) = ClientCapabilities(
        apiVersion = 2,
        minimumSupportedApiVersion = 2,
        serverVersion = "test",
        features = ClientFeatureFlags(
            library = true,
            nativePlayerBootstrap = true,
            directPlayback = true,
            playbackProgress = true,
            httpRangeRequests = true,
            mediaTrackMetadata = true,
            normalizedLearningCues = true,
            learningStateMutation = true,
            liveMp4Fallback = liveMp4,
            hlsFallback = hls,
            playbackSessions = false,
            companionPairing = false,
            companionControl = false,
            storageAvailability = true,
            ownerWakeOnLan = true,
        ),
    )

    private fun bootstrap(
        storageState: String = "available",
        fallbackKind: String = "live-fragmented-mp4",
    ) = PlayerBootstrap(
        apiVersion = 2,
        episode = PlayerEpisode(
            id = "episode",
            animeId = "anime",
            animeTitle = "Anime",
            title = "Episode",
            seasonNumber = 1,
            number = 1,
        ),
        media = PlayerMedia(
            mediaFileId = "media",
            fileName = "episode.mkv",
            contentType = "video/x-matroska",
            sizeBytes = 100,
            durationMs = 1_000,
            videoCodec = "hevc",
            pixelFormat = "yuv420p",
            audioCodec = "aac",
            directContentUrl = "/direct",
            supportsRangeRequests = true,
            device = PlaybackOption("ready", "Direct", false),
            server = PlaybackOption("ready", "Server", true),
            availability = MediaAvailability(
                state = storageState,
                retryable = storageState != "available",
                retryAfterMs = 2_000,
                canWake = false,
                rootId = null,
                availabilityUrl = "/availability",
                wakeUrl = null,
            ),
        ),
        audioTracks = emptyList(),
        subtitleTracks = emptyList(),
        learningSubtitles = emptyList(),
        activeLearningSubtitleTrackId = null,
        defaultAudioTrackId = null,
        defaultSubtitleTrackId = null,
        fallback = CompatibilityFallback(
            available = true,
            kind = fallbackKind,
            seekableWithinStream = false,
            canRestartAtPosition = true,
            url = "/fallback",
        ),
    )
}
