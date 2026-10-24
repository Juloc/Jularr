package de.juloc.jularr.core.session

import com.microsoft.signalr.HubConnection
import com.microsoft.signalr.HubConnectionBuilder
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URI
import java.net.URL

class PlaybackSessionClient(
    private val serverOrigin: String,
    private val requestHeaders: () -> Map<String, String>,
) {
    private val base =
        serverOrigin.trimEnd('/') + "/api/client/v1/playback-sessions"

    fun create(
        episodeId: String,
        state: PlaybackSessionUpdate,
    ): PlaybackSessionOwner =
        request(
            method = "POST",
            path = "",
            body = JSONObject()
                .put("episodeId", episodeId)
                .put("state", state.toJson()),
        ).toOwner()

    fun update(
        sessionId: String,
        expectedRevision: Long,
        state: PlaybackSessionUpdate,
    ): PlaybackSessionSnapshot =
        request(
            method = "PUT",
            path = "/$sessionId/state",
            body = JSONObject()
                .put("expectedRevision", expectedRevision)
                .put("state", state.toJson()),
        ).toSnapshot()

    fun createPairing(sessionId: String): PlaybackPairing =
        request(
            method = "POST",
            path = "/$sessionId/pairing",
            body = JSONObject(),
        ).toPairing()

    fun revoke(sessionId: String) {
        requestNoContent(
            method = "POST",
            path = "/$sessionId/revoke",
        )
    }

    fun end(sessionId: String) {
        requestNoContent(
            method = "DELETE",
            path = "/$sessionId",
        )
    }

    fun connectOwnerHub(
        owner: PlaybackSessionOwner,
        onState: (PlaybackSessionSnapshot) -> Unit,
        onCommand: (PlaybackCommand) -> Unit,
        onEnded: () -> Unit,
    ): PlaybackSessionHubConnection {
        val url = resolve(owner.hubUrl)
        val hub = HubConnectionBuilder
            .create(url)
            .withHeaders(requestHeaders())
            .build()

        hub.on(
            "SessionState",
            { value: PlaybackSessionSnapshot -> onState(value) },
            PlaybackSessionSnapshot::class.java,
        )
        hub.on(
            "Command",
            { value: PlaybackCommand -> onCommand(value) },
            PlaybackCommand::class.java,
        )
        hub.on(
            "SessionEnded",
            { _: String -> onEnded() },
            String::class.java,
        )

        hub.start().blockingAwait()
        return PlaybackSessionHubConnection(hub)
    }

    private fun request(
        method: String,
        path: String,
        body: JSONObject?,
    ): JSONObject {
        val connection = open(path)
        connection.requestMethod = method
        connection.setRequestProperty("Accept", "application/json")

        if (body != null) {
            connection.doOutput = true
            connection.setRequestProperty("Content-Type", "application/json")
            connection.outputStream.bufferedWriter(Charsets.UTF_8).use {
                it.write(body.toString())
            }
        }

        val status = connection.responseCode
        val raw = readBody(connection, status)
        if (status !in 200..299) {
            val message = runCatching {
                JSONObject(raw).optString("message")
            }.getOrNull()
                ?.takeIf { it.isNotBlank() }
                ?: "Playback session request failed with HTTP $status."
            throw PlaybackSessionRequestException(status, message)
        }

        return if (raw.isBlank()) JSONObject() else JSONObject(raw)
    }

    private fun requestNoContent(
        method: String,
        path: String,
    ) {
        val connection = open(path)
        connection.requestMethod = method
        connection.setRequestProperty("Accept", "application/json")

        val status = connection.responseCode
        val raw = readBody(connection, status)
        if (status !in 200..299) {
            val message = runCatching {
                JSONObject(raw).optString("message")
            }.getOrNull()
                ?.takeIf { it.isNotBlank() }
                ?: "Playback session request failed with HTTP $status."
            throw PlaybackSessionRequestException(status, message)
        }
    }

    private fun open(path: String): HttpURLConnection =
        (URL(base + path).openConnection() as HttpURLConnection).apply {
            connectTimeout = 10_000
            readTimeout = 15_000
            instanceFollowRedirects = false
            useCaches = false
            requestHeaders().forEach { (name, value) ->
                setRequestProperty(name, value)
            }
        }

    private fun resolve(relativeOrAbsolute: String): String {
        val candidate = URI(relativeOrAbsolute)
        return if (candidate.isAbsolute) {
            candidate.toString()
        } else {
            URI(serverOrigin.trimEnd('/') + "/")
                .resolve(relativeOrAbsolute.removePrefix("/"))
                .toString()
        }
    }

    private fun readBody(
        connection: HttpURLConnection,
        status: Int,
    ): String {
        val stream = if (status in 200..399) {
            connection.inputStream
        } else {
            connection.errorStream
        } ?: return ""

        return stream.bufferedReader(Charsets.UTF_8).use { it.readText() }
    }
}

