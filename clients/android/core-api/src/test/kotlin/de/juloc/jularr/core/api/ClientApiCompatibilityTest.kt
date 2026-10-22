package de.juloc.jularr.core.api

import de.juloc.jularr.core.model.ClientCapabilities
import de.juloc.jularr.core.model.ClientFeatureFlags
import org.junit.Assert.assertEquals
import org.junit.Test

class ClientApiCompatibilityTest {
    @Test
    fun currentApiIsCompatible() {
        assertEquals(
            ApiCompatibility.Compatible,
            ClientApiCompatibility.evaluate(capabilities(apiVersion = ClientApiRoutes.ApiVersion, minimumSupported = ClientApiRoutes.ApiVersion)),
        )
    }

    @Test
    fun newerRequiredClientStopsOldClient() {
        assertEquals(
            ApiCompatibility.ClientTooOld(ClientApiRoutes.ApiVersion + 1),
            ClientApiCompatibility.evaluate(capabilities(apiVersion = ClientApiRoutes.ApiVersion + 1, minimumSupported = ClientApiRoutes.ApiVersion + 1)),
        )
    }

    @Test
    fun olderServerStopsNewClient() {
        assertEquals(
            ApiCompatibility.ServerTooOld(0),
            ClientApiCompatibility.evaluate(capabilities(apiVersion = 0, minimumSupported = 0)),
        )
    }

    @Test
    fun serverFromBeforeTheClientDeclaredCompletionContractStopsTheClient() {
        assertEquals(
            ApiCompatibility.ServerTooOld(1),
            ClientApiCompatibility.evaluate(capabilities(apiVersion = 1, minimumSupported = 1)),
        )
    }

    private fun capabilities(
        apiVersion: Int,
        minimumSupported: Int,
    ) = ClientCapabilities(
        apiVersion = apiVersion,
        minimumSupportedApiVersion = minimumSupported,
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
