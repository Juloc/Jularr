package de.juloc.jularr.tv

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import org.json.JSONArray
import org.json.JSONObject

data class TvSavedSession(
    val id: String,
    val serverOrigin: String,
    val userName: String,
    val cookies: Map<String, String> = emptyMap(),
    val lastActiveAtMillis: Long = System.currentTimeMillis(),
)

class TvSessionStore(context: Context) {
    private val preferences = context.getSharedPreferences(
        PREFS_NAME,
        Context.MODE_PRIVATE,
    )

    init {
        val legacy = preferences.getString(KEY_SESSIONS, null)
        if (legacy != null) {
            try {
                check(preferences.edit()
                    .putString(KEY_ENCRYPTED_SESSIONS, encrypt(legacy))
                    .remove(KEY_SESSIONS)
                    .commit())
            } catch (exception: Exception) {
                preferences.edit()
                    .remove(KEY_SESSIONS)
                    .remove(KEY_ENCRYPTED_SESSIONS)
                    .commit()
                throw IllegalStateException("Could not safely migrate stored TV sessions.", exception)
            }
        }
    }

    @Synchronized
    fun getSessions(): List<TvSavedSession> {
        val encoded = preferences.getString(KEY_ENCRYPTED_SESSIONS, null) ?: return emptyList()
        val rawJson = runCatching { decrypt(encoded) }.getOrElse { return emptyList() }
        return runCatching {
            val array = JSONArray(rawJson)
            val list = mutableListOf<TvSavedSession>()
            for (i in 0 until array.length()) {
                val obj = array.getJSONObject(i)
                val cookieObj = obj.optJSONObject("cookies")
                val cookies = mutableMapOf<String, String>()
                cookieObj?.keys()?.forEach { key ->
                    cookies[key] = cookieObj.getString(key)
                }

                list.add(
                    TvSavedSession(
                        id = obj.getString("id"),
                        serverOrigin = obj.getString("serverOrigin"),
                        userName = obj.getString("userName"),
                        cookies = cookies,
                        lastActiveAtMillis = obj.optLong("lastActiveAtMillis", System.currentTimeMillis()),
                    ),
                )
            }
            list.sortedByDescending { it.lastActiveAtMillis }
        }.getOrDefault(emptyList())
    }

    @Synchronized
    fun getActiveSession(): TvSavedSession? {
        val activeId = preferences.getString(KEY_ACTIVE_ID, null)
        if (activeId == NEW_SIGN_IN) return null
        val sessions = getSessions()
        return if (activeId == null) sessions.firstOrNull() else sessions.firstOrNull { it.id == activeId }
    }

    @Synchronized
    fun setActiveSessionId(id: String) {
        preferences.edit().putString(KEY_ACTIVE_ID, id).apply()
        val sessions = getSessions().map {
            if (it.id == id) it.copy(lastActiveAtMillis = System.currentTimeMillis()) else it
        }
        persistSessions(sessions)
    }

    @Synchronized
    fun beginNewSignIn() {
        preferences.edit().putString(KEY_ACTIVE_ID, NEW_SIGN_IN).apply()
    }

    @Synchronized
    fun saveSession(session: TvSavedSession) {
        val current = getSessions().toMutableList()
        current.removeAll { it.id == session.id }
        current.add(0, session.copy(lastActiveAtMillis = System.currentTimeMillis()))
        persistSessions(current)
        preferences.edit().putString(KEY_ACTIVE_ID, session.id).apply()
    }

    @Synchronized
    fun updateCookiesForActiveSession(cookies: Map<String, String>) {
        val active = getActiveSession() ?: return
        val updated = active.copy(
            cookies = cookies,
            lastActiveAtMillis = System.currentTimeMillis(),
        )
        val current = getSessions().toMutableList()
        current.removeAll { it.id == active.id }
        current.add(0, updated)
        persistSessions(current)
    }

    @Synchronized
    fun removeSession(id: String) {
        val current = getSessions().toMutableList()
        current.removeAll { it.id == id }
        persistSessions(current)
        if (preferences.getString(KEY_ACTIVE_ID, null) == id) {
            val next = current.firstOrNull()?.id
            preferences.edit().putString(KEY_ACTIVE_ID, next).apply()
        }
    }

    @Synchronized
    fun clearAll() {
        preferences.edit().clear().apply()
    }

    private fun persistSessions(sessions: List<TvSavedSession>) {
        val array = JSONArray()
        for (session in sessions) {
            val obj = JSONObject()
            obj.put("id", session.id)
            obj.put("serverOrigin", session.serverOrigin)
            obj.put("userName", session.userName)
            obj.put("lastActiveAtMillis", session.lastActiveAtMillis)

            val cookieObj = JSONObject()
            session.cookies.forEach { (k, v) ->
                cookieObj.put(k, v)
            }
            obj.put("cookies", cookieObj)

            array.put(obj)
        }
        check(preferences.edit().putString(KEY_ENCRYPTED_SESSIONS, encrypt(array.toString())).commit()) {
            "Could not save encrypted TV sessions."
        }
    }

    private fun secretKey(): SecretKey {
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (keyStore.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return generator.generateKey()
    }

    private fun encrypt(plainText: String): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, secretKey())
        val data = cipher.doFinal(plainText.toByteArray(Charsets.UTF_8))
        return Base64.encodeToString(cipher.iv + data, Base64.NO_WRAP)
    }

    private fun decrypt(encoded: String): String {
        val payload = Base64.decode(encoded, Base64.NO_WRAP)
        require(payload.size > 12 + 16) { "Invalid session ciphertext." }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, secretKey(), GCMParameterSpec(128, payload.copyOfRange(0, 12)))
        return cipher.doFinal(payload.copyOfRange(12, payload.size)).toString(Charsets.UTF_8)
    }

    private companion object {
        const val PREFS_NAME = "jularr-tv-sessions"
        const val KEY_SESSIONS = "saved_sessions"
        const val KEY_ENCRYPTED_SESSIONS = "encrypted_sessions"
        const val KEY_ACTIVE_ID = "active_session_id"
        const val KEY_ALIAS = "jularr-tv-sessions-key"
        const val NEW_SIGN_IN = "new-sign-in"
    }
}
