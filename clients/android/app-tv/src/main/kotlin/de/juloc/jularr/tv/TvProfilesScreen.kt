package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch

/**
 * The profiles of the account of the device. The television works as one of them; the others
 * are opened with OK.
 */
@Composable
fun TvProfilesScreen(controller: TvController) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    val scope = rememberCoroutineScope()
    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        verticalArrangement = Arrangement.spacedBy(TvLayout.Gap),
    ) {
        item {
            Text(
                text = controller.texts["tv.profiles.title"],
                fontSize = type.shelfHeading,
                fontWeight = FontWeight.Bold,
                color = colors.text,
            )
        }
        items(controller.profiles, key = { it.id }) { profile ->
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
                    ) {
                        scope.launch { controller.switchProfile(profile.id) }
                    }
                            .then(
                        if (focused) {
                            Modifier.tvFocusRing(true, colors.focus, shape)
                        } else {
                            Modifier
                        },
                    )
                    .padding(horizontal = 14.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = profile.displayName,
                    fontSize = type.body,
                    fontWeight = FontWeight.Medium,
                    color = if (focused) colors.accentText else colors.text,
                )
            }
        }
    }
}
