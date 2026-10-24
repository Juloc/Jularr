package de.juloc.jularr.mobile.offline.library

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import de.juloc.jularr.mobile.offline.DownloadState
import de.juloc.jularr.mobile.offline.OfflineStoragePolicy

/**
 * Offline Books management: per-book aggregate status with per-chapter
 * detail, storage usage and the Wi-Fi-only switch. Sibling of
 * `OfflineDownloadsScreen` (bounded offline playback, #341), reachable next
 * to it (player-independent here, since a book has no native player).
 */
@Composable
fun LibraryDownloadsScreen(
    origin: String,
    downloads: LibraryDownloads,
    onClose: () -> Unit,
) {
    val snapshot by downloads.state.collectAsState()
    var pendingRemoval by remember { mutableStateOf<LibraryBookRecord?>(null) }
    var expandedWorkId by remember { mutableStateOf<String?>(null) }

    val account = snapshot.account?.takeIf { it.origin == origin }
    val books = snapshot.accessibleBooks(origin).sortedBy { it.title }
    val usedBytes = books.sumOf { bookSizeOnDevice(downloads, it) }

    BackHandler(onBack = onClose)

    Surface(modifier = Modifier.fillMaxSize()) {
        Column(
            modifier = Modifier.fillMaxSize().padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = "Offline books",
                    style = MaterialTheme.typography.headlineSmall,
                    modifier = Modifier.weight(1f),
                )
                TextButton(onClick = onClose) { Text("Close") }
            }

            Text(
                text = "${OfflineStoragePolicy.formatBytes(usedBytes)} used on this device",
                style = MaterialTheme.typography.bodyMedium,
            )

            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(text = "Download over Wi-Fi only", modifier = Modifier.weight(1f))
                Switch(checked = snapshot.settings.wifiOnly, onCheckedChange = downloads::setWifiOnly)
            }

            HorizontalDivider()

            when {
                account == null || !account.signedIn -> Text(
                    text = "Sign in to Jularr to use this account's offline books.",
                    style = MaterialTheme.typography.bodyMedium,
                )

                books.isEmpty() -> Text(
                    text = "No offline books yet. Use \"Save offline\" on a book's page to keep it on this device.",
                    style = MaterialTheme.typography.bodyMedium,
                )

                else -> LazyColumn(modifier = Modifier.fillMaxWidth()) {
                    items(books, key = { it.workId }) { book ->
                        val chapters = downloads.chaptersOf(book.ownerKey, book.workId)
                        BookRow(
                            book = book,
                            chapters = chapters,
                            expanded = expandedWorkId == book.workId,
                            onToggleExpanded = {
                                expandedWorkId = if (expandedWorkId == book.workId) null else book.workId
                            },
                            onRemoveBook = { pendingRemoval = book },
                            onPauseChapter = downloads::pauseChapter,
                            onResumeChapter = downloads::resumeChapter,
                            onRetryChapter = downloads::retryChapter,
                            onRemoveChapter = downloads::removeChapter,
                        )
                        HorizontalDivider()
                    }
                }
            }
        }
    }

    pendingRemoval?.let { book ->
        AlertDialog(
            onDismissRequest = { pendingRemoval = null },
            title = { Text("Remove book?") },
            text = { Text("\"${book.title}\" is deleted from this device. Your reading progress is kept on the server.") },
            confirmButton = {
                TextButton(
                    onClick = {
                        downloads.removeBook(book)
                        pendingRemoval = null
                    },
                ) { Text("Remove") }
            },
            dismissButton = { TextButton(onClick = { pendingRemoval = null }) { Text("Keep") } },
        )
    }
}

