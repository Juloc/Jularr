package de.juloc.jularr.core.api.playbackextras

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.math.BigDecimal

class TvV1PlaybackExtrasJsonTest {
    @Test fun markerAndChapterArrayExactlyMatchBackendV1() {
        val markers = JSONArray(
            """[{"mediaMarkerKindTypeId":1,"startMs":15200,"endMs":95000},
                {"mediaMarkerKindTypeId":2,"startMs":1280000,"endMs":1350000},
                {"mediaMarkerKindTypeId":3,"startMs":0,"endMs":12000},
                {"mediaMarkerKindTypeId":4,"startMs":1350000,"endMs":1370000}]"""
        ).toTvExtrasList { tvPlaybackMarker() }
        assertEquals(listOf(1, 2, 3, 4), markers.map { it.mediaMarkerKindTypeId }.sorted())
        assertTrue(markers[0].contains(15200))
        assertFalse(markers[0].contains(95000))
        assertEquals(4, markers.size)

        val chapters = JSONArray(
            """[{"chapterIndex":0,"startMs":0,"endMs":60000,"title":null},
               {"chapterIndex":1,"startMs":60000,"endMs":1400000,"title":"Episode"}]"""
        ).toTvExtrasList { tvPlaybackChapter() }
        assertNull(chapters[0].title)
        assertEquals("Episode", chapters[1].title)
        assertEquals(60000L, chapters[1].startMs)
        assertTrue(runCatching { JSONObject("""{"chapterIndex":0,"startMs":20,"endMs":20,"title":null}""").tvPlaybackChapter() }.isFailure)
    }

    @Test fun trickplayGeometryUsesRealFramesAndClampsAtLastAvailableFrame() {
        val info = JSONObject(
            """{"intervalSeconds":10,"frames":73,"columns":5,"rows":4,"tileWidth":160,"tileHeight":90}"""
        ).tvTrickplay()
        assertEquals(20, info.framesPerSheet)
        assertEquals(4, info.sheetCount)
        assertEquals(TvV1TrickplayTile(1, 0, 0, 0, 160, 90), info.previewAt(0))
        assertEquals(TvV1TrickplayTile(2, 21, 160, 0, 160, 90), info.previewAt(215_000))
        assertEquals(TvV1TrickplayTile(4, 72, 320, 180, 160, 90), info.previewAt(10_000_000))
        assertTrue(runCatching { info.previewAt(-1) }.isFailure)
        assertTrue(runCatching { JSONObject("""{"intervalSeconds":0,"frames":8,"columns":5,"rows":4,"tileWidth":160,"tileHeight":90}""").tvTrickplay() }.isFailure)
    }

    @Test fun embeddedAndSidecarTracksPreserveServerSupportedAndSelectedFlags() {
        val tracks = JSONArray(
            """[{"key":"s1","mediaTrackTypeId":2,"language":"ja","title":null,"channels":2,
                 "isDefault":true,"isForced":false,"isSupported":true,"isSelected":true},
                {"key":"s2","mediaTrackTypeId":3,"language":"de","title":"German ASS","channels":null,
                 "isDefault":false,"isForced":false,"isSupported":true,"isSelected":false},
                {"key":"f789","mediaTrackTypeId":3,"language":"en","title":null,"channels":null,
                 "isDefault":false,"isForced":true,"isSupported":true,"isSelected":true},
                {"key":"s5","mediaTrackTypeId":3,"language":null,"title":"PGS","channels":null,
                 "isDefault":false,"isForced":false,"isSupported":false,"isSelected":false}]"""
        ).toTvExtrasList { tvPlaybackTrack() }
        assertTrue(tracks[0].isAudio)
        assertTrue(tracks[2].isExternalSidecar)
        assertTrue(tracks[2].isSelected)
        assertFalse(tracks[3].isSupported)
        assertNull(tracks[3].language)
        assertTrue(runCatching { JSONObject("""{"key":"../foo","mediaTrackTypeId":3,"language":null,"title":null,"channels":null,"isDefault":false,"isForced":false,"isSupported":true,"isSelected":false}""").tvPlaybackTrack() }.isFailure)
    }

    @Test fun preferencesRoundTripDoesNotPersistReadOnlyProfileOrDeviceSettings() {
        val prefs = JSONObject(
            """{"audioLanguage":"ja","subtitleLanguage":null,"subtitleModeTypeId":4,
                "speed":1.25,"autoplayNext":true,"introSkipModeTypeId":2,"outroSkipModeTypeId":3,
                "autoplayPreviews":false,"profileLanguage":"de","deviceQuality":3}"""
        ).tvPlaybackPreferences()
        assertEquals("ja", prefs.audioLanguage)
        assertNull(prefs.subtitleLanguage)
        assertEquals(BigDecimal("1.25"), prefs.speed)
        assertEquals("de", prefs.profileLanguage)
        assertEquals(3, prefs.deviceQuality)
        val update = prefs.editable().toJson()
        assertEquals(8, update.length())
        assertFalse(update.has("profileLanguage"))
        assertFalse(update.has("deviceQuality"))
        assertTrue(update.isNull("subtitleLanguage"))
        assertEquals(3, update.getInt("outroSkipModeTypeId"))
        assertTrue(runCatching { prefs.editable().copy(speed = BigDecimal("4.01")) }.isFailure)
        assertTrue(runCatching { prefs.editable().copy(speed = BigDecimal("1.234")) }.isFailure)
        assertTrue(runCatching { prefs.editable().copy(subtitleModeTypeId = 5) }.isFailure)
    }
}
