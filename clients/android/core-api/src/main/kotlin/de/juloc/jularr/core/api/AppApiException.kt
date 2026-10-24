package de.juloc.jularr.core.api

import java.io.IOException

/**
 * A failed app call. [code] is the machine readable server code, [resourceKey] the key of the
 * localized message and [traceId] the reference a support answer can use. The server sends no
 * stack trace to a client.
 */
class AppApiException(
    val statusCode: Int,
    val code: String,
    val resourceKey: String,
    val traceId: String?,
    message: String,
) : IOException(message)
