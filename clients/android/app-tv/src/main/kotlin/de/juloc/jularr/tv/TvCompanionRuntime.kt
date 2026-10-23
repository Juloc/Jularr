package de.juloc.jularr.tv

import de.juloc.jularr.core.session.PlaybackCommand
import de.juloc.jularr.core.session.PlaybackPairing
import de.juloc.jularr.core.session.PlaybackSessionClient
import de.juloc.jularr.core.session.PlaybackSessionHubConnection
import de.juloc.jularr.core.session.PlaybackSessionOwner
import de.juloc.jularr.core.session.PlaybackSessionSnapshot
import de.juloc.jularr.core.session.PlaybackSessionUpdate

class TvCompanionRuntime(
    serverOrigin: String,
    requestHeaders: () -> Map<String, String>,
) : AutoCloseable {
    private val client = PlaybackSessionClient(
        serverOrigin = serverOrigin,
        requestHeaders = requestHeaders,
    )

    private var owner: PlaybackSessionOwner? = null
    private var hub: PlaybackSessionHubConnection? = null

    @Synchronized
    fun start(
        episodeId: String,
        initial: PlaybackSessionUpdate,
        onState: (PlaybackSessionSnapshot) -> Unit,
        onCommand: (PlaybackCommand) -> Unit,
        onEnded: () -> Unit,
    ): PlaybackSessionSnapshot {
        closeHub()
        val created = client.create(episodeId, initial)
        owner = created

        hub = client.connectOwnerHub(
            owner = created,
            onState = { state ->
                synchronized(this) {
                    owner = owner?.copy(state = state)
                }
                onState(state)
            },
            onCommand = onCommand,
            onEnded = {
                synchronized(this) {
                    owner = null
                    closeHub()
                }
                onEnded()
            },
        )

        return created.state
    }

    @Synchronized
    fun state(): PlaybackSessionSnapshot? = owner?.state

    @Synchronized
    fun update(update: PlaybackSessionUpdate): PlaybackSessionSnapshot? {
        val active = owner ?: return null
        val next = client.update(
            sessionId = active.state.sessionId,
            expectedRevision = active.state.revision,
            state = update,
        )
        owner = active.copy(state = next)
        return next
    }

    @Synchronized
    fun createPairing(): PlaybackPairing {
        val active = owner
            ?: error("No companion playback session is active.")
        return client.createPairing(active.state.sessionId)
    }

    @Synchronized
    fun revoke() {
        val active = owner ?: return
        client.revoke(active.state.sessionId)
    }

    @Synchronized
    fun end() {
        val active = owner
        owner = null
        closeHub()

        if (active != null) {
            client.end(active.state.sessionId)
        }
    }

    @Synchronized
    override fun close() {
        runCatching { end() }
        closeHub()
    }

    private fun closeHub() {
        runCatching { hub?.close() }
        hub = null
    }
}
