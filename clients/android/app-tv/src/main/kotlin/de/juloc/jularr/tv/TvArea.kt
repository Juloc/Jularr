package de.juloc.jularr.tv

import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MenuBook
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.SportsEsports
import androidx.compose.ui.graphics.vector.ImageVector

/**
 * The areas of the Discover, as the desktop groups them: the viewer chooses an area and the
 * shelves of the instance follow. Every area stands without a choice.
 */
enum class TvArea(val key: String) {
    Watch("watch"),
    Read("read"),
    Hear("hear"),
    Play("play"),
    ;

    /** The sign of the area, as the capsules of the desktop show it. */
    fun icon(): ImageVector = when (this) {
        Watch -> Icons.Filled.PlayArrow
        Read -> Icons.Filled.MenuBook
        Hear -> Icons.Filled.MusicNote
        Play -> Icons.Filled.SportsEsports
    }

    companion object {
        /** The area a type of the instance belongs to. */
        fun of(slug: String): TvArea = when (slug) {
            "film", "series", "anime" -> Watch
            "book", "manga", "lightnovel" -> Read
            "music", "audiobook" -> Hear
            "game" -> Play
            else -> Watch
        }
    }
}
