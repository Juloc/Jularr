package de.juloc.jularr.mobile

import java.net.URI
import java.util.UUID

class ServerOrigin private constructor(
    val uri: URI,
) {
    val value: String = buildString {
        append(uri.scheme.lowercase())
        append("://")
        append(uri.host.lowercase())
        if (uri.port >= 0) {
            append(":")
            append(uri.port)
        }
    }

    fun isSameOrigin(candidate: URI): Boolean =
        uri.scheme.equals(candidate.scheme, true) &&
            uri.host.equals(candidate.host, true) &&
            effectivePort(uri) == effectivePort(candidate)

    fun resolveSameOrigin(pathOrUrl: String): URI {
        val resolved = uri.resolve(pathOrUrl)
        require(isSameOrigin(resolved)) {
            "URL is outside the configured Jularr origin."
        }
        return resolved
    }

    companion object {
        fun parse(raw: String): Result<ServerOrigin> = runCatching {
            val parsed = URI(raw.trim())
            require(parsed.scheme.equals("https", true) || parsed.scheme.equals("http", true)) {
                "Use an http:// or https:// Jularr address."
            }
            require(!parsed.host.isNullOrBlank()) {
                "Enter a valid Jularr server host."
            }
            require(parsed.userInfo == null && parsed.query == null && parsed.fragment == null) {
                "The server address cannot contain credentials, query or fragment."
            }
            require(parsed.path.isNullOrBlank() || parsed.path == "/") {
                "Enter only the Jularr server origin, without an app path."
            }

            ServerOrigin(
                URI(
                    parsed.scheme.lowercase(),
                    null,
                    parsed.host.lowercase(),
                    parsed.port,
                    "/",
                    null,
                    null,
                ),
            )
        }

        private fun effectivePort(uri: URI): Int =
            when {
                uri.port >= 0 -> uri.port
                uri.scheme.equals("https", true) -> 443
                else -> 80
            }
    }
}

sealed interface WebNavigationDecision {
    data object AllowInWebView : WebNavigationDecision
    data class OpenNativeEpisode(val episodeId: String) : WebNavigationDecision
    data class OpenExternal(val uri: URI) : WebNavigationDecision
    data object Block : WebNavigationDecision
}

object WebNavigationPolicy {
    private val episodePath = Regex(
        pattern = "^/Library/Episode/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/?$",
    )

    fun decide(
        origin: ServerOrigin,
        rawUrl: String,
    ): WebNavigationDecision {
        val uri = runCatching { URI(rawUrl) }.getOrNull()
            ?: return WebNavigationDecision.Block

        if (!origin.isSameOrigin(uri)) {
            return if (uri.scheme.equals("http", true) || uri.scheme.equals("https", true)) {
                WebNavigationDecision.OpenExternal(uri)
            } else {
                WebNavigationDecision.Block
            }
        }

        val match = episodePath.matchEntire(uri.path ?: "")
        if (match != null && isNativePlaybackRequest(uri)) {
            val episodeId = match.groupValues[1]
            if (runCatching { UUID.fromString(episodeId) }.isSuccess) {
                return WebNavigationDecision.OpenNativeEpisode(episodeId)
            }
        }

        return WebNavigationDecision.AllowInWebView
    }

    private fun isNativePlaybackRequest(uri: URI): Boolean =
        uri.rawQuery
            ?.split('&')
            ?.any { parameter ->
                val parts = parameter.split('=', limit = 2)
                parts.size == 2 &&
                    parts[0] == "native" &&
                    parts[1] == "1"
            } == true
}
