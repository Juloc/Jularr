package de.juloc.jularr.tv

import androidx.compose.foundation.background
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
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
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
        item {
            TvCollectionAction(controller.texts["collections.create"]) { creating = true }
        }
    }

    if (creating) {
        TvNameDialog(controller, controller.texts["collections.create"]) { name ->
            creating = false
            controller.createCollection(name)
        }
    }
    named?.let { collection ->
        TvNameDialog(controller, controller.texts["collections.rename"]) { name ->
            named = null
            controller.renameCollection(collection, name)
        }
    }
}

/** One action of the collections: a new one. */
@Composable
private fun TvCollectionAction(title: String, onChoose: () -> Unit) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(TvLayout.RailItemRadius)
    Text(
        text = title,
        fontSize = type.body,
        fontWeight = FontWeight.Medium,
        color = if (focused) colors.accentText else colors.muted,
        maxLines = 1,
        modifier = Modifier
            .fillMaxWidth()
            .height(TvLayout.RailItemHeight)
            .clip(shape)
            .background(colors.surface, shape)
            .reportFocus { focused = it }
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
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(TvLayout.RailItemHeight)
            .clip(shape)
            .background(colors.surface, shape)
            .reportFocus { focused = it }
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
        Text(
            text = controller.texts["collections.rename"],
            fontSize = type.cardMeta,
            color = colors.muted,
            modifier = Modifier
                .clip(RoundedCornerShape(999.dp))
                .clickable(
                    interactionSource = remember { MutableInteractionSource() },
                    indication = null,
                ) { onRename() }
                .padding(horizontal = 10.dp, vertical = 6.dp),
        )
        Text(
            text = controller.texts["collections.delete"],
            fontSize = type.cardMeta,
            color = colors.muted,
            modifier = Modifier
                .clip(RoundedCornerShape(999.dp))
                .clickable(
                    interactionSource = remember { MutableInteractionSource() },
                    indication = null,
                ) { controller.deleteCollection(collection) }
                .padding(horizontal = 10.dp, vertical = 6.dp),
        )
    }
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
 * The dialog for a name: the viewer types it with the keyboard of the system.
 */
@Composable
private fun TvNameDialog(controller: TvController, title: String, onName: (String) -> Unit) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var name by remember { mutableStateOf("") }
    Dialog(onDismissRequest = { }) {
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
            OutlinedTextField(
                value = name,
                onValueChange = { name = it },
                singleLine = true,
                label = { Text(controller.texts["collections.name"], fontSize = type.body) },
                textStyle = androidx.compose.ui.text.TextStyle(fontSize = type.body, color = colors.text),
            )
            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                Text(
                    text = controller.texts["common.close"],
                    fontSize = type.body,
                    fontWeight = FontWeight.SemiBold,
                    color = colors.muted,
                    modifier = Modifier
                        .clip(RoundedCornerShape(999.dp))
                        .clickable(
                            interactionSource = remember { MutableInteractionSource() },
                            indication = null,
                        ) { }
                        .padding(horizontal = 16.dp, vertical = 10.dp),
                )
                Text(
                    text = controller.texts["common.save"],
                    fontSize = type.body,
                    fontWeight = FontWeight.SemiBold,
                    color = colors.accentText,
                    modifier = Modifier
                        .clip(RoundedCornerShape(999.dp))
                        .clickable(
                            interactionSource = remember { MutableInteractionSource() },
                            indication = null,
                        ) {
                            if (name.isNotBlank()) {
                                onName(name.trim())
                            }
                        }
                        .padding(horizontal = 16.dp, vertical = 10.dp),
                )
            }
        }
    }
}
