package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.api.playbackextras.TvV1PlaybackChapter

/** Chapter menu attached to the original TV player's existing settings panel. */
@Composable
internal fun TvV1ChapterPanel(
    chapters: List<TvV1PlaybackChapter>,
    onSeekTo: (TvV1PlaybackChapter) -> Unit,
    onBack: () -> Unit,
) {
    Box(
        modifier = Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.24f))
            .padding(end = 38.dp, top = 65.dp, bottom = 65.dp),
        contentAlignment = Alignment.CenterEnd,
    ) {
        Column(
            modifier = Modifier.widthIn(min = 380.dp, max = 440.dp)
                .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(18.dp))
                .padding(22.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Text("Kapitel", style = MaterialTheme.typography.headlineSmall)
            LazyColumn(
                verticalArrangement = Arrangement.spacedBy(8.dp),
                modifier = Modifier.weight(1f, fill = false),
            ) {
                items(chapters, key = { it.chapterIndex }) { chapter ->
                    Button(
                        modifier = Modifier.fillMaxWidth(),
                        onClick = { onSeekTo(chapter) },
                    ) {
                        Text(
                            (chapter.title?.takeIf(String::isNotBlank)
                                ?: "Kapitel ${chapter.chapterIndex + 1}") +
                                " · ${formatTvChapterTime(chapter.startMs)}",
                        )
                    }
                }
            }
            Button(onClick = onBack) { Text("Zurück") }
        }
    }
}

internal fun formatTvChapterTime(positionMs: Long): String {
    val seconds = positionMs.coerceAtLeast(0) / 1000
    val hours = seconds / 3600
    val minutes = seconds / 60 % 60
    val remainder = seconds % 60
    return if (hours > 0) "%d:%02d:%02d".format(hours, minutes, remainder)
        else "%02d:%02d".format(minutes, remainder)
}
