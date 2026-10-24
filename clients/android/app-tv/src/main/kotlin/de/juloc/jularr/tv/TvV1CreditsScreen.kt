package de.juloc.jularr.tv

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.api.catalog.TvPersonV1
import de.juloc.jularr.core.api.catalog.TvWorkCardV1
import de.juloc.jularr.core.api.catalog.TvWorkCreditV1

/** These numeric person roles are defined by the backend's Media/PersonRole enum. */
internal fun tvV1RoleName(roleId: Int): String = when (roleId) {
    1 -> "Schauspiel"
    2 -> "Gastrolle"
    3 -> "Regie"
    4 -> "Drehbuch"
    5 -> "Musik"
    6 -> "Idee"
    7 -> "Autor"
    8 -> "Illustration"
    9 -> "Kunst"
    10 -> "Entwicklung"
    11 -> "Sprecher"
    12 -> "Übersetzung"
    else -> "Mitwirkung (Rolle $roleId)"
}

@Composable
internal fun TvV1CreditsScreen(
    title: String,
    credits: List<TvWorkCreditV1>,
    loading: Boolean,
    error: String?,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    onPerson: (Long) -> Unit,
    onRetry: () -> Unit,
    onBack: () -> Unit,
) {
    TvCenteredPanel("Mitwirkende", title) {
        if (error != null) Text(error, color = MaterialTheme.colorScheme.error)
        if (loading) Text("Mitwirkende werden geladen …")
        if (!loading && error == null && credits.isEmpty()) Text("Keine Mitwirkenden hinterlegt.")
        LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(credits, key = { "${it.personId}:${it.personRoleId}:${it.credit.orEmpty()}" }) { credit ->
                Button(onClick = { onPerson(credit.personId) }, modifier = Modifier.fillMaxWidth()) {
                    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        if (!credit.imageUrl.isNullOrBlank()) {
                            TvArtwork(
                                url = credit.imageUrl,
                                serverOrigin = serverOrigin,
                                requestHeaders = requestHeaders,
                                contentDescription = credit.name,
                                modifier = Modifier.width(48.dp).height(64.dp),
                            )
                        }
                        Column {
                            Text(credit.name, style = MaterialTheme.typography.titleMedium)
                            Text(
                                listOfNotNull(tvV1RoleName(credit.personRoleId), credit.credit)
                                    .joinToString(" · "),
                                style = MaterialTheme.typography.bodySmall,
                            )
                        }
                    }
                }
            }
        }
        if (error != null) Button(onClick = onRetry) { Text("Erneut versuchen") }
        Button(onClick = onBack) { Text("Zurück zu Details") }
    }
}

@Composable
internal fun TvV1PersonScreen(
    person: TvPersonV1?,
    works: List<TvWorkCardV1>,
    loading: Boolean,
    error: String?,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    onWork: (Long) -> Unit,
    onRetry: () -> Unit,
    onBack: () -> Unit,
    onLoadMore: (() -> Unit)? = null,
    loadingMore: Boolean = false,
    pagingError: String? = null,
) {
    TvCenteredPanel(person?.name ?: "Person", "Film- und Serienwerke dieser Person") {
        if (loading) Text("Person und Werke werden geladen …")
        if (error != null) Text(error, color = MaterialTheme.colorScheme.error)
        if (!loading && error == null && works.isEmpty()) Text("Keine lokalen Film- oder Serienwerke.")
        LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(works, key = { it.id }) { work ->
                Button(onClick = { onWork(work.id) }, modifier = Modifier.fillMaxWidth()) {
                    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        if (!work.posterUrl.isNullOrBlank()) {
                            TvArtwork(
                                url = work.posterUrl,
                                serverOrigin = serverOrigin,
                                requestHeaders = requestHeaders,
                                contentDescription = work.title,
                                modifier = Modifier.width(48.dp).height(64.dp),
                            )
                        }
                        Column {
                            Text(work.title, style = MaterialTheme.typography.titleMedium)
                            work.releaseYear?.let { Text(it.toString(), style = MaterialTheme.typography.bodySmall) }
                        }
                    }
                }
            }
        }
        if (pagingError != null) Text(pagingError, color = MaterialTheme.colorScheme.error)
        if (loadingMore) Text("Weitere Filmografie wird geladen …")
        if (onLoadMore != null) Button(onClick = onLoadMore, enabled = !loadingMore) {
            Text("Weitere Werke laden")
        }
        if (error != null) Button(onClick = onRetry) { Text("Erneut versuchen") }
        Button(onClick = onBack) { Text("Zurück zu Mitwirkenden") }
    }
}
