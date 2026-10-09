package de.juloc.jularr.tv

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import de.juloc.jularr.core.model.AnimeSummary
import de.juloc.jularr.core.model.ClientLibrary

/**
 * Home's search field opens this screen (#522, "Correction: TV search lives inside
 * Home"): a search input at the top, the same All/Movies/TV/Anime filter as Home, and the
 * matching titles below. There is no separate "Discover" destination — this single screen
 * is both search and browse.
 */
@Composable
fun TvSearchScreen(
    library: ClientLibrary,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    focusMemory: TvFocusMemory,
    onAnime: (AnimeSummary) -> Unit,
    onBack: () -> Unit,
) {
    var query by rememberSaveable { mutableStateOf("") }
    var filter by remember { mutableStateOf(TvContentFilter.ALL) }
    val focusColor = rememberTvFocusColor()
    val results = remember(library, query, filter) {
        TvSearchFilter.matches(library.anime, query, filter)
    }

    Surface(modifier = Modifier.fillMaxSize()) {
        LazyColumn(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = 48.dp, vertical = 32.dp),
            verticalArrangement = Arrangement.spacedBy(22.dp),
        ) {
            item {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(18.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Button(onClick = onBack) { Text(stringResource(R.string.tv_action_back)) }
                }
            }

            item {
                Column(verticalArrangement = Arrangement.spacedBy(16.dp)) {
                    TvInput(
                        value = query,
                        onValueChange = { query = it },
                        label = stringResource(R.string.tv_search_placeholder),
                        placeholder = stringResource(R.string.tv_search_placeholder),
                        onFocusChanged = { focused ->
                            if (focused) {
                                focusMemory.remember("search", "search-field")
                            }
                        },
                    )
                    TvFilterChipRow(
                        selected = filter,
                        focusMemory = focusMemory,
                        screenKey = "search",
                        onSelect = { filter = it },
                    )
                }
            }

            item {
                when {
                    query.isBlank() && results.isEmpty() -> Text(
                        stringResource(R.string.tv_search_empty_query),
                        style = MaterialTheme.typography.bodyLarge,
                    )

                    results.isEmpty() -> Text(
                        stringResource(R.string.tv_search_no_results, query),
                        style = MaterialTheme.typography.bodyLarge,
                    )

                    else -> LazyRow(horizontalArrangement = Arrangement.spacedBy(18.dp)) {
                        items(items = results, key = { it.id }) { anime ->
                            var focused by remember(anime.id) { mutableStateOf(false) }
                            AnimeButton(
                                anime = anime,
                                serverOrigin = serverOrigin,
                                requestHeaders = requestHeaders,
                                focused = focused,
                                focusColor = focusColor,
                                onFocusChanged = { isFocused ->
                                    focused = isFocused
                                    if (isFocused) {
                                        focusMemory.remember("search", "anime:${anime.id}")
                                    }
                                },
                                onClick = { onAnime(anime) },
                            )
                        }
                    }
                }
            }
        }
    }
}
