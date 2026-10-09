package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Logout
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.Icon
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Surface
import androidx.tv.material3.Text

@Composable
fun TvProfileSelectScreen(
    sessions: List<TvSavedSession>,
    activeSessionId: String?,
    onSelectSession: (TvSavedSession) -> Unit,
    onAddAccount: () -> Unit,
    onSignOut: () -> Unit,
) {
    val context = LocalContext.current
    val accent = remember(context) { TvPlayerDesignLoader.load(context).accent }
    Surface(modifier = Modifier.fillMaxSize()) {
        Box(
            Modifier.fillMaxSize().background(
                Brush.verticalGradient(
                    listOf(MaterialTheme.colorScheme.surface, MaterialTheme.colorScheme.background),
                ),
            ),
        ) {
            Column(
                modifier = Modifier.fillMaxSize().padding(38.dp),
                verticalArrangement = Arrangement.spacedBy(22.dp),
            ) {
                Text(
                    stringResource(R.string.tv_profile_select_title),
                    color = MaterialTheme.colorScheme.onSurface,
                    style = MaterialTheme.typography.displaySmall,
                    fontWeight = FontWeight.Bold,
                )
                Text(
                    stringResource(R.string.tv_profile_select_description),
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.72f),
                    style = MaterialTheme.typography.titleMedium,
                )
                LazyVerticalGrid(
                    columns = GridCells.Adaptive(174.dp),
                    modifier = Modifier.fillMaxWidth().weight(1f),
                    horizontalArrangement = Arrangement.spacedBy(16.dp),
                    verticalArrangement = Arrangement.spacedBy(16.dp),
                ) {
                    items(sessions, key = { it.id }) { session ->
                        var focused by remember(session.id) { mutableStateOf(false) }
                        val shape = RoundedCornerShape(16.dp)
                        val current = activeSessionId == session.id
                        Column(
                            modifier = Modifier
                                .clip(shape)
                                .background(MaterialTheme.colorScheme.surfaceVariant)
                                .border(
                                    if (focused) 2.dp else 1.dp,
                                    if (focused) accent else MaterialTheme.colorScheme.outline.copy(alpha = 0.4f),
                                    shape,
                                )
                                .clickable { onSelectSession(session) }
                                .reportFocus { focused = it }
                                .padding(12.dp),
                            horizontalAlignment = Alignment.CenterHorizontally,
                            verticalArrangement = Arrangement.spacedBy(10.dp),
                        ) {
                            Box(
                                modifier = Modifier.fillMaxWidth().height(155.dp)
                                    .clip(RoundedCornerShape(12.dp))
                                    .background(
                                        Brush.verticalGradient(
                                            listOf(accent.copy(alpha = 0.55f), accent.copy(alpha = 0.13f)),
                                        ),
                                    ),
                                contentAlignment = Alignment.Center,
                            ) {
                                Text(
                                    session.userName.firstOrNull()?.uppercaseChar()?.toString() ?: "?",
                                    style = MaterialTheme.typography.displayLarge,
                                    color = MaterialTheme.colorScheme.onSurface,
                                )
                            }
                            Text(
                                session.userName,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                color = MaterialTheme.colorScheme.onSurface,
                                style = MaterialTheme.typography.titleMedium,
                            )
                            if (current) {
                                Text(
                                    stringResource(R.string.tv_profile_select_current),
                                    color = accent,
                                    style = MaterialTheme.typography.labelLarge,
                                )
                            } else {
                                Spacer(Modifier.height(20.dp))
                            }
                        }
                    }
                }
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.Center,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Button(onClick = onAddAccount) {
                        Icon(Icons.Filled.Add, contentDescription = null)
                        Text(" " + stringResource(R.string.tv_profile_select_other_account))
                    }
                    Spacer(Modifier.size(18.dp))
                    if (activeSessionId != null) {
                        Button(onClick = onSignOut) {
                            Icon(Icons.Filled.Logout, contentDescription = null)
                            Text(" " + stringResource(R.string.tv_profile_select_sign_out))
                        }
                    }
                }
            }
        }
    }
}
