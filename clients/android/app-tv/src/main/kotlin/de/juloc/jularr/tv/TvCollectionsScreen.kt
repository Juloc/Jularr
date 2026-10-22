package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
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
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import de.juloc.jularr.core.model.CollectionV1

/**
 * The collections of the profile: the favourites of the viewer. A collection opens its titles
 * as the cards of the desktop; the viewer creates one, changes its name or removes it.
 */
@Composable
fun TvCollectionsScreen(controller: TvController) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var named by remember { mutableStateOf<CollectionV1?>(null) }
    var creating by remember { mutableStateOf(false) }
    val open = controller.openCollection
    if (open != null) {
        TvCollectionWorks(controller, open)
        return
    }
    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        item {
            Text(
                text = controller.texts["collections.title"],
                fontSize = type.shelfHeading,
                fontWeight = FontWeight.Bold,
                color = colors.text,
                modifier = Modifier.padding(bottom = 6.dp),
            )
        }
        items(controller.collections, key = { it.id }) { collection ->
            TvCollectionRow(controller, collection) { named = collection }
        }
        item(key = "collection-create") {
            TvCollectionAction(controller) { creating = true }
        }
    }

    // Die Sammlungen kommen später als die Oberfläche; der Fokus wartet auf sie.
    LaunchedEffect(controller.collections) {
        if (controller.collections.isNotEmpty()) {
            controller.focusFirstEntry(TvFocusArea.List)
        }
    }

    if (creating) {
        TvNameDialog(
            controller = controller,
            title = controller.texts["collections.create"],
            onName = { name ->
                creating = false
                controller.createCollection(name)
            },
            onClose = { creating = false },
        )
    }
    named?.let { collection ->
        TvNameDialog(
            controller = controller,
            title = controller.texts["collections.rename"],
            onName = { name ->
                named = null
                controller.renameCollection(collection, name)
            },
            onClose = { named = null },
        )
    }
}

/** One action of the collections: a new one. */
@Composable
private fun TvCollectionAction(controller: TvController, onChoose: () -> Unit) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(TvLayout.RailItemRadius)
    Text(
        text = controller.texts["collections.create"],
        fontSize = type.body,
        fontWeight = FontWeight.Medium,
        color = if (focused) colors.accentText else colors.muted,
        maxLines = 1,
        modifier = Modifier
            .fillMaxWidth()
            .height(TvLayout.RailItemHeight)
            .clip(shape)
            .background(colors.surface, shape)
            // Der Eintrag steht am Ende der Liste; er gehört nicht zur Kette, damit die
            // Sammlungen, die später kommen, den Fokus an ihren Anfang bekommen.
            .reportFocus {
                focused = it
                if (it) {
                    controller.focusMovedTo(TvFocusArea.List)
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
            ) { onChoose() }
            .padding(horizontal = 16.dp),
    )
}

/** One collection of the profile. */
@Composable
private fun TvCollectionRow(controller: TvController, collection: CollectionV1, onRename: () -> Unit) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(TvLayout.RailItemRadius)
    val requester = rememberTvFocusEntry(controller, TvFocusArea.List, "collection-${collection.id}")
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
            ) { controller.openCollection(collection) }
            .padding(horizontal = 16.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text = collection.name,
            fontSize = type.body,
            fontWeight = if (focused) FontWeight.Bold else FontWeight.Medium,
            color = if (focused) colors.accentText else colors.text,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f),
        )
        collection.workCount?.let { count ->
            Text(text = count.toString(), fontSize = type.cardMeta, color = colors.muted)
        }
        // The viewer changes the name of the collection or removes it.
        TvCollectionActionLink(controller, controller.texts["collections.rename"], onRename)
        TvCollectionActionLink(controller, controller.texts["collections.delete"]) {
            controller.deleteCollection(collection)
        }
    }
}

/** One action of a collection: the name or the removal. */
@Composable
private fun TvCollectionActionLink(controller: TvController, title: String, onClick: () -> Unit) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(999.dp)
    Text(
        text = title,
        fontSize = type.cardMeta,
        color = if (focused) colors.accentText else colors.muted,
        modifier = Modifier
            .clip(shape)
            .focusRequester(remember { FocusRequester() })
            .reportFocus { focused = it }
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
            ) { onClick() }
            .padding(horizontal = 10.dp, vertical = 6.dp),
    )
}

/** The titles of a collection. */
@Composable
private fun TvCollectionWorks(controller: TvController, collection: CollectionV1) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    Column(modifier = Modifier.fillMaxSize()) {
        Text(
            text = collection.name,
            fontSize = type.shelfHeading,
            fontWeight = FontWeight.Bold,
            color = colors.text,
            modifier = Modifier.padding(bottom = 6.dp),
        )
        if (controller.collectionWorks.isEmpty()) {
            Text(
                text = controller.texts["collections.empty"],
                fontSize = type.body,
                color = colors.muted,
            )
            return@Column
        }
        LazyColumn(verticalArrangement = Arrangement.spacedBy(TvLayout.Gap)) {
            items(controller.collectionWorks, key = { it.id }) { card ->
                TvCardView(card, controller, "collection-${collection.id}")
            }
        }
    }
}

/**
 * The dialog for a name: the viewer types it with the keyboard of the system, the field takes
 * the focus when the dialog opens.
 */
@Composable
private fun TvNameDialog(
    controller: TvController,
    title: String,
    onName: (String) -> Unit,
    onClose: () -> Unit,
) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var name by remember { mutableStateOf("") }
    val requester = remember { FocusRequester() }
    val shape = RoundedCornerShape(TvLayout.ControlRadius)

    // The field takes the focus when the dialog opens, so the keyboard of the television types
    // the name right away.
    LaunchedEffect(Unit) {
        runCatching { requester.requestFocus() }
    }

    Dialog(onDismissRequest = { onClose() }) {
        Column(
            modifier = Modifier
                .width(TvLayout.CardWidth * 2)
                .clip(RoundedCornerShape(TvLayout.TopRowRadius))
                .background(colors.surface)
                .padding(TvLayout.ContentPadding),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Text(
                text = title,
                fontSize = type.shelfHeading,
                fontWeight = FontWeight.Bold,
                color = colors.text,
            )
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(TvLayout.TopRowHeight)
                    .clip(shape)
                    .background(colors.field)
                    .border(1.dp, colors.border, shape)
                    .padding(horizontal = 18.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                if (name.isEmpty()) {
                    Text(
                        text = controller.texts["collections.name"],
                        fontSize = type.body,
                        color = colors.muted,
                    )
                }
                BasicTextField(
                    value = name,
                    onValueChange = { name = it },
                    singleLine = true,
                    textStyle = TextStyle(color = colors.text, fontSize = type.body),
                    cursorBrush = SolidColor(colors.accent),
                    modifier = Modifier
                        .fillMaxWidth()
                        .focusRequester(requester),
                )
            }
            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                TvDialogButton(controller, controller.texts["common.close"], onClose)
                TvDialogButton(controller, controller.texts["common.save"]) {
                    if (name.isNotBlank()) {
                        onName(name.trim())
                    }
                }
            }
        }
    }
}

/** One button of a dialog of the television. */
@Composable
private fun TvDialogButton(controller: TvController, title: String, onClick: () -> Unit) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(999.dp)
    Text(
        text = title,
        fontSize = type.body,
        fontWeight = FontWeight.SemiBold,
        color = if (focused) colors.accentText else colors.muted,
        modifier = Modifier
            .clip(shape)
            .reportFocus { focused = it }
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
            ) { onClick() }
            .padding(horizontal = 16.dp, vertical = 10.dp),
    )
}
