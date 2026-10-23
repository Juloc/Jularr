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
import de.juloc.jularr.core.api.catalog.TvDiscoverCatalogEntryV1
import de.juloc.jularr.core.api.catalog.TvDiscoverItemV1
import de.juloc.jularr.core.api.catalog.TvDiscoverScopeV1
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay

/** External catalog identity; never cast to a TMDB ID or Jularr WorkId. */
internal data class TvV1CatalogSelection(
    val mediaTypeId: Int,
    val namespace: String,
    val externalId: String,
    val mediaSubtypeId: Int?,
)

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
    onUnauthorized: () -> Unit,
    initialItem: TvDiscoverItemV1? = null,
    initialCatalog: TvV1CatalogSelection? = null,
) {
    var scopes by remember(catalog) { mutableStateOf<List<TvDiscoverScopeV1>>(emptyList()) }
    var selectedType by remember(catalog) { mutableStateOf<Int?>(null) }
    var query by remember(catalog) { mutableStateOf("") }
    var results by remember(catalog) { mutableStateOf<List<TvDiscoverItemV1>>(emptyList()) }
    var selectedExternal by remember(catalog, initialItem) { mutableStateOf(initialItem) }
    var selectedCatalog by remember(catalog, initialCatalog) { mutableStateOf(initialCatalog) }
    var catalogDetail by remember(catalog, initialCatalog) {
        mutableStateOf<TvDiscoverCatalogEntryV1?>(null)
    }
    var catalogLoading by remember(catalog, initialCatalog) { mutableStateOf(false) }
    var catalogError by remember(catalog, initialCatalog) { mutableStateOf<String?>(null) }
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
                .distinctBy { Triple(it.mediaTypeId, it.tmdbId, it.workId) }
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
    LaunchedEffect(selectedCatalog, catalog) {
        val selection = selectedCatalog ?: return@LaunchedEffect
        catalogLoading = true
        catalogDetail = null
        catalogError = null
        try {
            catalogDetail = catalog.discoverCatalogEntry(
                selection.mediaTypeId, selection.namespace,
                selection.externalId, selection.mediaSubtypeId,
            )
        } catch (cancel: CancellationException) {
            throw cancel
        } catch (failure: AppApiException) {
            if (failure.statusCode == 401) onUnauthorized()
            else catalogError = "Katalogdetails: ${failure.code} (HTTP ${failure.statusCode})"
        } catch (failure: Exception) {
            catalogError = failure.message ?: "Katalogdetails konnten nicht geladen werden."
        } finally {
            catalogLoading = false
        }
    }

    if (selectedCatalog != null) {
        val detail = catalogDetail
        TvCenteredPanel(
            title = detail?.title ?: "Katalogeintrag",
            description = detail?.overview.orEmpty(),
        ) {
            when {
                catalogLoading -> Text("Katalogdetails werden geladen …")
                catalogError != null -> {
                    Text(requireNotNull(catalogError), color = MaterialTheme.colorScheme.error)
                    Button(onClick = {
                        // Refresh via a distinct selection object to rerun the request.
                        selectedCatalog = null
                        catalogDetail = null
                    }) { Text("Zurück zu Discover") }
                }
                detail == null -> Text("Für diesen Katalogeintrag liegen keine Details vor.")
                else -> {
                    Text(
                        listOfNotNull(detail.releaseYear?.toString(), detail.author)
                            .joinToString(" · "),
                        style = MaterialTheme.typography.bodyMedium,
                    )
                    detail.workId?.let { local ->
                        Button(onClick = { onLocalWork(local) }) {
                            Text("In der Bibliothek öffnen")
                        }
                    } ?: Text("Noch kein lokales Jularr-Werk.")
                }
            }
            Button(onClick = { selectedCatalog = null }) { Text("Zurück zu Discover") }
        }
        return
    }

    val external = selectedExternal
    // Provider data is read on demand and replaces search-summary details.
    LaunchedEffect(external?.mediaTypeId, external?.tmdbId) {
        val tmdbId = external?.tmdbId
        if (external != null && external.workId == null && tmdbId != null) {
            try {
                selectedExternal = catalog.discoverTmdbItem(external.mediaTypeId, tmdbId)
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (failure: Exception) {
                if (failure is AppApiException && failure.statusCode == 401) onUnauthorized()
                else error = failure.message ?: "Externe Details konnten nicht geladen werden."
            }
        }
    }
    if (external != null) {
        TvCenteredPanel(title = external.title, description = external.overview.orEmpty()) {
            Text(
                listOfNotNull(external.releaseYear?.toString(),
                    external.rating?.let { "Bewertung $it" }).joinToString(" · "),
                style = MaterialTheme.typography.bodyMedium,
            )
            val localWorkId = external.workId
            if (localWorkId != null) {
                Button(onClick = { onLocalWork(localWorkId) }) {
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
                        val workId = item.workId
                        if (workId != null) onLocalWork(workId)
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

}
