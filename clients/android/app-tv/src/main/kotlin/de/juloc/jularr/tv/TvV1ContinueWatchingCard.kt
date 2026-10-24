package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Text
import de.juloc.jularr.core.api.catalog.TvContinueItemV1

/**
 * Original TV Continue Watching card style, fed from the actual V1 asset+work DTO.
 * Does not cast MediaAssetId into EpisodeId or make up a season/episode number.
 */
@Composable
internal fun TvV1ContinueWatchingCard(
    item: TvContinueItemV1,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    focusRequester: FocusRequester,
    onFocused: () -> Unit,
    onClick: () -> Unit,
) {
    val focusColor = rememberTvFocusColor()
    var focused by remember(item.mediaAssetId) { mutableStateOf(false) }
    val shape = RoundedCornerShape(14.dp)
    val ratio = item.durationMs?.takeIf { it > 0 }?.let {
        (item.positionMs.toDouble() / it.toDouble()).toFloat().coerceIn(0f, 1f)
    }
    val episodeHint = listOfNotNull(
        item.parentUnitNumber?.stripTrailingZeros()?.toPlainString()?.let { "S$it" },
        item.unitNumber?.stripTrailingZeros()?.toPlainString()?.let { "E$it" },
    ).joinToString(" ")

    Box(
        modifier = Modifier
            .width(280.dp)
            .clip(shape)
            .background(if (focused) focusColor.copy(alpha = 0.28f) else Color(0xFF121520))
            .tvFocusIndication(focused, focusColor, shape)
            .focusRequester(focusRequester)
            .clickable(onClick = onClick)
            .reportFocus {
                focused = it
                if (it) onFocused()
            },
    ) {
        Column(modifier = Modifier.fillMaxWidth()) {
            TvArtwork(
                url = item.posterUrl,
                serverOrigin = serverOrigin,
                requestHeaders = requestHeaders,
                contentDescription = item.title,
                modifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f)
                    .clip(RoundedCornerShape(topStart = 14.dp, topEnd = 14.dp)),
            )
            Column(
                modifier = Modifier.fillMaxWidth().padding(12.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                Text(
                    text = item.title,
                    style = TextStyle(color = Color.White, fontSize = 15.sp, fontWeight = FontWeight.Bold),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    text = listOfNotNull(
                        episodeHint.takeIf { it.isNotBlank() },
                        "Fortsetzen bei ${formatEpisodePosition(item.positionMs)}",
                    ).joinToString(" · "),
                    style = TextStyle(color = Color.White.copy(alpha = 0.65f), fontSize = 12.sp),
                    maxLines = 1,
                )
                if (ratio != null) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        Box(
                            modifier = Modifier.weight(1f).height(4.dp)
                                .clip(RoundedCornerShape(2.dp))
                                .background(Color(0xFF2A2E3D)),
                        ) {
                            Box(
                                modifier = Modifier.fillMaxHeight().fillMaxWidth(ratio)
                                    .background(focusColor),
                            )
                        }
                        Text(
                            text = "${(ratio * 100f).toInt()}%",
                            style = TextStyle(color = Color.White.copy(alpha = 0.65f), fontSize = 11.sp),
                        )
                    }
                }
            }
        }
    }
}
