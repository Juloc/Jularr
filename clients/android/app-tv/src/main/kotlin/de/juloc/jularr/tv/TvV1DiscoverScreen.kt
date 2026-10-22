package de.juloc.jularr.tv

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

/**
 * Discover results reuse the original TV search input, card artwork and D-pad buttons.
 * Provider-only results never cast TMDB IDs to local Work IDs.
 */
@Composable
internal fun TvV1DiscoverScreen(
    catalog: TvCatalogV1Client,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    onLocalWork: (Long) -> Unit,
    onBack: () -> Unit,
) {
    var scopes by remember(catalog) { mutableStateOf<List<TvDiscoverScopeV1>>(emptyList()) }
    var selectedType by remember(catalog) { mutableStateOf<Int?>(null) }
    var query by remember(catalog) { mutableStateOf("") }
    var results by remember(catalog) { mutableStateOf<List<TvDiscoverItemV1>>(emptyList()) }
    var selectedExternal by remember(catalog) { mutableStateOf<TvDiscoverItemV1?>(null) }
    var loading by remember(catalog) { mutableStateOf(false) }
    var error by remember(catalog) { mutableStateOf<String?>(null) }
    var retry by remember(catalog) { mutableStateOf(0) }

    LaunchedEffect(catalog) {
        try {
            scopes = catalog.discoverScopes().filter {
                !it.isCatalog && (it.mediaTypeId == 1 || it.mediaTypeId == 2)
            }
        } catch (failure: AppApiException) {
            error = "Discover: ${failure.code} (HTTP ${failure.statusCode})"
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
                .distinctBy { Triple(it.mediaTypeId, it.tmdbId, it.workId) }
            error = null
        } catch (cancel: CancellationException) {
            throw cancel
        } catch (failure: AppApiException) {
            error = "Discover: ${failure.code} (HTTP ${failure.statusCode})"
        } catch (failure: Exception) {
            error = failure.message ?: "Discover konnte nicht geladen werden."
        } finally { loading = false }
    }
    val external = selectedExternal
    if (external != null) {
        TvCenteredPanel(title = external.title, description = external.overview.orEmpty()) {
            Text(
                listOfNotNull(external.releaseYear?.toString(),
                    external.rating?.let { "Bewertung $it" }).joinToString(" · "),
                style = MaterialTheme.typography.bodyMedium,
            )
            if (external.workId != null) {
                Button(onClick = { onLocalWork(external.workId) }) {
                    Text("In der Bibliothek öffnen")
                }
            } else {
                Text("Dieser Titel ist noch kein lokales Jularr-Work. Es wird keine ID erfunden.")
            }
            Button(onClick = { selectedExternal = null }) { Text("Zurück zu Discover") }
        }
        return
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
            items(results, key = { "${it.mediaTypeId}:${it.tmdbId ?: "local"}:${it.workId ?: it.title}" }) { item ->
                Button(
                    modifier = Modifier.fillMaxWidth(),
                    onClick = {
                        if (item.workId != null) onLocalWork(item.workId)
                        else if (item.tmdbId != null) {
                            // Details must be fetched from the provider route, not /works/TMDB-ID.
                            selectedExternal = item
                        }
                    },
                    enabled = item.workId != null || item.tmdbId != null,
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
                                    if (item.workId != null) "In deiner Bibliothek" else "Externer Katalog",
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

    // Provider data is read on demand and replaces search-summary details.
    LaunchedEffect(external?.mediaTypeId, external?.tmdbId) {
        if (external?.workId == null && external?.tmdbId != null) {
            try {
                selectedExternal = catalog.discoverTmdbItem(external.mediaTypeId, external.tmdbId)
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (failure: Exception) {
                error = failure.message ?: "Externe Details konnten nicht geladen werden."
            }
        }
    }
}
