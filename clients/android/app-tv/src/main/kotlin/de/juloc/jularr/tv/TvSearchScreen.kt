package de.juloc.jularr.tv

import android.content.Intent
import android.speech.RecognizerIntent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.input.key.onKeyEvent
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import de.juloc.jularr.core.model.DiscoverItemV1
import de.juloc.jularr.core.model.DiscoverSuggestionV1

/**
 * The search of the television: the field and the keyboard of the television on the left, the
 * suggestions of the instance under the keyboard and the titles of the instance on the right.
 * With an empty search the right side shows the trending titles.
 */
@Composable
fun TvSearchScreen(controller: TvController) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    val context = LocalContext.current

    // The voice of the television: the system hears the words and they enter the field.
    val voice = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        val spoken = result.data
            ?.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS)
            ?.firstOrNull()
        if (!spoken.isNullOrBlank()) {
            controller.heard(spoken)
        }
    }
    val voiceAvailable = remember {
        Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).resolveActivity(context.packageManager) != null
    }

    Row(
        modifier = Modifier
            .fillMaxSize()
            // A hardware keyboard types into the field as well.
            .onKeyEvent { event ->
                val native = event.nativeKeyEvent
                if (native.action == android.view.KeyEvent.ACTION_DOWN &&
                    native.unicodeChar != 0 &&
                    native.keyCode != android.view.KeyEvent.KEYCODE_ENTER &&
                    native.keyCode != android.view.KeyEvent.KEYCODE_BACK
                ) {
                    controller.type(native.unicodeChar.toString())
                    true
                } else {
                    false
                }
            },
    ) {
        // The field, the keyboard and the suggestions under it stand on the left.
        Column(
            modifier = Modifier
                .width(KeyboardWidth)
                .fillMaxHeight()
                .padding(end = TvLayout.Gap),
            verticalArrangement = Arrangement.spacedBy(TvLayout.Gap),
        ) {
            TvSearchField(
                controller = controller,
                onVoice = {
                    voice.launch(
                        Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
                            putExtra(
                                RecognizerIntent.EXTRA_LANGUAGE_MODEL,
                                RecognizerIntent.LANGUAGE_MODEL_FREE_FORM,
                            )
                        },
                    )
                },
                voiceAvailable = voiceAvailable,
            )

            // The keyboard of the television: narrow like the keyboard of a telephone, its
            // letters on the left and its actions on the right.
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                LetterKeys.chunked(LetterColumns).forEach { row ->
                    Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                        row.forEach { key ->
                            TvKey(
                                controller = controller,
                                label = key,
                                onClick = { controller.type(key) },
                                modifier = Modifier.width(KeyWidth),
                            )
                        }
                    }
                }
                DigitKeys.chunked(LetterColumns).forEach { row ->
                    Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                        row.forEach { key ->
                            TvKey(
                                controller = controller,
                                label = key,
                                onClick = { controller.type(key) },
                                modifier = Modifier.width(KeyWidth),
                            )
                        }
                    }
                }
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    ActionKeys.forEach { key ->
                        TvKey(
                            controller = controller,
                            label = key,
                            onClick = { controller.type(key) },
                            modifier = Modifier.width(ActionKeyWidth),
                        )
                    }
                }
            }

            // The suggestions of the instance stand under the keyboard.
            if (controller.searchSuggestions.isNotEmpty()) {
                LazyColumn(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    items(controller.searchSuggestions, key = { it.externalId }) { suggestion ->
                        TvSuggestionView(controller, suggestion)
                    }
                }
            }
        }

        // The titles of the instance stand on the right: the trending ones until the viewer
        // types, then the titles that match.
        Column(
            modifier = Modifier.weight(1f).fillMaxHeight(),
            verticalArrangement = Arrangement.spacedBy(TvLayout.Gap),
        ) {
            val titles = if (controller.searchResults.isNotEmpty()) {
                controller.searchResults
            } else {
                controller.trending
            }
            if (titles.isNotEmpty()) {
                Text(
                    text = controller.texts[
                        if (controller.searchResults.isNotEmpty()) "tv.search.results" else "tv.search.trending"
                    ],
                    fontSize = type.shelfHeading,
                    fontWeight = FontWeight.Bold,
                    color = colors.text,
                )
                LazyVerticalGrid(
                    columns = GridCells.Fixed(3),
                    horizontalArrangement = Arrangement.spacedBy(TvLayout.Gap),
                    verticalArrangement = Arrangement.spacedBy(TvLayout.Gap),
                ) {
                    items(titles, key = { "${it.workId ?: it.tmdbId}-${it.title}" }) { item ->
                        TvCardView(item.toTvCard(controller.texts), controller)
                    }
                }
            } else if (controller.searchQuery.isNotEmpty()) {
                Text(
                    text = controller.texts["tv.search.empty"],
                    fontSize = type.body,
                    color = colors.muted,
                )
            }
        }
    }
}

