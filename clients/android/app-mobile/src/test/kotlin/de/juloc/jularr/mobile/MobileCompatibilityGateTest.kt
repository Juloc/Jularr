package de.juloc.jularr.mobile

import de.juloc.jularr.core.model.ClientCapabilities
import de.juloc.jularr.core.model.ClientFeatureFlags
import org.junit.Assert.assertTrue
import org.junit.Test

class MobileCompatibilityGateTest {
    @Test
    fun incompatibleServerProducesExplicitUpdateRequiredState() {
        val state = MobileCompatibilityGate.evaluate(
            capabilities(apiVersion = 0, minimumSupportedApiVersion = 0),
        )

        assertTrue(state is MobileCompatibilityState.UpdateRequired)
    }

    private fun capabilities(
        apiVersion: Int,
        minimumSupportedApiVersion: Int,
    ) = ClientCapabilities(
        apiVersion = apiVersion,
        minimumSupportedApiVersion = minimumSupportedApiVersion,
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
            liveMp4Fallback = true,
            hlsFallback = false,
            playbackSessions = false,
            companionPairing = false,
            companionControl = false,
            storageAvailability = true,
            ownerWakeOnLan = true,
        ),
    )
}
