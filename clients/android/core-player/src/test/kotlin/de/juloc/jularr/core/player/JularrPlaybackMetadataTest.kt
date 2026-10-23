package de.juloc.jularr.core.player

import de.juloc.jularr.core.model.PlayerEpisode
import org.junit.Assert.assertEquals
import org.junit.Test

class JularrPlaybackMetadataTest {
    @Test
    fun playerEpisodeMapsToStableMediaSessionMetadata() {
        val metadata = PlayerEpisode(
            id = "episode-id",
            animeId = "anime-id",
            animeTitle = "Frieren",
            title = "The Journey Continues",
            seasonNumber = 2,
            number = 3,
        ).toPlaybackMetadata()

        assertEquals("episode-id", metadata.mediaId)
        assertEquals("The Journey Continues", metadata.title)
        assertEquals("Frieren", metadata.seriesTitle)
        assertEquals("S02 E03", metadata.episodeLabel)
    }
}
