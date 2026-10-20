package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
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
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog

/**
 * The filter of the discover: the types of the instance and the language of the interface.
 * The granular types stay available here, as the desktop offers them in its filter.
 */
@Composable
fun TvFilterDialog(controller: TvController, onClose: () -> Unit) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    Dialog(onDismissRequest = onClose) {
        Column(
            modifier = Modifier
                .width(TvLayout.CardWidth * 3)
                .clip(RoundedCornerShape(TvLayout.TopRowRadius))
                .background(colors.surface)
                .padding(TvLayout.ContentPadding),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Text(
                text = controller.texts["discover.filter"],
                fontSize = type.shelfHeading,
                fontWeight = FontWeight.Bold,
                color = colors.text,
            )

            Text(
                text = controller.texts["discover.types"],
                fontSize = type.body,
                fontWeight = FontWeight.SemiBold,
                color = colors.muted,
            )
            LazyColumn(
                modifier = Modifier.height(TvLayout.CardWidth),
                verticalArrangement = Arrangement.spacedBy(4.dp),
            ) {
                items(controller.scopes, key = { it.slug }) { scope ->
                    TvFilterEntry(
                        title = controller.texts["discover.type." + scope.slug],
                        chosen = controller.selectedScope == scope.slug,
                    ) {
                        controller.selectScope(if (controller.selectedScope == scope.slug) null else scope.slug)
                    }
                }
            }

            Text(
                text = controller.texts["tv.settings.language"],
                fontSize = type.body,
                fontWeight = FontWeight.SemiBold,
                color = colors.muted,
            )
            LazyColumn(
                modifier = Modifier.height(TvLayout.CardWidth),
                verticalArrangement = Arrangement.spacedBy(4.dp),
            ) {
                items(controller.locales, key = { it.id }) { locale ->
                    TvFilterEntry(title = locale.name, chosen = controller.language == locale.name) {
                        controller.setLanguage(locale)
                    }
                }
            }

            Spacer(Modifier.height(4.dp))
            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                Text(
                    text = controller.texts["common.close"],
                    fontSize = type.body,
                    fontWeight = FontWeight.SemiBold,
                    color = colors.accentText,
                    modifier = Modifier
                        .clip(RoundedCornerShape(999.dp))
                        .clickable(
                            interactionSource = remember { MutableInteractionSource() },
                            indication = null,
                        ) { onClose() }
                        .padding(horizontal = 18.dp, vertical = 10.dp),
                )
            }
        }
    }
}

/** One entry of the filter. */
@Composable
private fun TvFilterEntry(title: String, chosen: Boolean, onChoose: () -> Unit) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(TvLayout.RailItemRadius)
    Text(
        text = title,
        fontSize = type.body,
        fontWeight = if (chosen) FontWeight.Bold else FontWeight.Medium,
        color = when {
            chosen -> colors.accentText
            focused -> colors.text
            else -> colors.muted
        },
        maxLines = 1,
        overflow = TextOverflow.Ellipsis,
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(if (chosen) colors.field else colors.surface, shape)
            .reportFocus { focused = it }
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
            ) { onChoose() }
            .padding(horizontal = 14.dp, vertical = 10.dp),
    )
}
