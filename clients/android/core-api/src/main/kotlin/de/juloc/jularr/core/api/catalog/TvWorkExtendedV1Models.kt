package de.juloc.jularr.core.api.catalog

/**
 * Work editions are separate from WorkVersions and MediaAssets.
 * Edition availability describes stored files; the selected MediaAsset still comes from
 * /api/v1/works/{id}/availability, never from an edition's id.
 */
data class TvWorkEditionV1(
    val id: Long, // WorkEditionId
    val name: String?,
    val title: String?,
    val languageCode: String?,
    val editionFormatId: Int?,
    val publisher: String?,
    val releaseYear: Int?,
    val fileCount: Long,
    val isAvailable: Boolean,
)

/** A real person/credit from the catalog; roles remain server enum IDs. */
data class TvWorkCreditV1(
    val personId: Long,
    val name: String,
    val personRoleId: Int,
    val credit: String?,
    val imageUrl: String?,
)

/** Real catalog identity, not a Work or an episode. */
data class TvPersonV1(
    val id: Long,
    val name: String,
    val imageUrl: String?,
)
