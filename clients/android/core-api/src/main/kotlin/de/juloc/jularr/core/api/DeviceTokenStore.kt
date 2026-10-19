package de.juloc.jularr.core.api

/**
 * Holds the device token of a paired device. The server renews the token past half its
 * lifetime and returns the new one with the answer, so every answer updates this store.
 */
class DeviceTokenStore {
    @Volatile
    private var token: String? = null

    val current: String?
        get() = token

    fun update(value: String?) {
        if (!value.isNullOrEmpty()) {
            token = value
        }
    }

    fun clear() {
        token = null
    }
}
