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
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import de.juloc.jularr.core.model.DiscoverItemV1
import de.juloc.jularr.core.model.DiscoverSuggestionV1

/**
 * The search of the television: the field with the text of the search on the left, the
 * suggestions of the instance under it and the titles of the instance on the right. The
 * standard keyboard of the television types into the field; with an empty search the right side
 * shows the trending titles of the instance.
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

    Row(modifier = Modifier.fillMaxSize()) {
        // The field and the suggestions of the instance stand on the left.
        Column(
            modifier = Modifier
                .width(SearchColumnWidth)
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

            // The suggestions of the instance stand under the field.
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
                    columns = GridCells.Fixed(4),
                    horizontalArrangement = Arrangement.spacedBy(TvLayout.Gap),
                    verticalArrangement = Arrangement.spacedBy(TvLayout.Gap),
                ) {
                    items(titles, key = { "${it.workId ?: it.tmdbId}-${it.title}" }) { item ->
                        TvCardView(
                            item.toTvCard(controller.texts),
                            controller,
                            "trending",
                            TvFocusArea.Search,
                        )
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

/**
 * The field with the text of the search, the microphone and the clear button. The field takes
 * the focus when the search opens, so the standard keyboard of the television types into it.
 */
@Composable
private fun TvSearchField(
    controller: TvController,
    onVoice: () -> Unit,
    voiceAvailable: Boolean,
) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    val requester = remember { FocusRequester() }
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(TvLayout.ControlRadius)

    // The field takes the focus when the search opens, so the keyboard of the television is
    // there right away.
    LaunchedEffect(Unit) {
        runCatching { requester.requestFocus() }
    }

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(TvLayout.TopRowHeight)
            .clip(shape)
            .background(colors.surface)
            .border(
                width = if (focused) TvLayout.FocusRing * 2 else 1.dp,
                color = if (focused) colors.accent else colors.border,
                shape = shape,
            )
            .padding(start = 18.dp, end = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(modifier = Modifier.weight(1f)) {
            if (controller.searchQuery.isEmpty()) {
                Text(
                    text = controller.texts["tv.search.placeholder"],
                    fontSize = type.body,
                    fontWeight = FontWeight.Medium,
                    color = colors.muted,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            BasicTextField(
                value = controller.searchQuery,
                onValueChange = { controller.searchTextChanged(it) },
                singleLine = true,
                textStyle = TextStyle(
                    color = colors.text,
                    fontSize = type.body,
                    fontWeight = FontWeight.Medium,
                ),
                cursorBrush = SolidColor(colors.accent),
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                keyboardActions = KeyboardActions(
                    onSearch = { controller.back() },
                    onDone = { controller.back() },
                ),
                modifier = Modifier
                    .fillMaxWidth()
                    .focusRequester(requester)
                    .onFocusChanged {
                        focused = it.isFocused
                        if (it.isFocused) {
                            controller.focusMovedTo(TvFocusArea.Search)
                        }
                    },
            )
        }
        if (controller.searchQuery.isNotEmpty()) {
            TvSmallButton(controller, "×", onClick = { controller.clearSearch() })
        }
        if (voiceAvailable) {
            TvSmallButton(controller, "", onClick = onVoice) {
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
            .reportFocus {
                focused = it
                if (it) {
                    controller.focusMovedTo(TvFocusArea.Search)
                }
            }
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
            ) { controller.openSuggestion(suggestion) }
            .padding(horizontal = 16.dp, vertical = 10.dp),
    )
}

/** A small button of the television: the clear button and the microphone of the search. */
@Composable
private fun TvSmallButton(
    controller: TvController,
    label: String,
    onClick: () -> Unit,
    content: @Composable (() -> Unit)? = null,
) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(TvLayout.RailItemRadius)
    Box(
        modifier = Modifier
            .size(TvLayout.AvailabilitySymbol + 12.dp)
            .clip(shape)
            .background(colors.surface, shape)
            .border(1.dp, colors.border, shape)
            .reportFocus {
                focused = it
                if (it) {
                    controller.focusMovedTo(TvFocusArea.Search)
                }
            }
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

/** The width of the column with the field and the suggestions. */
private val SearchColumnWidth = 260.dp
