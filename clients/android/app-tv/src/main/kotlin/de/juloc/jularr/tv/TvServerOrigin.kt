package de.juloc.jularr.tv

import java.net.URI

object TvServerOrigin {
    fun normalize(input: String): String {
        val trimmed = input.trim()
        require(trimmed.isNotEmpty()) { "Enter the Jularr server address." }

        val uri = URI(trimmed)
        require(uri.scheme.equals("http", true) || uri.scheme.equals("https", true)) {
            "The Jularr server address must start with http:// or https://."
        }
        require(!uri.host.isNullOrBlank()) {
            "The Jularr server address must include a host."
        }
        require(uri.userInfo == null && uri.query == null && uri.fragment == null) {
            "The Jularr server address cannot contain credentials, query parameters or a fragment."
        }
        require(uri.path.isNullOrBlank() || uri.path == "/") {
            "Enter only the Jularr server origin, without an application path."
        }

        return URI(
            uri.scheme.lowercase(),
            null,
            uri.host.lowercase(),
            uri.port,
            null,
            null,
            null,
        ).toString()
    }
}
