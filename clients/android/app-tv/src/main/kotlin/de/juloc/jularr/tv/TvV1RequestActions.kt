package de.juloc.jularr.tv

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.catalog.TvCatalogV1Client
import de.juloc.jularr.core.api.catalog.TvRequestLocaleV1
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

/** Remote-friendly confirmation and locale selection for the canonical V1 request service. */
@Composable
internal fun TvV1RequestActions(
    catalog: TvCatalogV1Client,
    itemKey: String,
    canRequest: Boolean,
    onUnauthorized: () -> Unit,
    tmdb: Pair<Int, Int>? = null,
    catalogItem: TvV1CatalogSelection? = null,
) {
    if (!canRequest || (tmdb == null && catalogItem == null)) return

    var locales by remember(catalog, itemKey) { mutableStateOf<List<TvRequestLocaleV1>>(emptyList()) }
    var loading by remember(catalog, itemKey) { mutableStateOf(true) }
    var selectedLocale by remember(catalog, itemKey) { mutableStateOf<Long?>(null) }
    var busy by remember(catalog, itemKey) { mutableStateOf(false) }
    var createdId by remember(catalog, itemKey) { mutableStateOf<Long?>(null) }
    var error by remember(catalog, itemKey) { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    LaunchedEffect(catalog, itemKey) {
        loading = true
        error = null
        try {
            locales = catalog.requestLocales().filter { it.id > 0 && it.locale.isNotBlank() }
        } catch (cancel: CancellationException) {
            throw cancel
        } catch (failure: AppApiException) {
            if (failure.statusCode == 401) onUnauthorized()
            else error = "Sprachen konnten nicht geladen werden (HTTP ${failure.statusCode})."
        } catch (failure: Exception) {
            error = failure.message ?: "Sprachen konnten nicht geladen werden."
        } finally {
            loading = false
        }
    }

    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        if (createdId != null) {
            Text("Anfrage erstellt. Den Fortschritt kannst du unter Anfragen verfolgen.")
            return@Column
        }
        Text("Medium anfragen", style = MaterialTheme.typography.titleMedium)
        if (loading) Text("Verfügbare Sprachen werden geladen …")
        if (!loading && locales.isEmpty()) Text("Keine Anfragesprache verfügbar.")
        locales.forEach { locale ->
            Button(
                enabled = !busy,
                onClick = { selectedLocale = locale.id; error = null },
            ) {
                Text((if (selectedLocale == locale.id) "✓ " else "") + locale.name)
            }
        }
        if (selectedLocale != null) {
            Button(enabled = !busy, onClick = {
                val localeId = selectedLocale ?: return@Button
                if (busy) return@Button
                busy = true
                error = null
                scope.launch {
                    try {
                        val result = if (tmdb != null) {
                            catalog.requestDiscoverTmdb(tmdb.first, tmdb.second, localeId)
                        } else {
                            val item = requireNotNull(catalogItem)
                            catalog.requestDiscoverCatalog(
                                item.mediaTypeId, item.namespace, item.externalId, item.mediaSubtypeId, localeId,
                            )
                        }
                        check(result > 0) { "Jularr hat keine gültige Anfragen-ID bestätigt." }
                        createdId = result
                    } catch (cancel: CancellationException) {
                        throw cancel
                    } catch (failure: AppApiException) {
                        if (failure.statusCode == 401) onUnauthorized()
                        else error = "Anfrage fehlgeschlagen (HTTP ${failure.statusCode}: ${failure.code})."
                    } catch (failure: Exception) {
                        error = failure.message ?: "Anfrage fehlgeschlagen."
                    } finally {
                        busy = false
                    }
                }
            }) {
                Text(if (busy) "Anfrage wird erstellt …" else "Anfrage bestätigen")
            }
        }
        error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
    }
}
