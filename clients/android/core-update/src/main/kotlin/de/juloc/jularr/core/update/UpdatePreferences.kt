package de.juloc.jularr.core.update

import android.content.Context

/**
 * Per-app, device-only update-check settings: the enable toggle, the last-check
 * timestamp (so app start only checks when stale) and the non-nagging dismissal (so a
 * declined version is not offered again until a newer one is published). Never synced
 * to the Jularr server: this mirrors how [de.juloc.jularr.mobile.ServerSettings] and the
 * TTS provider/model choice are device-local facts, not profile-scoped state.
 */
class UpdatePreferences(context: Context) {
    private val preferences = context.applicationContext
        .getSharedPreferences(PreferencesName, Context.MODE_PRIVATE)

    var checksEnabled: Boolean
        get() = preferences.getBoolean(KeyEnabled, true)
        set(value) {
            preferences.edit().putBoolean(KeyEnabled, value).apply()
        }

    var lastCheckedAtMillis: Long
        get() = preferences.getLong(KeyLastChecked, 0L)
        set(value) {
            preferences.edit().putLong(KeyLastChecked, value).apply()
        }

    /** The version last dismissed with "Later"; `null` once a newer version is offered. */
    var dismissedVersion: String?
        get() = preferences.getString(KeyDismissedVersion, null)
        set(value) {
            preferences.edit().putString(KeyDismissedVersion, value).apply()
        }

    fun shouldPrompt(availableVersion: String): Boolean = dismissedVersion != availableVersion

    companion object {
        private const val PreferencesName = "jularr_update_prefs"
        private const val KeyEnabled = "checks_enabled"
        private const val KeyLastChecked = "last_checked_at"
        private const val KeyDismissedVersion = "dismissed_version"

        /** How often an app-start check may run automatically; manual "Check now" ignores this. */
        const val CheckIntervalMillis: Long = 24L * 60 * 60 * 1000

        fun isCheckDue(lastCheckedAtMillis: Long, nowMillis: Long = System.currentTimeMillis()): Boolean =
            nowMillis - lastCheckedAtMillis >= CheckIntervalMillis
    }
}
