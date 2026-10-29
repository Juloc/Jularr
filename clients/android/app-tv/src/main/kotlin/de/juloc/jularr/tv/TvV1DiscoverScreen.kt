package de.juloc.jularr.tv

import androidx.activity.compose.BackHandler
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
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.catalog.TvCatalogV1Client
import de.juloc.jularr.core.api.catalog.TvDiscoverItemV1
import de.juloc.jularr.core.api.catalog.TvDiscoverScopeV1
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay

/** MediaStateType.Available on the server. */
internal const val MEDIA_STATE_AVAILABLE = 3

/**
 * Discover results reuse the original TV search input, card artwork and D-pad buttons.
 * Every title is a Work and opens the Work page, which also offers the request.
 */
@Composable
internal fun TvV1DiscoverScreen(
    catalog: TvCatalogV1Client,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    onLocalWork: (Long) -> Unit,
    onBack: () -> Unit,
    onUnauthorized: () -> Unit,
    canRequest: Boolean = false,
) {
    var scopes by remember(catalog) { mutableStateOf<List<TvDiscoverScopeV1>>(emptyList()) }
    var selectedType by remember(catalog) { mutableStateOf<Int?>(null) }
    var query by remember(catalog) { mutableStateOf("") }
    var results by remember(catalog) { mutableStateOf<List<TvDiscoverItemV1>>(emptyList()) }
    var loading by remember(catalog) { mutableStateOf(false) }
    var error by remember(catalog) { mutableStateOf<String?>(null) }
    var retry by remember(catalog) { mutableStateOf(0) }

    LaunchedEffect(catalog) {
        try {
            scopes = catalog.discoverScopes().filter {
                !it.isCatalog && (it.mediaTypeId == 1 || it.mediaTypeId == 2)
            }
        } catch (failure: AppApiException) {
            if (failure.statusCode == 401) onUnauthorized()
            else error = "Discover: ${failure.code} (HTTP ${failure.statusCode})"
        } catch (failure: Exception) {
            error = failure.message ?: "Discover-Kategorien konnten nicht geladen werden."
        }
    }
    LaunchedEffect(catalog, query, selectedType, retry) {
        loading = true
        try {
            if (query.isNotBlank()) delay(350)
            val loaded = if (query.isBlank()) {
                val shelves = catalog.discoverShelves()
                shelves.flatMap { it.items }.filter {
                    it.mediaTypeId == 1 || it.mediaTypeId == 2
                }
            } else {
                catalog.discoverSearch(query.trim(), selectedType)
            }
            results = loaded.filter { (it.mediaTypeId == 1 || it.mediaTypeId == 2) &&
                (selectedType == null || it.mediaTypeId == selectedType) }
                .distinctBy { it.workId }
            error = null
        } catch (cancel: CancellationException) {
            throw cancel
        } catch (failure: AppApiException) {
            if (failure.statusCode == 401) onUnauthorized()
            else error = "Discover: ${failure.code} (HTTP ${failure.statusCode})"
        } catch (failure: Exception) {
            error = failure.message ?: "Discover konnte nicht geladen werden."
        } finally { loading = false }
    }
    Column(
        modifier = Modifier.fillMaxSize().padding(28.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        Text("Discover", style = MaterialTheme.typography.displaySmall)
        TvInput(
            value = query,
            onValueChange = { query = it },
            label = "Katalog durchsuchen",
            placeholder = "Film oder Serie suchen",
        )
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Button(onClick = { selectedType = null }) {
                Text(if (selectedType == null) "✓ Alle" else "Alle")
            }
            scopes.distinctBy { it.mediaTypeId }.forEach { scope ->
                Button(onClick = { selectedType = scope.mediaTypeId }) {
                    Text((if (selectedType == scope.mediaTypeId) "✓ " else "") +
                        if (scope.mediaTypeId == 1) "Filme" else "Serien")
                }
            }
            Button(onClick = onBack) { Text("Zur Bibliothekssuche") }
        }
        if (loading) Text("Discover-Daten werden geladen …")
        if (error != null) {
            Text(requireNotNull(error), color = MaterialTheme.colorScheme.error)
            Button(onClick = { retry += 1 }) { Text("Erneut versuchen") }
        }
        LazyColumn(
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            items(results, key = { it.workId }) { item ->
                Button(
                    modifier = Modifier.fillMaxWidth(),
                    onClick = { onLocalWork(item.workId) },
                ) {
                    Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                        TvArtwork(
                            url = item.posterUrl,
                            serverOrigin = serverOrigin,
                            requestHeaders = requestHeaders,
                            contentDescription = item.title,
                            modifier = Modifier.width(64.dp).height(86.dp),
                        )
                        Column {
                            Text(item.title, style = MaterialTheme.typography.titleMedium)
                            Text(
                                listOfNotNull(item.releaseYear?.toString(),
                                    if (item.mediaStateTypeId == MEDIA_STATE_AVAILABLE) "In deiner Bibliothek" else "Nicht in deiner Bibliothek",
                                ).joinToString(" · "),
                                style = MaterialTheme.typography.bodySmall,
                            )
                        }
                    }
                }
            }
        }
        if (!loading && error == null && results.isEmpty()) Text("Keine Katalogtreffer.")
    }

}
