package de.juloc.jularr.tv

/**
 * Navigation for the /api/v1 entry path, mirroring the original TV's push/back
 * behavior rather than the discontinued desktop-style shell.
 *
 * Page history contains only UI destinations. The current WorkId, WorkUnitId,
 * MediaAssetId and token remain in the owning V1 controller/host.
 */
internal enum class TvV1CatalogPage {
    Home, Search, Discover, Watchlist, Settings, ProfileSelect,
    Details, MovieEditions, PlaybackPending, Player,
}

internal data class TvV1NavigationState(
    val page: TvV1CatalogPage = TvV1CatalogPage.Home,
    val previous: List<TvV1CatalogPage> = emptyList(),
)

internal object TvV1Navigation {
    fun open(state: TvV1NavigationState, next: TvV1CatalogPage): TvV1NavigationState {
        if (state.page == next) return state
        // The original TV sidebar swaps root pages instead of growing a backstack.
        if (next == TvV1CatalogPage.Home || next == TvV1CatalogPage.Watchlist ||
            next == TvV1CatalogPage.Settings
        ) return TvV1NavigationState(next)
        // Selecting a profile from Settings is an overlay-style transition.
        val stack = state.previous + state.page
        return TvV1NavigationState(next, stack.takeLast(16))
    }

    fun back(state: TvV1NavigationState): TvV1NavigationState {
        if (state.previous.isNotEmpty()) return TvV1NavigationState(
            page = state.previous.last(),
            previous = state.previous.dropLast(1),
        )
        val fallback = when (state.page) {
            TvV1CatalogPage.Home -> TvV1CatalogPage.Home
            TvV1CatalogPage.Search -> TvV1CatalogPage.Home
            TvV1CatalogPage.Discover -> TvV1CatalogPage.Search
            TvV1CatalogPage.MovieEditions, TvV1CatalogPage.PlaybackPending,
            TvV1CatalogPage.Player -> TvV1CatalogPage.Details
            TvV1CatalogPage.Details, TvV1CatalogPage.ProfileSelect,
            TvV1CatalogPage.Watchlist, TvV1CatalogPage.Settings -> TvV1CatalogPage.Home
        }
        return TvV1NavigationState(fallback)
    }
}
