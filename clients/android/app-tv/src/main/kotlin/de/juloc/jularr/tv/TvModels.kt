package de.juloc.jularr.tv

/** What the app shows now. */
enum class TvPhase {
    /** The address of the instance is asked for. */
    Setup,

    /** The pairing waits for the account to confirm the code. */
    Pairing,

    /** The instance answered; the shell shows the library. */
    Ready,
}

/** The surfaces of the television: the rail carries them. */
enum class TvRoute(val key: String) {
    Home("home"),
    Watchlist("watchlist"),
    Library("library"),
    Profiles("profiles"),
    Settings("settings"),
}

/**
 * One card of a shelf. The client shows what the server says: the title, the poster, the
 * meta line of the work and how far it is watched, read or listened to.
 */
data class TvCard(
    val id: String,
    val title: String,
    val posterUrl: String?,
    val meta: String?,
    val progress: Float?,
    val availabilityStateId: Int?,
)

/** One shelf of the home page: its heading and its cards. */
data class TvShelf(
    val key: String,
    val title: String,
    val cards: List<TvCard>,
)