class PlaybackSessionHubConnection internal constructor(
    private val hub: HubConnection,
) : AutoCloseable {
    override fun close() {
        hub.stop().blockingAwait()
    }
}

class PlaybackSessionRequestException(
    val statusCode: Int,
    message: String,
) : IllegalStateException(message)

private fun PlaybackSessionUpdate.toJson() = JSONObject()
    .put("animeTitle", animeTitle)
    .put("episodeTitle", episodeTitle)
    .put("positionMs", positionMs)
    .put("durationMs", durationMs ?: JSONObject.NULL)
    .put("isPlaying", isPlaying)
    .put("playbackRate", playbackRate)
    .put("audioTrackId", audioTrackId ?: JSONObject.NULL)
    .put("subtitleTrackId", subtitleTrackId ?: JSONObject.NULL)
    .put("currentCueId", currentCueId ?: JSONObject.NULL)
    .put("currentCueText", currentCueText ?: JSONObject.NULL)
    .put(
        "currentCueTokens",
        JSONArray().apply {
            currentCueTokens.forEach { put(it.toJson()) }
        },
    )
    .put("selectedTermId", selectedTermId ?: JSONObject.NULL)

private fun PlaybackSessionToken.toJson() = JSONObject()
    .put("surface", surface)
    .put("termId", termId ?: JSONObject.NULL)
    .put("canonical", canonical ?: JSONObject.NULL)
    .put("reading", reading ?: JSONObject.NULL)
    .put("meaning", meaning ?: JSONObject.NULL)
    .put("state", state)

private fun JSONObject.toOwner() = PlaybackSessionOwner(
    state = getJSONObject("state").toSnapshot(),
    hubUrl = getString("hubUrl"),
)

private fun JSONObject.toPairing() = PlaybackPairing(
    sessionId = getString("sessionId"),
    code = getString("code"),
    token = getString("token"),
    companionUrl = getString("companionUrl"),
    expiresAtUtc = getString("expiresAtUtc"),
)

private fun JSONObject.toSnapshot() = PlaybackSessionSnapshot(
    sessionId = getString("sessionId"),
    episodeId = getString("episodeId"),
    animeTitle = optString("animeTitle"),
    episodeTitle = optString("episodeTitle"),
    positionMs = getLong("positionMs"),
    durationMs = nullableLong("durationMs"),
    isPlaying = getBoolean("isPlaying"),
    playbackRate = getDouble("playbackRate"),
    audioTrackId = nullableString("audioTrackId"),
    subtitleTrackId = nullableString("subtitleTrackId"),
    currentCueId = nullableLong("currentCueId"),
    currentCueText = nullableString("currentCueText"),
    currentCueTokens = getJSONArray("currentCueTokens").toTokens(),
    selectedTermId = nullableString("selectedTermId"),
    revision = getLong("revision"),
    updatedAtUtc = getString("updatedAtUtc"),
)

private fun JSONArray.toTokens(): List<PlaybackSessionToken> =
    buildList(length()) {
        for (index in 0 until length()) {
            val token = getJSONObject(index)
            add(
                PlaybackSessionToken(
                    surface = token.getString("surface"),
                    termId = token.nullableString("termId"),
                    canonical = token.nullableString("canonical"),
                    reading = token.nullableString("reading"),
                    meaning = token.nullableString("meaning"),
                    state = token.optString("state", "new"),
                ),
            )
        }
    }

private fun JSONObject.nullableString(name: String): String? =
    if (isNull(name)) null else optString(name).takeIf { it.isNotBlank() }

private fun JSONObject.nullableLong(name: String): Long? =
    if (isNull(name) || !has(name)) null else getLong(name)