/** The field with the text of the search, the microphone and the clear button. */
@Composable
private fun TvSearchField(
    controller: TvController,
    onVoice: () -> Unit,
    voiceAvailable: Boolean,
) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(TvLayout.TopRowHeight)
            .clip(RoundedCornerShape(TvLayout.TopRowRadius))
            .background(colors.surface)
            .border(1.dp, colors.border, RoundedCornerShape(TvLayout.TopRowRadius))
            .padding(horizontal = 18.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text = controller.searchQuery.ifEmpty { controller.texts["tv.search.placeholder"] },
            fontSize = type.body,
            fontWeight = FontWeight.Medium,
            color = if (controller.searchQuery.isEmpty()) colors.muted else colors.text,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f),
        )
        if (controller.searchQuery.isNotEmpty()) {
            TvKey(
                controller = controller,
                label = "×",
                onClick = { controller.clearSearch() },
                modifier = Modifier.width(48.dp),
            )
        }
        if (voiceAvailable) {
            TvKey(
                controller = controller,
                label = "",
                onClick = onVoice,
                modifier = Modifier.width(48.dp),
            ) {
                Icon(
                    imageVector = Icons.Filled.Mic,
                    contentDescription = controller.texts["tv.search.voice"],
                    tint = colors.muted,
                    modifier = Modifier.size(TvLayout.AvailabilitySymbol),
                )
            }
        }
    }
}

/** One suggestion of the instance. */
@Composable
private fun TvSuggestionView(controller: TvController, suggestion: DiscoverSuggestionV1) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(TvLayout.RailItemRadius)
    Text(
        text = suggestion.title,
        fontSize = type.body,
        color = if (focused) colors.accentText else colors.muted,
        maxLines = 1,
        overflow = TextOverflow.Ellipsis,
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(colors.surface, shape)
            .reportFocus { focused = it; if (focused) controller.focusMovedTo(TvFocusArea.Content) }
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
            ) { controller.openSuggestion(suggestion) }
            .padding(horizontal = 16.dp, vertical = 10.dp),
    )
}

/** One key of the keyboard of the television. */
@Composable
private fun TvKey(
    controller: TvController,
    label: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    content: @Composable (() -> Unit)? = null,
) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(TvLayout.RailItemRadius)
    Box(
        modifier = modifier
            .height(TvLayout.RailItemHeight)
            .clip(shape)
            .background(colors.surface, shape)
            .border(1.dp, colors.border, shape)
            .reportFocus { focused = it; if (focused) controller.focusMovedTo(TvFocusArea.Content) }
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
            ) { onClick() },
        contentAlignment = Alignment.Center,
    ) {
        if (content != null) {
            content()
        } else {
            Text(
                text = label,
                fontSize = type.body,
                fontWeight = FontWeight.Medium,
                color = if (focused) colors.accentText else colors.text,
            )
        }
    }
}

/** The letters of the keyboard in the German layout. */
private val LetterKeys = listOf(
    "q", "w", "e", "r", "t", "z", "u", "i", "o", "p",
    "a", "s", "d", "f", "g", "h", "j", "k", "l",
    "y", "x", "c", "v", "b", "n", "m",
)

/** The digits of the keyboard. */
private val DigitKeys = listOf("1", "2", "3", "4", "5", "6", "7", "8", "9", "0")

/** The actions of the keyboard: the space, the backspace and the enter. */
private val ActionKeys = listOf("␣", "⌫", "↵")

/** The width of the keyboard of the television. */
private val KeyboardWidth = 260.dp

/** The columns of the keyboard: narrow like the keyboard of a telephone. */
private val LetterColumns = 5

/** The width of one key. */
private val KeyWidth = 48.dp

/** The width of one action key; the space is wider. */
private val ActionKeyWidth = 80.dp

/** Reads a search result as a card of the desktop. */
fun DiscoverItemV1.toTvCard(texts: TvTexts): TvCard = TvCard(
    id = if (workId != null) "work-$workId" else "tmdb-$tmdbId",
    title = title,
    posterUrl = posterUrl,
    meta = texts["media.state." + mediaStateName(mediaStateTypeId)],
    progress = null,
    availabilityStateId = null,
)

/** The name of the state of a work, as the texts of the server know it. */
private fun mediaStateName(mediaStateTypeId: Int): String = when (mediaStateTypeId) {
    1 -> "NotRequested"
    2 -> "Requested"
    3 -> "Available"
    else -> "NotRequested"
}
