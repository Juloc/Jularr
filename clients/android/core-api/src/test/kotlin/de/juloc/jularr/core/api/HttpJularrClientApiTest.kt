package de.juloc.jularr.core.api

import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotSame
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.atomic.AtomicReference

class HttpJularrClientApiTest {
    @Test
    fun requestWorkIsDispatchedAwayFromTheCallerThread() {
        val callerThread = Thread.currentThread()
        val requestThread = AtomicReference<Thread>()
        val client = HttpJularrClientApi(
            origin = "http://localhost",
            requestHeaders = {
                requestThread.set(Thread.currentThread())
                throw RequestProbeComplete()
            },
        )

        var probeCompleted = false
        try {
            runBlocking {
                client.logout()
            }
        } catch (_: RequestProbeComplete) {
            probeCompleted = true
        }

        assertTrue(probeCompleted)
        assertNotSame(callerThread, requestThread.get())
    }

    @Test
    fun featureParserIncludesNativeSessionAuth() {
        val enabled = setOf(
            "library",
            "nativeSessionAuth",
            "nativePlayerBootstrap",
            "directPlayback",
            "playbackProgress",
            "httpRangeRequests",
            "mediaTrackMetadata",
            "normalizedLearningCues",
            "learningStateMutation",
            "liveMp4Fallback",
            "storageAvailability",
            "ownerWakeOnLan",
        )

        val parsed = ClientFeatureFlagParser.parse { name -> name in enabled }

        assertTrue(parsed.nativeSessionAuth)
        assertTrue(parsed.library)
        assertTrue(parsed.nativePlayerBootstrap)
        assertFalse(parsed.hlsFallback)
        assertFalse(parsed.playbackSessions)
    }

    @Test
    fun continueWatchingAndPlaybackHistoryDefaultToFalseOnAnOlderServer() {
        val enabled = setOf(
            "library",
            "nativeSessionAuth",
            "nativePlayerBootstrap",
            "directPlayback",
            "playbackProgress",
            "httpRangeRequests",
            "mediaTrackMetadata",
            "normalizedLearningCues",
            "learningStateMutation",
            "liveMp4Fallback",
            "storageAvailability",
            "ownerWakeOnLan",
        )

        val parsed = ClientFeatureFlagParser.parse { name -> name in enabled }

        assertFalse(parsed.continueWatching)
        assertFalse(parsed.playbackHistory)
        assertFalse(parsed.watchlist)
    }

    @Test
    fun continueWatchingAndPlaybackHistoryParseWhenAdvertised() {
        val enabled = setOf("continueWatching", "playbackHistory")
        val parsed = ClientFeatureFlagParser.parse { name -> name in enabled }

        assertTrue(parsed.continueWatching)
        assertTrue(parsed.playbackHistory)
    }

    @Test
    fun watchlistDefaultsToFalseOnAnOlderServerAndParsesWhenAdvertised() {
        val olderServer = ClientFeatureFlagParser.parse { name -> name == "library" }
        assertFalse(olderServer.watchlist)

        val enabled = setOf("watchlist")
        val parsed = ClientFeatureFlagParser.parse { name -> name in enabled }
        assertTrue(parsed.watchlist)
    }

    @Test
    fun devicePairingDefaultsToFalseOnAnOlderServerAndParsesWhenAdvertised() {
        val olderServer = ClientFeatureFlagParser.parse { name -> name == "library" }
        assertFalse(olderServer.devicePairing)

        val enabled = setOf("devicePairing")
        val parsed = ClientFeatureFlagParser.parse { name -> name in enabled }
        assertTrue(parsed.devicePairing)
    }

    @Test
    fun offlinePackagesCapabilityIsOptionalAndParsed() {
        val absent = ClientFeatureFlagParser.parse { name -> name == "library" }
        assertFalse(absent.offlinePackages)

        val advertised = ClientFeatureFlagParser.parse { name -> name == "offlinePackages" }
        assertTrue(advertised.offlinePackages)
    }

    private class RequestProbeComplete : RuntimeException()
}

