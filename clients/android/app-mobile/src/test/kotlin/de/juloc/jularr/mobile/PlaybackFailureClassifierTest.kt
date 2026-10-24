package de.juloc.jularr.mobile

import androidx.media3.common.PlaybackException
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PlaybackFailureClassifierTest {
    @Test
    fun codecFailureCanUseServerFallback() {
        assertTrue(
            PlaybackFailureClassifier.shouldUseServerFallback(
                PlaybackException.ERROR_CODE_DECODER_INIT_FAILED,
            ),
        )
    }

    @Test
    fun storageOrNetworkFailureIsNotCodecFallback() {
        assertFalse(
            PlaybackFailureClassifier.shouldUseServerFallback(
                PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED,
            ),
        )
        assertTrue(
            PlaybackFailureClassifier.isNetworkFailure(
                PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED,
            ),
        )
    }
}
