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
    Search("search"),
    Details("details"),
    Player("player"),
    Languages("languages"),
    Collections("collections"),
}

/** Where the focus is now; the D-pad moves between the areas in the reading order. */
enum class TvFocusArea {
    Rail,
    TopRow,
    Content,

    /** A list of the screen: the settings, the collections, the profiles. */
    List,

    /** The search of the television: the field, the suggestions and the titles of the instance. */
    Search,
}

/** The directions of the D-pad. */
enum class TvFocusDirection {
    Left,
    Right,
    Up,
    Down,
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
    /** The asset to play, when the title can be played at once. */
    val mediaAssetId: Long? = null,
    /** The work of the title, when the instance knows it. */
    val workId: Long? = null,
    /** The wide picture of the title, for the hero of the desktop. */
    val backdropUrl: String? = null,
    /** The year of the title. */
    val year: Int? = null,
    /** The text of the title. */
    val overview: String? = null,
)

/** One shelf of the home page: its heading and its cards. */
data class TvShelf(
    val key: String,
    val title: String,
    val cards: List<TvCard>,
)
