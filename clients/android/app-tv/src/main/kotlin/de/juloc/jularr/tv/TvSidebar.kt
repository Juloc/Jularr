package de.juloc.jularr.tv

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
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
import androidx.compose.material.icons.filled.CalendarMonth
import androidx.compose.material.icons.filled.Home
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
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Icon
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text

private data class TvSidebarItem(
    val route: TvRoute,
    val labelRes: Int,
    val icon: ImageVector,
)

private val sidebarItems = listOf(
    TvSidebarItem(TvRoute.Home, R.string.tv_sidebar_home, Icons.Filled.Home),
    TvSidebarItem(TvRoute.Watchlist, R.string.tv_sidebar_watchlist, Icons.Filled.Bookmark),
    TvSidebarItem(TvRoute.Activity, R.string.tv_sidebar_activity, Icons.Filled.CalendarMonth),
)

@Composable
fun TvSidebar(
    selected: TvRoute,
    focusMemory: TvFocusMemory,
    onSelect: (TvRoute) -> Unit,
) {
    val focusColor = rememberTvFocusColor()
    var focusedRoute by remember { mutableStateOf<TvRoute?>(null) }
    val expanded = focusedRoute != null
    val width by animateDpAsState(if (expanded) 190.dp else 72.dp, label = "sidebar-width")

    val itemShape = RoundedCornerShape(14.dp)
    val context = LocalContext.current
    val selectedPurple = remember(context) { TvPlayerDesignLoader.load(context).accent }
    val idleBackground = Color(0xFF10121A)

    Column(
        modifier = Modifier
            .fillMaxHeight()
            .width(width)
            .background(idleBackground)
            .padding(vertical = 20.dp, horizontal = 12.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        for (item in sidebarItems) {
            val isSelected = item.route == selected
            val isFocused = focusedRoute == item.route
            val background by animateColorAsState(
                if (isSelected) selectedPurple else Color.Transparent,
                label = "sidebar-item-background",
            )

            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(48.dp)
                    .clip(itemShape)
                    .background(background)
                    .tvFocusIndication(isFocused, focusColor, itemShape)
                    .selectable(selected = isSelected, onClick = { onSelect(item.route) })
                    .reportFocus { hasFocus ->
                        focusedRoute = if (hasFocus) {
                            focusMemory.remember(
                                "sidebar",
                                TvNavigation.screenKey(item.route),
                            )
                            item.route
                        } else if (focusedRoute == item.route) {
                            null
                        } else {
                            focusedRoute
                        }
                    }
                    .padding(horizontal = 12.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                Icon(
                    imageVector = item.icon,
                    contentDescription = stringResource(item.labelRes),
                    tint = if (isSelected || isFocused) Color.White else Color.White.copy(alpha = 0.6f),
                    modifier = Modifier.size(24.dp),
                )
                if (expanded) {
                    Text(
                        text = stringResource(item.labelRes),
                        style = MaterialTheme.typography.titleMedium,
                        color = if (isSelected || isFocused) Color.White else Color.White.copy(alpha = 0.7f),
                        maxLines = 1,
                    )
                }
            }
        }

        Spacer(modifier = Modifier.weight(1f))

        // Bottom profile icon
        val profileRoute = TvRoute.Profile
        val isProfileSelected = selected == profileRoute
        val isProfileFocused = focusedRoute == profileRoute

        Box(
            modifier = Modifier
                .size(48.dp)
                .clip(itemShape)
                .background(if (isProfileSelected) selectedPurple else Color.Transparent)
                .tvFocusIndication(isProfileFocused, focusColor, itemShape)
                .selectable(selected = isProfileSelected, onClick = { onSelect(profileRoute) })
                .reportFocus { hasFocus ->
                    focusedRoute = if (hasFocus) profileRoute else if (focusedRoute == profileRoute) null else focusedRoute
                },
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                imageVector = Icons.Filled.AccountCircle,
                contentDescription = stringResource(R.string.tv_sidebar_profile),
                tint = Color.White.copy(alpha = 0.85f),
                modifier = Modifier.size(28.dp),
            )
        }
    }
}
