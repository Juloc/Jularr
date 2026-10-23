package de.juloc.jularr.mobile

import android.webkit.CookieManager

class WebSession(
    private val origin: ServerOrigin,
) {
    private val cookieManager: CookieManager
        get() = CookieManager.getInstance()

    fun requestHeaders(): Map<String, String> {
        cookieManager.flush()
        val cookie = cookieManager.getCookie(origin.value)
            ?.takeIf { it.isNotBlank() }
            ?: return emptyMap()

        return mapOf("Cookie" to cookie)
    }

    fun acceptResponseCookies(cookies: List<String>) {
        cookies.forEach { cookie ->
            cookieManager.setCookie(origin.value, cookie)
        }
        cookieManager.flush()
    }

    fun configureFor(webView: android.webkit.WebView) {
        cookieManager.setAcceptCookie(true)
        cookieManager.setAcceptThirdPartyCookies(webView, false)
    }
}
