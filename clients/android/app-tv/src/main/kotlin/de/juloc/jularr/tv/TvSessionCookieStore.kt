package de.juloc.jularr.tv

class TvSessionCookieStore(
    private val onCookiesChanged: ((Map<String, String>) -> Unit)? = null,
) {
    private val cookies = linkedMapOf<String, String>()

    @Synchronized
    fun loadCookies(map: Map<String, String>) {
        cookies.clear()
        cookies.putAll(map)
    }

    @Synchronized
    fun getRawCookies(): Map<String, String> = HashMap(cookies)

    @Synchronized
    fun accept(setCookieHeaders: List<String>) {
        for (header in setCookieHeaders) {
            val pair = header.substringBefore(';').trim()
            val separator = pair.indexOf('=')
            if (separator <= 0) continue

            val name = pair.substring(0, separator).trim()
            val value = pair.substring(separator + 1).trim()
            if (name.isEmpty()) continue

            val expires = header.lowercase()
            val removesCookie = value.isEmpty() ||
                "max-age=0" in expires ||
                "max-age=-1" in expires

            if (removesCookie) {
                cookies.remove(name)
            } else {
                cookies[name] = value
            }
        }
        onCookiesChanged?.invoke(cookies)
    }

    @Synchronized
    fun requestHeaders(): Map<String, String> =
        if (cookies.isEmpty()) {
            emptyMap()
        } else {
            mapOf(
                "Cookie" to cookies.entries.joinToString("; ") { (name, value) ->
                    "$name=$value"
                },
            )
        }

    @Synchronized
    fun clear() {
        cookies.clear()
        onCookiesChanged?.invoke(emptyMap())
    }

    @Synchronized
    fun isEmpty(): Boolean = cookies.isEmpty()
}
