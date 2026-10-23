package de.juloc.jularr.tv

import androidx.media3.common.C
import de.juloc.jularr.core.model.MediaTrack
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

// Scores plain track attributes instead of Media3 Format instances: Format.Builder().build()
// calls Android framework stubs that throw "not mocked" on the plain JVM.
class TvMediaTrackSelectorTest {
    private val requested = MediaTrack(
        id = "stream:2",
        streamIndex = 2,
        kind = "audio",
        codec = "aac",
        language = "jpn",
        title = "Japanese",
        isDefault = true,
        isForced = false,
        isText = false,
    )

    @Test
    fun exactLanguageAndLabelOutscoreCodecOnlyMatch() {
        val exact = TvMediaTrackSelector.score(
            language = "jpn",
            label = "Japanese",
            sampleMimeType = "audio/mp4a-latm",
            selectionFlags = C.SELECTION_FLAG_DEFAULT,
            requested = requested,
        )
        val codecOnly = TvMediaTrackSelector.score(
            language = "eng",
            label = "English",
            sampleMimeType = "audio/mp4a-latm",
            selectionFlags = 0,
            requested = requested,
        )

        assertTrue(exact > codecOnly)
    }

    @Test
    fun scoreAddsLanguageLabelCodecAndDefaultWeights() {
        val exact = TvMediaTrackSelector.score(
            language = "JPN",
            label = "japanese",
            sampleMimeType = "audio/mp4a-latm",
            selectionFlags = C.SELECTION_FLAG_DEFAULT,
            requested = requested,
        )
        val codecOnly = TvMediaTrackSelector.score(
            language = "eng",
            label = "English",
            sampleMimeType = "audio/mp4a-latm",
            selectionFlags = 0,
            requested = requested,
        )
        val nothing = TvMediaTrackSelector.score(
            language = null,
            label = null,
            sampleMimeType = null,
            selectionFlags = 0,
            requested = requested,
        )

        assertEquals(6 + 5 + 3 + 1, exact)
        assertEquals(3, codecOnly)
        assertEquals(0, nothing)
    }
}
