package de.juloc.jularr.core.api

import de.juloc.jularr.core.model.ContinueItemV1
import de.juloc.jularr.core.model.DevicePlatform
import de.juloc.jularr.core.model.HomeShelfV1
import de.juloc.jularr.core.model.WorkCardV1
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.assertFalse
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
    fun aWorkCardIsReadFromTheServerShape() {
        val card = JSONObject(
            """{"id":10,"mediaTypeId":2,"mediaSubtypeId":1,"title":"One Show","releaseYear":2006,"posterUrl":"https://example.test/poster.jpg",
               "mediaStateTypeId":3,"isWatchlisted":true,"availabilityStateId":2,"availableUnits":2,"monitoredUnits":4,
               "audioLanguages":["ja"],"subtitleLanguages":["de","en"]}""",
        ).toWorkCard()

        assertEquals(10L, card.id)
        assertEquals(1, card.mediaSubtypeId)
        assertEquals("One Show", card.title)
        assertEquals(2006, card.releaseYear)
        assertEquals(WorkCardV1.AvailabilityPartial, card.availabilityStateId)
        assertEquals(2, card.availableUnits)
        assertEquals(4, card.monitoredUnits)
        assertEquals(listOf("ja"), card.audioLanguages)
        assertEquals(listOf("de", "en"), card.subtitleLanguages)
    }

    @Test
    fun aWorkCardWithoutAYearOrAPosterKeepsBothEmpty() {
        val card = JSONObject(
            """{"id":30,"mediaTypeId":3,"mediaSubtypeId":null,"title":"One Book","releaseYear":null,"posterUrl":null,"mediaStateTypeId":1,
               "isWatchlisted":true,"availabilityStateId":1,"availableUnits":0,"monitoredUnits":0,
               "audioLanguages":[],"subtitleLanguages":[]}""",
        ).toWorkCard()

        assertNull(card.releaseYear)
        assertNull(card.posterUrl)
        assertNull(card.mediaSubtypeId)
        assertEquals(WorkCardV1.AvailabilityAvailable, card.availabilityStateId)
    }

    @Test
    fun aContinueItemIsReadFromTheServerShape() {
        val item = JSONObject(
            """{"workId":10,"mediaAssetId":55,"mediaTypeId":2,"title":"One Show","posterUrl":"https://example.test/poster.jpg",
               "workUnitTypeId":1,"unitNumber":3.0,"parentUnitNumber":1.0,"positionMs":120000,"durationMs":2400000,
               "progression":0.25,"mediaAssetTypeId":1}""",
        ).toContinueItem()

        assertEquals(10L, item.workId)
        assertEquals(55L, item.mediaAssetId)
        assertEquals(2, item.mediaTypeId)
        assertEquals("One Show", item.title)
        assertEquals(1, item.workUnitTypeId)
        assertEquals(3.0, item.unitNumber!!, 0.001)
        assertEquals(1.0, item.parentUnitNumber!!, 0.001)
        assertEquals(120000L, item.positionMs)
        assertEquals(2400000L, item.durationMs)
        assertEquals(0.25, item.progression!!, 0.001)
        assertEquals(ContinueItemV1.AssetVideo, item.mediaAssetTypeId)
    }

    @Test
    fun aContinueItemWithoutAUnitKeepsTheOptionalNumbersEmpty() {
        val item = JSONObject(
            """{"workId":11,"mediaAssetId":56,"mediaTypeId":3,"title":"One Book","posterUrl":null,
               "workUnitTypeId":null,"unitNumber":null,"parentUnitNumber":null,"positionMs":0,"durationMs":null,
               "progression":null,"mediaAssetTypeId":3}""",
        ).toContinueItem()

        assertNull(item.posterUrl)
        assertNull(item.workUnitTypeId)
        assertNull(item.unitNumber)
        assertNull(item.parentUnitNumber)
        assertNull(item.durationMs)
        assertNull(item.progression)
        assertEquals(ContinueItemV1.AssetEbook, item.mediaAssetTypeId)
    }

    @Test
    fun theHomeLayoutIsReadFromTheServerShape() {
        val shelves = listOf(
            JSONObject("""{"homeShelfTypeId":1,"isHidden":false}""").toHomeShelf(),
            JSONObject("""{"homeShelfTypeId":2,"isHidden":true}""").toHomeShelf(),
        )

        assertEquals(HomeShelfV1.ContinueWatching, shelves[0].homeShelfTypeId)
        assertEquals(false, shelves[0].isHidden)
        assertEquals(HomeShelfV1.ContinueListening, shelves[1].homeShelfTypeId)
        assertEquals(true, shelves[1].isHidden)
    }

    @Test
    fun aDiscoverScopeIsReadFromTheServerShape() {
        val scope = JSONObject(
            """{"slug":"anime","mediaTypeId":2,"mediaSubtypeId":1,"isCatalog":false,"feeds":[1,2]}""",
        ).toDiscoverScope()

        assertEquals("anime", scope.slug)
        assertEquals(2, scope.mediaTypeId)
        assertEquals(1, scope.mediaSubtypeId)
        assertEquals(false, scope.isCatalog)
        assertEquals(listOf(1, 2), scope.feeds)
    }

    @Test
    fun aDiscoverScopeWithoutASubtypeKeepsItEmpty() {
        val scope = JSONObject(
            """{"slug":"film","mediaTypeId":1,"mediaSubtypeId":null,"isCatalog":true,"feeds":[]}""",
        ).toDiscoverScope()

        assertNull(scope.mediaSubtypeId)
        assertEquals(true, scope.isCatalog)
        assertEquals(emptyList<Int>(), scope.feeds)
    }

    @Test
    fun theWatchlistPageIsReadFromTheServerShape() {
        val page = JSONObject(
            """{"items":[{"id":10,"mediaTypeId":2,"mediaSubtypeId":1,"title":"One Show","releaseYear":2006,"posterUrl":null,
               "mediaStateTypeId":3,"isWatchlisted":true,"availabilityStateId":7,"availableUnits":4,"monitoredUnits":4,
               "audioLanguages":["ja"],"subtitleLanguages":["de"]}],"page":1,"pageSize":25,"totalCount":1,"hasMore":false}""",
        )

        val cards = page.toWorkCards()

        assertEquals(1, cards.size)
        assertEquals(WorkCardV1.AvailabilityProfileAudio, cards[0].availabilityStateId)
    }

    @Test
    fun theInstancePresentationIsReadFromTheServerShape() {
        val presentation = JSONObject(
            """{"name":"Jularr","uiLocaleId":2,"publicUrl":null,"devicesEnabled":true,"registrationEnabled":false}""",
        ).toInstancePresentation()

        assertEquals("Jularr", presentation.name)
        assertEquals(2L, presentation.uiLocaleId)
        assertNull(presentation.publicUrl)
        assertTrue(presentation.devicesEnabled)
        assertFalse(presentation.registrationEnabled)
    }

    @Test
    fun theServerTextsAreReadFromTheServerShape() {
        val texts = JSONObject("""{"uiLocaleId":2,"locale":"en-GB","isRightToLeft":false,"texts":{"watchlist.title":"Watchlist"}}""")
            .toUiTexts()

        assertEquals(mapOf("watchlist.title" to "Watchlist"), texts)
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
