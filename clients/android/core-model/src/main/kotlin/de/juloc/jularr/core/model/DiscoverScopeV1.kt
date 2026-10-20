package de.juloc.jularr.core.model

/**
 * One Discover scope: the media type the client may browse, its address in the type capsule and
 * the feeds it offers.
 */
data class DiscoverScopeV1(
    val slug: String,
    val mediaTypeId: Int,
    val mediaSubtypeId: Int?,
    val isCatalog: Boolean,
    val feeds: List<Int>,
)
