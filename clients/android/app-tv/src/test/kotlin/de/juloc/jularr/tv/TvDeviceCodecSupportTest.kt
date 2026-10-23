package de.juloc.jularr.tv

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class TvDeviceCodecSupportTest {
    @Test
    fun mapsCommonVideoCodecsToAndroidMimeTypes() {
        assertEquals("video/avc", TvDeviceCodecSupport.videoMime("h264"))
        assertEquals("video/hevc", TvDeviceCodecSupport.videoMime("HEVC"))
        assertEquals("video/av01", TvDeviceCodecSupport.videoMime("av1"))
        assertEquals("video/x-vnd.on2.vp9", TvDeviceCodecSupport.videoMime("vp9"))
        assertNull(TvDeviceCodecSupport.videoMime("unknown"))
    }

    @Test
    fun mapsCommonAudioCodecsToAndroidMimeTypes() {
        assertEquals("audio/mp4a-latm", TvDeviceCodecSupport.audioMime("aac"))
        assertEquals("audio/opus", TvDeviceCodecSupport.audioMime("opus"))
        assertEquals("audio/eac3", TvDeviceCodecSupport.audioMime("eac3"))
        assertNull(TvDeviceCodecSupport.audioMime("unknown"))
    }
}
