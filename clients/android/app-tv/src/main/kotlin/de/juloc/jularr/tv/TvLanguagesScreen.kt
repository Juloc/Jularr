package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
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

/**
 * The languages of the instance. The profile the television works as takes the language the
 * viewer chooses; the texts of the instance follow in the new language.
 */
@Composable
fun TvLanguagesScreen(controller: TvController) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        item {
            Text(
                text = controller.texts["tv.settings.language"],
                fontSize = type.shelfHeading,
                fontWeight = FontWeight.Bold,
                color = colors.text,
                modifier = Modifier.padding(bottom = 6.dp),
            )
        }
        items(controller.locales, key = { it.id }) { locale ->
            var focused by remember { mutableStateOf(false) }
            val shape = RoundedCornerShape(TvLayout.RailItemRadius)
            val chosen = controller.language == locale.locale
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(TvLayout.RailItemHeight)
                    .clip(shape)
                    .background(colors.surface, shape)
                    .reportFocus { focused = it }
                    .clickable(
                        interactionSource = remember { MutableInteractionSource() },
                        indication = null,
                    ) { controller.setLanguage(locale) }
                    .padding(horizontal = 16.dp, vertical = 10.dp),
                verticalArrangement = Arrangement.Center,
            ) {
                Text(
                    text = locale.name,
                    fontSize = type.body,
                    fontWeight = if (chosen) FontWeight.Bold else FontWeight.Medium,
                    color = if (focused) colors.accentText else colors.text,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}
