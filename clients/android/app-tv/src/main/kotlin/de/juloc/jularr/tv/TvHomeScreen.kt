package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import coil3.compose.AsyncImage

/**
 * The home page of the television: the type capsule of the desktop and the shelves of the
 * profile in its order.
 */
@Composable
fun TvHomeScreen(controller: TvController) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        verticalArrangement = Arrangement.spacedBy(TvLayout.Gap),
    ) {
        // The row of media types, as on the desktop.
        if (controller.scopes.isNotEmpty()) {
            item {
                LazyRow(horizontalArrangement = Arrangement.spacedBy(TvLayout.Gap)) {
                    items(controller.scopes) { scope ->
                        TvCapsule(controller, controller.texts["discover.type." + scope.slug])
                    }
                }
            }
        }

        if (controller.shelves.isEmpty()) {
            item {
                Text(
                    text = controller.texts["tv.empty.home"],
                    fontSize = type.body,
                    color = colors.muted,
                )
            }
        } else {
            items(controller.shelves, key = { it.key }) { shelf ->
                TvShelfRow(controller, shelf)
            }
        }
    }
}

/** One entry of the type capsule. */
@Composable
private fun TvCapsule(controller: TvController, title: String) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(TvLayout.RailItemRadius)
    Text(
        text = title,
        fontSize = type.body,
        fontWeight = FontWeight.Medium,
        color = if (focused) colors.accentText else colors.muted,
        modifier = Modifier
            .background(if (focused) colors.field else colors.surface, shape)
            .clip(shape)
            .reportFocus { focused = it }
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
            ) { }
            .then(
                if (focused) {
                    Modifier.tvFocusRing(true, colors.focus, shape)
                } else {
                    Modifier
                },
            )
            .padding(horizontal = 16.dp, vertical = 8.dp),
    )
}

/** One shelf of the home page: its heading and its cards. */
@Composable
fun TvShelfRow(controller: TvController, shelf: TvShelf) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    Column {
        Text(
            text = shelf.title,
            fontSize = type.shelfHeading,
            fontWeight = FontWeight.Bold,
            color = colors.text,
            modifier = Modifier.padding(bottom = 6.dp),
        )
        LazyRow(horizontalArrangement = Arrangement.spacedBy(TvLayout.Gap)) {
            items(shelf.cards, key = { it.id }) { card ->
                TvCardView(card)
            }
        }
    }
}

/**
 * One card of the desktop: the poster, the title and the line below it. The focused card
 * shows its ring.
 */
@Composable
fun TvCardView(card: TvCard) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(TvLayout.CardRadius)
    Column(
        modifier = Modifier
            .width(TvLayout.CardWidth)
            .reportFocus { focused = it }
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
            ) { }
            .then(
                if (focused) {
                    Modifier.tvFocusRing(true, colors.focus, shape)
                } else {
                    Modifier
                },
            ),
    ) {
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(TvLayout.CardHeight)
                .clip(shape)
                .background(colors.field),
        ) {
            if (card.posterUrl != null) {
                AsyncImage(
                    model = card.posterUrl,
                    contentDescription = card.title,
                    contentScale = ContentScale.Crop,
                    modifier = Modifier.fillMaxWidth().height(TvLayout.CardHeight),
                )
            }
            // The progress of the desktop sits at the lower edge of the cover.
            card.progress?.let { progress ->
                Box(
                    modifier = Modifier
                        .align(Alignment.BottomStart)
                        .fillMaxWidth()
                        .height(4.dp)
                        .background(colors.field),
                ) {
                    Box(
                        modifier = Modifier
                            .fillMaxWidth(progress)
                            .height(4.dp)
                            .background(colors.accent),
                    )
                }
            }
        }
        Spacer(Modifier.height(6.dp))
        Text(
            text = card.title,
            fontSize = type.cardTitle,
            fontWeight = FontWeight.SemiBold,
            color = colors.text,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
            lineHeight = type.cardTitle * 1.3f,
        )
        card.meta?.let { meta ->
            Text(
                text = meta,
                fontSize = type.cardMeta,
                color = colors.muted,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

/** A list of cards, as the watchlist and the library show it. */
@Composable
fun TvCardGrid(title: String, cards: List<TvCard>, empty: String) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        verticalArrangement = Arrangement.spacedBy(TvLayout.Gap),
    ) {
        item {
            Text(
                text = title,
                fontSize = type.shelfHeading,
                fontWeight = FontWeight.Bold,
                color = colors.text,
            )
        }
        if (cards.isEmpty()) {
            item {
                Text(text = empty, fontSize = type.body, color = colors.muted)
            }
        } else {
            items(cards.chunked(5).withIndex().toList(), key = { it.index }) { (_, row) ->
                LazyRow(horizontalArrangement = Arrangement.spacedBy(TvLayout.Gap)) {
                    items(row, key = { it.id }) { card ->
                        TvCardView(card)
                    }
                }
            }
        }
    }
}
