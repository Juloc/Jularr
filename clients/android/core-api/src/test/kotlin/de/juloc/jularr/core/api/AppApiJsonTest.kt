package de.juloc.jularr.core.api

import de.juloc.jularr.core.model.DevicePlatform
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

class AppApiJsonTest {
    @Test
    fun pairingStartedIsReadFromTheServerShape() {
        val started = JSONObject("""{"pairingId":"pairing-1","code":"482913","expiresAt":"2026-10-19T10:00:00Z"}""")
            .toDevicePairingStarted()

        assertEquals("pairing-1", started.pairingId)
        assertEquals("482913", started.code)
        assertEquals("2026-10-19T10:00:00Z", started.expiresAt)
    }

    @Test
    fun aClaimBeforeTheConfirmationCarriesNoToken() {
        val claim = JSONObject("""{"isPaired":false}""").toDevicePairingClaim(null)

        assertEquals(false, claim.isPaired)
        assertNull(claim.deviceToken)
    }

    @Test
    fun anUnconfirmedClaimDropsATokenOfAnEarlierAnswer() {
        val claim = JSONObject("""{"isPaired":false}""").toDevicePairingClaim("token-1")

        assertEquals(false, claim.isPaired)
        assertNull(claim.deviceToken)
    }

    @Test
    fun aConfirmedClaimCarriesTheDeviceToken() {
        val claim = JSONObject("""{"isPaired":true}""").toDevicePairingClaim("token-1")

        assertEquals(true, claim.isPaired)
        assertEquals("token-1", claim.deviceToken)
    }

    @Test
    fun aConfirmedClaimWithoutTokenFailsInsteadOfSilentlySigningIn() {
        assertThrows(IllegalStateException::class.java) {
            JSONObject("""{"isPaired":true}""").toDevicePairingClaim(null)
        }
    }

    @Test
    fun capabilitiesAreReadFromTheServerShape() {
        val capabilities = JSONObject("""{"permissions":["profile.playback"],"modules":["Library","Playback"]}""")
            .toInstanceCapabilities()

        assertEquals(listOf("profile.playback"), capabilities.permissions)
        assertEquals(listOf("Library", "Playback"), capabilities.modules)
        assertEquals(true, capabilities.hasModule("Library"))
        assertEquals(false, capabilities.hasModule("Devices"))
        assertEquals(true, capabilities.hasPermission("profile.playback"))
    }

    @Test
    fun aPairingRequestCarriesThePlatformAndTheReplacedPairing() {
        val body = de.juloc.jularr.core.model.DevicePairingRequest(
            deviceName = "Living room TV",
            devicePlatform = DevicePlatform.AndroidTv,
            replacesPairingId = "pairing-0",
        ).toRequestBody()

        assertEquals("Living room TV", body.getString("deviceName"))
        assertEquals("AndroidTV", body.getString("devicePlatform"))
        assertEquals("pairing-0", body.getString("replacesPairingId"))
    }

    @Test
    fun aPairingRequestWithoutANameOmitsIt() {
        val body = de.juloc.jularr.core.model.DevicePairingRequest(
            deviceName = null,
            devicePlatform = DevicePlatform.Android,
        ).toRequestBody()

        assertEquals(false, body.has("deviceName"))
        assertEquals("Android", body.getString("devicePlatform"))
        assertEquals(false, body.has("replacesPairingId"))
    }

    @Test
    fun problemDetailsCarryTheMachineCodeAndTheResourceKey() {
        val exception = appApiException(
            401,
            """{"title":"invalid_token","status":401,"code":"invalid_token","resourceKey":"error.invalid_token","traceId":"trace-1"}""",
        )

        assertEquals(401, exception.statusCode)
        assertEquals("invalid_token", exception.code)
        assertEquals("error.invalid_token", exception.resourceKey)
        assertEquals("trace-1", exception.traceId)
    }

    @Test
    fun anUnreadableErrorKeepsItsStatus() {
        val exception = appApiException(500, "not json")

        assertEquals(500, exception.statusCode)
        assertEquals("http_error", exception.code)
        assertEquals("http_error", exception.resourceKey)
        assertNull(exception.traceId)
    }
}
