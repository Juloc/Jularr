package de.juloc.jularr.tv

/**
 * The texts of the server. The client shows the wording of the instance and keeps no own
 * strings; a key the instance does not know stays visible as its key.
 */
class TvTexts(private val values: Map<String, String>) {
    operator fun get(key: String): String = values[key] ?: key

    /** The text of a key, or a fallback when the instance does not know it. */
    fun get(key: String, fallback: String): String = values[key] ?: fallback

    fun has(key: String): Boolean = values.containsKey(key)

    companion object {
        val empty = TvTexts(emptyMap())
    }
}
