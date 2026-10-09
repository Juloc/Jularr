package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AccountCircle
import androidx.compose.material.icons.filled.Bookmark
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.Settings
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Icon
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text

private data class TvSidebarItem(
    val route: TvRoute,
    val labelRes: Int,
    val icon: ImageVector,
)

private val mainNavigation = listOf(
    TvSidebarItem(TvRoute.Home, R.string.tv_sidebar_home, Icons.Filled.Home),
    TvSidebarItem(TvRoute.Watchlist, R.string.tv_sidebar_watchlist, Icons.Filled.Bookmark),
)
private val footerNavigation = listOf(
    TvSidebarItem(TvRoute.Settings, R.string.tv_sidebar_settings, Icons.Filled.Settings),
    TvSidebarItem(TvRoute.ProfileSelect, R.string.tv_profile_switch_account, Icons.Filled.AccountCircle),
)

@Composable
fun TvSidebar(
    selected: TvRoute,
    focusMemory: TvFocusMemory,
    onSelect: (TvRoute) -> Unit,
) {
    val context = LocalContext.current
    val accent = remember(context) { TvPlayerDesignLoader.load(context).accent }
    Column(
        modifier = Modifier
            .fillMaxHeight()
            .width(208.dp)
            .background(MaterialTheme.colorScheme.surface)
            .padding(horizontal = 14.dp, vertical = 24.dp),
        verticalArrangement = Arrangement.spacedBy(9.dp),
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 10.dp, vertical = 14.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                text = "Jularr",
                color = MaterialTheme.colorScheme.onSurface,
                style = MaterialTheme.typography.headlineSmall,
                fontWeight = FontWeight.Bold,
            )
            Text(
                text = " TV",
                color = accent,
                style = MaterialTheme.typography.headlineSmall,
                fontWeight = FontWeight.Bold,
            )
        }
        Spacer(Modifier.height(12.dp))
        mainNavigation.forEach { item ->
            TvSidebarLink(item, selected, accent, focusMemory, onSelect)
        }
        Spacer(Modifier.weight(1f))
        footerNavigation.forEach { item ->
            TvSidebarLink(item, selected, accent, focusMemory, onSelect)
        }
    }
}

@Composable
private fun TvSidebarLink(
    item: TvSidebarItem,
    selected: TvRoute,
    accent: Color,
    focusMemory: TvFocusMemory,
    onSelect: (TvRoute) -> Unit,
) {
    var focused by remember(item.route) { mutableStateOf(false) }
    val shape = RoundedCornerShape(13.dp)
    val selectedHere = selected == item.route
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(52.dp)
            .clip(shape)
            .background(
                when {
                    selectedHere -> accent.copy(alpha = 0.31f)
                    focused -> accent.copy(alpha = 0.13f)
                    else -> Color.Transparent
                },
            )
            .border(
                if (focused) 2.dp else 1.dp,
                if (focused) accent else if (selectedHere) accent.copy(alpha = 0.50f) else Color.Transparent,
                shape,
            )
            .selectable(selected = selectedHere, onClick = { onSelect(item.route) })
            .reportFocus { hasFocus ->
                focused = hasFocus
                if (hasFocus) focusMemory.remember("sidebar", TvNavigation.screenKey(item.route))
            }
            .padding(horizontal = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(13.dp),
    ) {
        Icon(
            imageVector = item.icon,
            contentDescription = null,
            tint = if (selectedHere || focused) MaterialTheme.colorScheme.onSurface
                else MaterialTheme.colorScheme.onSurface.copy(alpha = 0.72f),
            modifier = Modifier.size(23.dp),
        )
        Text(
            text = stringResource(item.labelRes),
            maxLines = 1,
            color = MaterialTheme.colorScheme.onSurface,
            style = MaterialTheme.typography.titleSmall,
        )
    }
}
