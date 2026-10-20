package de.juloc.jularr.tv

import android.content.Context
import de.juloc.jularr.core.api.DeviceTokenStorage

/**
 * The settings of the app on this television: the address of the instance, the skin and the
 * brightness. They belong to the device, not to the profile.
 */
class TvSettings(context: Context) {
    private val preferences = context.getSharedPreferences(Name, Context.MODE_PRIVATE)

    /** The address of the instance, without a trailing slash. */
    var address: String
        get() = preferences.getString(Address, "")?.trimEnd('/').orEmpty()
        set(value) {
            preferences.edit().putString(Address, value.trim().trimEnd('/')).apply()
        }

    /** The skin of the instance the television shows. */
    var skin: String
        get() = preferences.getString(Skin, DefaultSkin) ?: DefaultSkin
        set(value) {
            preferences.edit().putString(Skin, value).apply()
        }

    /** The brightness: the television shows the dark mode of the desktop. */
    var dark: Boolean
        get() = preferences.getBoolean(Dark, true)
        set(value) {
            preferences.edit().putBoolean(Dark, value).apply()
        }

    private companion object {
        const val Name = "jularr.tv"
        const val Address = "address"
        const val Skin = "skin"
        const val Dark = "dark"
        const val DefaultSkin = "clean"
    }
}

/**
 * Keeps the device token of the paired device between two starts of the app, so the
 * television does not ask for a new pairing every evening.
 */
class TvTokenStorage(context: Context) : DeviceTokenStorage {
    private val preferences = context.getSharedPreferences("jularr.tv", Context.MODE_PRIVATE)

    override fun read(): String? = preferences.getString(Token, null)

    override fun write(token: String) {
        preferences.edit().putString(Token, token).apply()
    }

    override fun clear() {
        preferences.edit().remove(Token).apply()
    }

    private companion object {
        const val Token = "device-token"
    }
}