@Composable
private fun BookRow(
    book: LibraryBookRecord,
    chapters: List<LibraryChapterRecord>,
    expanded: Boolean,
    onToggleExpanded: () -> Unit,
    onRemoveBook: () -> Unit,
    onPauseChapter: (LibraryChapterRecord) -> Unit,
    onResumeChapter: (LibraryChapterRecord) -> Unit,
    onRetryChapter: (LibraryChapterRecord) -> Unit,
    onRemoveChapter: (LibraryChapterRecord) -> Unit,
) {
    val status = LibraryBookStatus.of(chapters)
    val readyCount = chapters.count { it.isUpToDate }

    Column(modifier = Modifier.fillMaxWidth().padding(vertical = 12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(modifier = Modifier.weight(1f)) {
                Text(text = book.title, style = MaterialTheme.typography.titleMedium)
                book.author?.let { Text(text = it, style = MaterialTheme.typography.bodySmall) }
                Text(
                    text = "${statusLabel(status)} · $readyCount of ${chapters.size} chapters",
                    style = MaterialTheme.typography.bodySmall,
                    color = if (status == BookDownloadStatus.FAILED) {
                        MaterialTheme.colorScheme.error
                    } else {
                        MaterialTheme.colorScheme.onSurfaceVariant
                    },
                )
            }
            TextButton(onClick = onToggleExpanded) { Text(if (expanded) "Hide chapters" else "Chapters") }
            TextButton(onClick = onRemoveBook) { Text("Remove") }
        }

        if (expanded) {
            chapters.sortedBy { it.number }.forEach { chapter ->
                ChapterRow(
                    chapter = chapter,
                    onPause = { onPauseChapter(chapter) },
                    onResume = { onResumeChapter(chapter) },
                    onRetry = { onRetryChapter(chapter) },
                    onRemove = { onRemoveChapter(chapter) },
                )
            }
        }
    }
}

@Composable
private fun ChapterRow(
    chapter: LibraryChapterRecord,
    onPause: () -> Unit,
    onResume: () -> Unit,
    onRetry: () -> Unit,
    onRemove: () -> Unit,
) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(start = 16.dp, top = 4.dp, bottom = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(text = chapter.title, style = MaterialTheme.typography.bodyMedium)
            Text(
                text = chapterStatusLabel(chapter),
                style = MaterialTheme.typography.bodySmall,
                color = if (chapter.state == DownloadState.FAILED) {
                    MaterialTheme.colorScheme.error
                } else {
                    MaterialTheme.colorScheme.onSurfaceVariant
                },
            )
        }
        when (chapter.state) {
            DownloadState.QUEUED, DownloadState.DOWNLOADING -> OutlinedButton(onClick = onPause) { Text("Pause") }
            DownloadState.PAUSED -> OutlinedButton(onClick = onResume) { Text("Resume") }
            DownloadState.FAILED -> OutlinedButton(onClick = onRetry) { Text("Retry") }
            DownloadState.READY -> if (!chapter.isUpToDate) {
                OutlinedButton(onClick = onRetry) { Text("Update") }
            }
        }
        TextButton(onClick = onRemove) { Text("Remove") }
    }
}

private fun statusLabel(status: BookDownloadStatus): String =
    when (status) {
        BookDownloadStatus.QUEUED -> "Queued"
        BookDownloadStatus.DOWNLOADING -> "Downloading"
        BookDownloadStatus.PAUSED -> "Paused"
        BookDownloadStatus.FAILED -> "Download failed"
        BookDownloadStatus.AVAILABLE -> "Available offline"
        BookDownloadStatus.UPDATE_AVAILABLE -> "Update available"
    }

private fun chapterStatusLabel(chapter: LibraryChapterRecord): String =
    when (chapter.state) {
        DownloadState.QUEUED -> "Queued"
        DownloadState.DOWNLOADING -> "Downloading"
        DownloadState.PAUSED -> "Paused"
        DownloadState.FAILED -> chapter.failure ?: "Download failed"
        DownloadState.READY -> if (chapter.isUpToDate) "Downloaded" else "Update available"
    }

private fun bookSizeOnDevice(downloads: LibraryDownloads, book: LibraryBookRecord): Long {
    val chapterBytes = downloads.chaptersOf(book.ownerKey, book.workId)
        .filter { it.state == DownloadState.READY }
        .sumOf { downloads.store.chapterFile(it.ownerKey, it.chapterId).length() }
    val assetBytes = book.assetFileNames.sumOf { downloads.store.assetFile(book.ownerKey, it).length() }
    return chapterBytes + assetBytes
}
