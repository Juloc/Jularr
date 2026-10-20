package de.juloc.jularr.core.model

/**
 * One language of the interface: its name, its locale and the direction it is written in.
 */
data class UiLocaleV1(
    val id: Long,
    val locale: String,
    val name: String,
    val isRightToLeft: Boolean,
)
