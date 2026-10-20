package de.juloc.jularr.core.model

/**
 * The signed-in state of the device: the account, the profile it works as and the locale the
 * instance shows.
 */
data class AuthenticationV1(
    val accountId: Long?,
    val profileId: Long?,
    val uiLocaleId: Long?,
    val permissions: List<String>,
)
