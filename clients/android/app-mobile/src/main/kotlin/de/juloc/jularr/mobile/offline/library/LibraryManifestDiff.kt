package de.juloc.jularr.mobile.offline.library

import de.juloc.jularr.core.model.ClientOfflineLibraryChapterRef
import de.juloc.jularr.core.model.ClientOfflineLibraryManifest

sealed interface ManifestChapterDiff {
    /** Chapter reference in the manifest, whatever the local state; nothing to change. */
    data class Keep(val chapter: LibraryChapterRecord) : ManifestChapterDiff

    /** New chapter, or the manifest hash changed since the local copy was verified. */
    data class Download(val ref: ClientOfflineLibraryChapterRef) : ManifestChapterDiff

    /** No longer in the manifest, or deselected by the user; the local copy is deleted. */
    data class Remove(val chapter: LibraryChapterRecord) : ManifestChapterDiff
}

/**
 * Differential detection between what is already stored for a book and its
 * current manifest (mirrors the PWA engine's `diffManifest`, see
 * docs/OFFLINE_LIBRARY.md): a chapter whose hash is unchanged is left alone
 * regardless of its local download state; a new or changed hash is queued for
 * (re)download; a chapter no longer wanted is queued for removal. Unchanged
 * chapters are never re-fetched.
 */
object LibraryManifestDiff {
    fun diff(
        stored: List<LibraryChapterRecord>,
        manifest: ClientOfflineLibraryManifest,
        wants: (String) -> Boolean,
    ): List<ManifestChapterDiff> {
        val storedById = stored.associateBy { it.chapterId }
        val manifestIds = manifest.chapters.mapTo(HashSet()) { it.chapterId }

        val decisions = mutableListOf<ManifestChapterDiff>()

        manifest.chapters.forEach { ref ->
            val existing = storedById[ref.chapterId]
            decisions += when {
                !wants(ref.chapterId) -> existing?.let { ManifestChapterDiff.Remove(it) }
                existing == null -> ManifestChapterDiff.Download(ref)
                existing.hash != ref.hash -> ManifestChapterDiff.Download(ref)
                else -> ManifestChapterDiff.Keep(existing)
            } ?: return@forEach
        }

        stored.forEach { chapter ->
            if (chapter.chapterId !in manifestIds) {
                decisions += ManifestChapterDiff.Remove(chapter)
            }
        }

        return decisions
    }

    /** Whether re-fetching the manifest is even worth diffing chapter-by-chapter. */
    fun hasChanged(storedContentVersion: String?, manifest: ClientOfflineLibraryManifest): Boolean =
        storedContentVersion != manifest.contentVersion
}
