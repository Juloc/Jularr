package de.juloc.jularr.mobile

import android.content.Context

class ServerSettings(context: Context) {
    private val preferences = context.getSharedPreferences(
        "jularr_mobile",
        Context.MODE_PRIVATE,
    )

    fun getOrigin(): String? =
        preferences.getString(KeyOrigin, null)?.takeIf { it.isNotBlank() }

    fun setOrigin(origin: ServerOrigin) {
        preferences.edit()
            .putString(KeyOrigin, origin.value)
            .apply()
    }

    fun clearOrigin() {
        preferences.edit()
            .remove(KeyOrigin)
            .apply()
    }

    private companion object {
        const val KeyOrigin = "server_origin"
    }
}
