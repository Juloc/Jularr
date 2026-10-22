package de.juloc.jularr.tv

import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material3.Icon
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import coil3.compose.AsyncImage

/**
 * The details of a title: the backdrop, the name, the year, the genres and the text of the
 * instance. The actions of the title come with the player.
 */
@Composable
fun TvDetailsScreen(controller: TvController) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    val details = controller.details
    if (details == null) {
        Text(
            text = controller.texts["tv.details.loading"],
            fontSize = type.body,
            color = colors.muted,
        )
        return
    }

    Column(modifier = Modifier.fillMaxSize()) {
        // The backdrop of the desktop.
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(TvLayout.HeroHeight)
                .clip(RoundedCornerShape(TvLayout.HeroRadius))
                .background(colors.field),
        ) {
            if (details.backdropUrl != null) {
                AsyncImage(
                    model = details.backdropUrl,
                    contentDescription = details.canonicalTitle,
                    contentScale = ContentScale.Crop,
                    modifier = Modifier.fillMaxSize(),
                )
            }
        }
        Spacer(Modifier.height(TvLayout.Gap))
        Text(
            text = details.canonicalTitle,
            fontSize = type.heroTitle,
            fontWeight = FontWeight.Bold,
            color = colors.text,
        )
        Spacer(Modifier.height(4.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(TvLayout.Gap)) {
            details.releaseYear?.let { year ->
                Text(text = year.toString(), fontSize = type.body, color = colors.muted)
            }
            details.genres?.let { genres ->
                Text(text = genres, fontSize = type.body, color = colors.muted)
            }
            details.studio?.let { studio ->
                Text(text = studio, fontSize = type.body, color = colors.muted)
            }
        }
        Spacer(Modifier.height(TvLayout.Gap))
        Text(
            text = details.overview.orEmpty(),
            fontSize = type.body,
            color = colors.text,
            modifier = Modifier.width(TvLayout.CardWidth * 4),
        )
        Spacer(Modifier.height(TvLayout.Gap))
        // The collections of the profile: the viewer puts the title into one of them.
        if (details.id > 0) {
            Text(
                text = controller.texts["collections.title"],
                fontSize = type.body,
                fontWeight = FontWeight.SemiBold,
                color = colors.muted,
            )
            LazyColumn(modifier = Modifier.height(TvLayout.CardWidth * 2)) {
                items(controller.collections, key = { it.id }) { collection ->
                    TvCollectionCheck(
                        controller = controller,
                        title = collection.name,
                        workId = details.id,
                    )
                }
            }
        }
    }
}

/** One collection of the profile with the mark of the title. */
@Composable
private fun TvCollectionCheck(controller: TvController, title: String, workId: Long) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(TvLayout.RailItemRadius)
    val collection = controller.collections.firstOrNull { it.name == title }
    val member = controller.collectionWorks.any { it.workId == workId }
    val requester = rememberTvFocusEntry(controller, TvFocusArea.List, "collection-check-$title")
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(TvLayout.RailItemHeight)
            .clip(shape)
            .background(colors.surface, shape)
            .focusRequester(requester)
            .reportFocus {
                focused = it
                if (it) {
                    controller.focusMovedTo(TvFocusArea.List)
                    controller.focusMovedTo(requester)
                } else if (controller.focusedEntry == requester) {
                    controller.focusMovedTo(null)
                }
            }
            .then(
                if (focused) {
                    Modifier.tvFocusRing(true, colors.focus, shape)
                } else {
                    Modifier
                },
            )
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
            ) {
                collection?.let { controller.toggleCollection(it, workId, !member) }
            }
            .padding(horizontal = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Text(
            text = title,
            fontSize = type.body,
            color = if (focused) colors.accentText else colors.text,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        if (member) {
            Icon(
                imageVector = Icons.Filled.Check,
                contentDescription = null,
                tint = colors.accentText,
                modifier = Modifier.size(TvLayout.AvailabilitySymbol * 0.55f),
            )
        }
    }
}
