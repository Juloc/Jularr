package de.juloc.jularr.core.api

/**
 * Holds the current device bearer token. The Android client supplies decrypted state and a
 * persistence callback backed by Android Keystore; this shared module never writes plain tokens.
 * A missing token header does not invalidate a current session. Call clear() after revocation.
 */
class DeviceTokenStore(
    initialToken: String? = null,
    private val onChanged: (String?) -> Unit = {},
) {
    @Volatile
    private var token: String? = initialToken?.takeIf { it.isNotBlank() }

    val current: String?
        get() = token

    @Synchronized
    fun update(value: String?) {
        if (!value.isNullOrBlank() && value != token) {
            onChanged(value)
            token = value
        }
    }

    @Synchronized
    fun clear() {
        if (token != null) {
            onChanged(null)
            token = null
        }
    }
}
