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
import java.util.UUID
import org.json.JSONArray
import org.json.JSONObject

data class TvSavedSession(
    val id: String,
    val serverOrigin: String,
    val userName: String,
    val cookies: Map<String, String> = emptyMap(),
    val lastActiveAtMillis: Long = System.currentTimeMillis(),
)

/**
 * Storage boundary for clean-start device credentials. Implementations must encrypt credentials
 * at rest; app code never places them in the old cookie session map.
 */
interface TvDeviceTokenPersistence {
    fun loadToken(serverOrigin: String): String?
    fun storeToken(serverOrigin: String, token: String?)
}

/** A slot is local and opaque: it is NOT an account identity invented from a device token. */
data class TvV1SavedAccount(val id: String, val label: String, val hasToken: Boolean)

interface TvV1AccountPersistence : TvDeviceTokenPersistence {
    fun listV1Accounts(serverOrigin: String): List<TvV1SavedAccount>
    fun ensureV1Account(serverOrigin: String): String
    fun createV1Account(serverOrigin: String): String
    fun activateV1Account(serverOrigin: String, id: String)
    fun loadV1AccountToken(serverOrigin: String, id: String): String?
    fun storeV1AccountToken(serverOrigin: String, id: String, token: String?)
}

class TvSessionStore(context: Context) : TvV1AccountPersistence {
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

    /**
     * Paired-device bearer tokens are kept in a separate encrypted object, keyed by server origin.
     * The legacy account/cookie store remains unchanged while the UI is being migrated.
     */
    @Synchronized
    override fun loadToken(serverOrigin: String): String? =
        readDeviceTokens().optString(TvServerOrigin.normalize(serverOrigin))
            .takeIf { it.isNotBlank() }

    @Synchronized
    override fun storeToken(serverOrigin: String, token: String?) {
        val origin = TvServerOrigin.normalize(serverOrigin)
        val saved = readDeviceTokens()
        if (token.isNullOrBlank()) saved.remove(origin) else saved.put(origin, token)
        check(preferences.edit()
            .putString(KEY_ENCRYPTED_DEVICE_TOKENS, encrypt(saved.toString()))
            .commit()) {
            "Could not save encrypted TV device tokens."
        }
    }

    // New encrypted V1 slots; independent of the original /api/client/v1 cookie accounts.
    // The token callback ALWAYS captures a slot ID, so an in-flight response for a
    // previous viewer cannot write its rotated bearer into the newly selected slot.
    private fun readV1Accounts(): JSONObject =
        preferences.getString(KEY_ENCRYPTED_V1_ACCOUNTS, null)
            ?.let { JSONObject(decrypt(it)) } ?: JSONObject()

    private fun saveV1Accounts(data: JSONObject) {
        check(preferences.edit()
            .putString(KEY_ENCRYPTED_V1_ACCOUNTS, encrypt(data.toString()))
            .commit()) { "Unable to persist encrypted TV account slots." }
    }

    private fun section(data: JSONObject, origin: String): JSONObject? =
        data.optJSONObject(TvServerOrigin.normalize(origin))

    @Synchronized
    override fun listV1Accounts(serverOrigin: String): List<TvV1SavedAccount> {
        val data = readV1Accounts()
        val group = section(data, serverOrigin) ?: return emptyList()
        val slots = group.optJSONArray("slots") ?: return emptyList()
        return (0 until slots.length()).map { i ->
            val slot = slots.getJSONObject(i)
            TvV1SavedAccount(
                id = slot.getString("id"),
                label = slot.getString("label"),
                hasToken = slot.optString("token").isNotBlank(),
            )
        }
    }

    @Synchronized
    override fun ensureV1Account(serverOrigin: String): String {
        val origin = TvServerOrigin.normalize(serverOrigin)
        val data = readV1Accounts()
        val existing = section(data, origin)
        if (existing != null) {
            val id = existing.getString("active")
            require((0 until existing.getJSONArray("slots").length()).any {
                existing.getJSONArray("slots").getJSONObject(it).getString("id") == id
            }) { "Active TV account not found." }
            return id
        }

        // One-time compatibility migration of a previously paired per-origin V1 token.
        val legacy = readDeviceTokens()
        val migrated = legacy.optString(origin).takeIf(String::isNotBlank)
        val id = UUID.randomUUID().toString()
        val slots = JSONArray().put(JSONObject().put("id", id)
            .put("label", "Anmeldung 1").put("token", migrated ?: ""))
        data.put(origin, JSONObject().put("active", id).put("slots", slots))
        // Commit both values together so the old token cannot be restored into a
        // different account after the slot-based store becomes authoritative.
        legacy.remove(origin)
        check(preferences.edit()
            .putString(KEY_ENCRYPTED_V1_ACCOUNTS, encrypt(data.toString()))
            .putString(KEY_ENCRYPTED_DEVICE_TOKENS, encrypt(legacy.toString()))
            .commit()) { "Unable to migrate encrypted device credentials." }
        return id
    }

    @Synchronized
    override fun createV1Account(serverOrigin: String): String {
        val origin = TvServerOrigin.normalize(serverOrigin)
        ensureV1Account(origin)
        val data = readV1Accounts()
        val group = requireNotNull(section(data, origin))
        val slots = group.getJSONArray("slots")
        val id = UUID.randomUUID().toString()
        slots.put(JSONObject().put("id", id)
            .put("label", "Anmeldung ${slots.length() + 1}").put("token", ""))
        group.put("active", id)
        saveV1Accounts(data)
        return id
    }

    @Synchronized
    override fun activateV1Account(serverOrigin: String, id: String) {
        val data = readV1Accounts()
        val group = requireNotNull(section(data, serverOrigin)) { "TV server has no saved accounts." }
        val slots = group.getJSONArray("slots")
        require((0 until slots.length()).any { slots.getJSONObject(it).getString("id") == id }) {
            "Unknown TV account slot."
        }
        group.put("active", id)
        saveV1Accounts(data)
    }

    @Synchronized
    override fun loadV1AccountToken(serverOrigin: String, id: String): String? {
        val group = section(readV1Accounts(), serverOrigin) ?: return null
        val slots = group.getJSONArray("slots")
        for (i in 0 until slots.length()) {
            val slot = slots.getJSONObject(i)
            if (slot.getString("id") == id) return slot.optString("token").takeIf(String::isNotBlank)
        }
        error("Unknown TV account slot.")
    }

    @Synchronized
    override fun storeV1AccountToken(serverOrigin: String, id: String, token: String?) {
        val data = readV1Accounts()
        val group = requireNotNull(section(data, serverOrigin))
        val slots = group.getJSONArray("slots")
        for (i in 0 until slots.length()) {
            val slot = slots.getJSONObject(i)
            if (slot.getString("id") == id) {
                slot.put("token", token ?: "")
                saveV1Accounts(data)
                return
            }
        }
        error("Unknown TV account slot.")
    }

    private fun readDeviceTokens(): JSONObject {
        val encrypted = preferences.getString(KEY_ENCRYPTED_DEVICE_TOKENS, null)
            ?: return JSONObject()
        // An undecryptable credential store is not silently overwritten with empty credentials.
        return JSONObject(decrypt(encrypted))
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
        const val KEY_ENCRYPTED_DEVICE_TOKENS = "encrypted_device_tokens"
        const val KEY_ENCRYPTED_V1_ACCOUNTS = "encrypted_device_account_slots_v1"
        const val KEY_ACTIVE_ID = "active_session_id"
        const val KEY_ALIAS = "jularr-tv-sessions-key"
        const val NEW_SIGN_IN = "new-sign-in"
    }
}
