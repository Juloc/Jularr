package de.juloc.jularr.mobile.offline.library

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import de.juloc.jularr.core.api.JularrLibraryApi
import kotlinx.coroutines.launch

/**
 * Native "Save offline" action shown while browsing a book/novel detail page
 * in the WebView (`/Novels/Work/{id}` or `/Books/Library/{id}`, detected by
 * `LibraryWebPages`). This is the Android entry point for downloading a book
 * with the native engine (Room-free JSON store + WorkManager, this file's
 * package), as distinct from the PWA's own IndexedDB/OPFS engine running
 * inside a regular browser — no JS bridge is used to trigger it, only the
 * same URL-path detection already used for the episode Play route.
 */
@Composable
fun LibrarySaveOfflineOverlay(
    origin: String,
    workId: String,
    downloads: LibraryDownloads,
    api: JularrLibraryApi,
    onOpenDownloads: () -> Unit,
) {
    val snapshot by downloads.state.collectAsState()
    val scope = rememberCoroutineScope()
    var message by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }

    val book = snapshot.accessibleBooks(origin).firstOrNull { it.workId == workId }
    val chapters = book?.let { snapshot.chaptersOf(it.ownerKey, it.workId) }.orEmpty()
    val status = book?.let { LibraryBookStatus.of(chapters) }

    Box(modifier = Modifier.fillMaxSize().padding(16.dp)) {
        Column(
            modifier = Modifier.align(Alignment.BottomEnd),
            horizontalAlignment = Alignment.End,
        ) {
            message?.let {
                Text(
                    text = it,
                    color = MaterialTheme.colorScheme.error,
                    style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.padding(bottom = 8.dp),
                )
            }

            when (status) {
                BookDownloadStatus.AVAILABLE, BookDownloadStatus.UPDATE_AVAILABLE,
                BookDownloadStatus.DOWNLOADING, BookDownloadStatus.PAUSED, BookDownloadStatus.QUEUED,
                -> ExtendedFloatingActionButton(
                    onClick = onOpenDownloads,
                    text = { Text(bookStatusActionLabel(status)) },
                    icon = {},
                )

                BookDownloadStatus.FAILED, null -> ExtendedFloatingActionButton(
                    onClick = {
                        if (busy) return@ExtendedFloatingActionButton
                        busy = true
                        message = null
                        scope.launch {
                            val result = downloads.enqueueBook(origin, workId, api)
                            message = (result as? LibraryEnqueueResult.Rejected)?.message
                            busy = false
                        }
                    },
                    text = { Text(if (status == BookDownloadStatus.FAILED) "Retry download" else "Save offline") },
                    icon = {},
                )
            }
        }
    }
}

private fun bookStatusActionLabel(status: BookDownloadStatus?): String =
    when (status) {
        BookDownloadStatus.AVAILABLE -> "Downloaded"
        BookDownloadStatus.UPDATE_AVAILABLE -> "Update available"
        BookDownloadStatus.DOWNLOADING -> "Downloading…"
        BookDownloadStatus.PAUSED -> "Paused"
        BookDownloadStatus.QUEUED -> "Queued"
        BookDownloadStatus.FAILED, null -> "Download failed"
    }
