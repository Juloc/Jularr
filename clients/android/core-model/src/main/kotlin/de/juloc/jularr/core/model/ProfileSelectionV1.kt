package de.juloc.jularr.core.model

/**
 * One profile the account of the device may use. A locked profile asks for its PIN before it
 * opens.
 */
data class ProfileSelectionV1(
    val id: Long,
    val displayName: String,
    val isLocked: Boolean,
    val pinLength: Int?,
    val isOwned: Boolean,
)
