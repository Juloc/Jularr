package de.juloc.jularr.core.model

/**
 * The public display values of the instance: its name, the default locale and whether the
 * Devices and Registration modules work here. The client shows them before it pairs.
 */
data class InstancePresentationV1(
    val name: String,
    val uiLocaleId: Long,
    val publicUrl: String?,
    val devicesEnabled: Boolean,
    val registrationEnabled: Boolean,
)
