package de.juloc.jularr.tv

import de.juloc.jularr.core.model.AnimeDetail
import de.juloc.jularr.core.model.ContinueWatchingItem
import de.juloc.jularr.core.model.EpisodeSummary
import de.juloc.jularr.core.model.Season
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class TvAnimeDetailScreenTest {
    private val episode = EpisodeSummary(
        id = "episode-4",
        seasonNumber = 1,
        number = 4,
        title = "Chapter 4",
        hasMedia = true,
        hasJapaneseLearningSubtitle = false,
    )
    private val anime = AnimeDetail(
        id = "anime-1",
        title = "Test",
        localTitle = "Test",
        nativeTitle = null,
        description = null,
        coverImageUrl = null,
        bannerImageUrl = null,
        seasonYear = 2026,
        format = "TV",
        seasons = listOf(Season(1, listOf(episode))),
    )
    private fun progress(
        episodeId: String = "episode-4",
        animeId: String = "anime-1",
        percent: Int = 73,
    ) = ContinueWatchingItem(
        kind = "episode",
        episodeId = episodeId,
        animeId = animeId,
        animeTitle = "Test",
        seasonNumber = 1,
        episodeNumber = 4,
        episodeTitle = "Chapter 4",
        resumePositionMs = 730_000L,
        durationMs = 1_000_000L,
        percent = percent,
        updatedAtUtc = "2026-10-09T12:00:00Z",
        coverImageUrl = null,
    )

    @Test
    fun resumeRequiresSameWorkAndPlayableEpisode() {
        assertEquals(73, currentAnimeResume(anime, listOf(progress()))?.percent)
        assertNull(currentAnimeResume(anime, listOf(progress(animeId = "another"))))
        assertNull(currentAnimeResume(anime, listOf(progress(episodeId = "missing"))))
        assertNull(currentAnimeResume(anime.copy(seasons = listOf(Season(1, listOf(episode.copy(hasMedia = false))))), listOf(progress())))
    }

    @Test
    fun completedOrUnstartedEpisodesHaveNoResumeAction() {
        assertNull(currentAnimeResume(anime, listOf(progress(percent = 0))))
        assertNull(currentAnimeResume(anime, listOf(progress(percent = 100))))
    }
}
