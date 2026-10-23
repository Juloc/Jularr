package de.juloc.jularr.tv

import de.juloc.jularr.core.model.ClientCapabilities
import de.juloc.jularr.core.model.ClientFeatureFlags
import de.juloc.jularr.core.model.CompatibilityFallback
import de.juloc.jularr.core.model.MediaAvailability
import de.juloc.jularr.core.model.PlaybackOption
import de.juloc.jularr.core.model.PlayerBootstrap
import de.juloc.jularr.core.model.PlayerEpisode
import de.juloc.jularr.core.model.PlayerMedia
import de.juloc.jularr.core.player.PlaybackTransport
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class TvPlaybackPlannerTest {
    @Test
    fun directPlaybackKeepsLocalSeekPosition() {
        val plan = TvPlaybackPlanner.plan(
            serverOrigin = "https://jularr.example",
            capabilities = capabilities(),
            bootstrap = bootstrap(),
            directSupported = true,
            startPositionMs = 42_500,
        )

        assertEquals(PlaybackTransport.DIRECT, plan.transport)
        assertEquals(
            "https://jularr.example/api/client/v1/media/media/content",
            plan.uri,
        )
        assertEquals(42_500, plan.startPositionMs)
    }

    @Test
    fun fallbackRestartsServerStreamAtSameAbsolutePosition() {
        val plan = TvPlaybackPlanner.plan(
            serverOrigin = "https://jularr.example",
            capabilities = capabilities(),
            bootstrap = bootstrap(),
            directSupported = false,
            startPositionMs = 42_500,
        )

        assertEquals(PlaybackTransport.LIVE_MP4_FALLBACK, plan.transport)
        assertEquals(
            "https://jularr.example/api/client/v1/episodes/episode/fallback?mode=server&startSeconds=42.500",
            plan.uri,
        )
        assertEquals(0, plan.startPositionMs)
    }

    @Test
    fun hlsFallbackRestartsAtRequestedAbsolutePosition() {
        val plan = TvPlaybackPlanner.plan(
            serverOrigin = "https://jularr.example",
            capabilities = capabilities(hls = true),
            bootstrap = bootstrap(
                fallbackKind = "hls",
                fallbackUrl = "/api/client/v1/episodes/episode/hls",
                seekableWithinStream = true,
            ),
            directSupported = false,
            startPositionMs = 42_500,
        )

        assertEquals(PlaybackTransport.HLS_FALLBACK, plan.transport)
        assertEquals(
            "https://jularr.example/api/client/v1/episodes/episode/hls?startSeconds=42.500",
            plan.uri,
        )
        assertEquals(0, plan.startPositionMs)
    }

    @Test
    fun externalPlaybackRouteIsRejected() {
        val bootstrap = bootstrap(
            directUrl = "https://evil.example/video.mkv",
        )

        assertThrows(IllegalArgumentException::class.java) {
            TvPlaybackPlanner.plan(
                serverOrigin = "https://jularr.example",
                capabilities = capabilities(),
                bootstrap = bootstrap,
                directSupported = true,
                startPositionMs = 0,
            )
        }
    }

    private fun capabilities(
        hls: Boolean = false,
    ) = ClientCapabilities(
        apiVersion = 2,
        minimumSupportedApiVersion = 2,
        serverVersion = "test",
        features = ClientFeatureFlags(
            library = true,
            nativeSessionAuth = true,
            nativePlayerBootstrap = true,
            directPlayback = true,
            playbackProgress = true,
            httpRangeRequests = true,
            mediaTrackMetadata = true,
            normalizedLearningCues = true,
            learningStateMutation = true,
            liveMp4Fallback = true,
            hlsFallback = hls,
            playbackSessions = false,
            companionPairing = false,
            companionControl = false,
            storageAvailability = true,
            ownerWakeOnLan = true,
        ),
    )

    private fun bootstrap(
        directUrl: String = "/api/client/v1/media/media/content",
        fallbackKind: String = "live-fragmented-mp4",
        fallbackUrl: String = "/api/client/v1/episodes/episode/fallback?mode=server",
        seekableWithinStream: Boolean = false,
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
            durationMs = 120_000,
            videoCodec = "hevc",
            pixelFormat = "yuv420p",
            audioCodec = "aac",
            directContentUrl = directUrl,
            supportsRangeRequests = true,
            device = PlaybackOption("ready", "Direct", false),
            server = PlaybackOption("ready", "Server", true),
            availability = MediaAvailability(
                state = "available",
                retryable = false,
                retryAfterMs = 0,
                canWake = false,
                rootId = null,
                availabilityUrl = "/api/client/v1/media/media/availability",
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
            seekableWithinStream = seekableWithinStream,
            canRestartAtPosition = true,
            url = fallbackUrl,
        ),
    )
}
