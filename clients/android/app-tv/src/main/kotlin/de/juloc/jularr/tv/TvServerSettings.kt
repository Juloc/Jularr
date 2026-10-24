package de.juloc.jularr.tv

import android.content.Context

interface TvServerOriginStore {
    var origin: String?
    fun clear()
}

class TvServerSettings(context: Context) : TvServerOriginStore {
    private val preferences = context.getSharedPreferences(
        "jularr-tv",
        Context.MODE_PRIVATE,
    )

    override var origin: String?
        get() = preferences
            .getString(KEY_ORIGIN, null)
            ?.takeIf { it.isNotBlank() }
        set(value) {
            preferences.edit().apply {
                if (value.isNullOrBlank()) {
                    remove(KEY_ORIGIN)
                } else {
                    putString(KEY_ORIGIN, TvServerOrigin.normalize(value))
                }
            }.apply()
        }

    override fun clear() {
        preferences.edit().remove(KEY_ORIGIN).apply()
    }

    private companion object {
        const val KEY_ORIGIN = "server_origin"
    }
}
