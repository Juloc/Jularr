package de.juloc.jularr.core.api

/**
 * Keeps the device token of a paired device between two starts of the app. The app owns the
 * storage; the client only reads and writes the token.
 */
interface DeviceTokenStorage {
    fun read(): String?

    fun write(token: String)

    fun clear()
}

/**
 * Holds the device token of a paired device. The server renews the token past half its
 * lifetime and returns the new one with the answer, so every answer updates this store.
 */
class DeviceTokenStore(private val storage: DeviceTokenStorage? = null) {
    @Volatile
    private var token: String? = storage?.read()

    val current: String?
        get() = token

    fun update(value: String?) {
        if (!value.isNullOrEmpty()) {
            token = value
            storage?.write(value)
        }
    }

    fun clear() {
        token = null
        storage?.clear()
    }
}
