package de.juloc.jularr.tv

/**
 * V1 navigation reuses original TV screen surfaces. Typed identities are stored in the
 * Back stack to prevent Person -> Work -> Back accidentally showing a different Work.
 * Never conflate WorkId, WorkUnitId or MediaAssetId.
 */
internal enum class TvV1CatalogPage {
    Home, Search, Discover, Watchlist, Settings, Statistics, ProfileSelect,
    Details, Episode, Credits, Person, MovieEditions, PlaybackPending, Player,
}

internal data class TvV1NavigationEntry(
    val page: TvV1CatalogPage,
    val workId: Long? = null,
    val personId: Long? = null,
    val workUnitId: Long? = null,
) {
    init {
        require(workId == null || workId > 0)
        require(personId == null || personId > 0)
        require(workUnitId == null || workUnitId > 0)
    }
}

internal data class TvV1NavigationState(
    val current: TvV1NavigationEntry = TvV1NavigationEntry(TvV1CatalogPage.Home),
    val previous: List<TvV1NavigationEntry> = emptyList(),
) {
    val page: TvV1CatalogPage get() = current.page
}

internal object TvV1Navigation {
    fun open(
        state: TvV1NavigationState,
        next: TvV1CatalogPage,
        workId: Long? = null,
        personId: Long? = null,
        workUnitId: Long? = null,
    ): TvV1NavigationState {
        val destination = TvV1NavigationEntry(next, workId, personId, workUnitId)
        if (state.current == destination) return state
        // The original TV sidebar replaces roots instead of retaining other accounts' history.
        if (next == TvV1CatalogPage.Home || next == TvV1CatalogPage.Watchlist ||
            next == TvV1CatalogPage.Settings
        ) return TvV1NavigationState(destination)
        // A route's exact identity travels with the backstack.
        val stack = state.previous + state.current
        return TvV1NavigationState(destination, stack.takeLast(16))
    }

    fun back(state: TvV1NavigationState): TvV1NavigationState {
        if (state.previous.isNotEmpty()) return TvV1NavigationState(
            current = state.previous.last(),
            previous = state.previous.dropLast(1),
        )
        val fallback = when (state.page) {
            TvV1CatalogPage.Home -> TvV1CatalogPage.Home
            TvV1CatalogPage.Search -> TvV1CatalogPage.Home
            TvV1CatalogPage.Discover -> TvV1CatalogPage.Search
            TvV1CatalogPage.MovieEditions, TvV1CatalogPage.PlaybackPending,
            TvV1CatalogPage.Player -> TvV1CatalogPage.Details
            TvV1CatalogPage.Episode, TvV1CatalogPage.Credits -> TvV1CatalogPage.Details
            TvV1CatalogPage.Person -> TvV1CatalogPage.Credits
            TvV1CatalogPage.Statistics -> TvV1CatalogPage.Settings
            TvV1CatalogPage.Details, TvV1CatalogPage.ProfileSelect,
            TvV1CatalogPage.Watchlist, TvV1CatalogPage.Settings -> TvV1CatalogPage.Home
        }
        return TvV1NavigationState(TvV1NavigationEntry(fallback))
    }
}
