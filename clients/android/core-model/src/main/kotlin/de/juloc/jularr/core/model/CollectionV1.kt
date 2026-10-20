package de.juloc.jularr.core.model

/**
 * One collection of the profile: a named group of titles, either chosen by hand or assembled
 * by the instance from the rules of the collection.
 */
data class CollectionV1(
    val id: Long,
    val name: String,
    val isSmart: Boolean,
    val mediaTypeId: Int?,
    val workCount: Int?,
    /** The version of the collection; the instance expects it when it changes. */
    val version: Long = 0,
)
