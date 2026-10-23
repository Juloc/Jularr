package de.juloc.jularr.tv

import de.juloc.jularr.core.api.tvlegacy.TvV1PlaybackAssetInfo

/**
 * Navigation/identity boundary for the clean-start TV entry.
 *
 * The original TvAppController/TvClientFlow are based on /api/client/v1 cookie
 * sessions. Never reuse that flow with a /api/v1 bearer token. This controller
 * keeps only real backend identities and original-TV navigation semantics.
 * IO remains in the existing V1 API clients, not in this navigation layer.
 */
internal object TvV1AppController {
    fun open(
        current: TvV1NavigationState,
        destination: TvV1CatalogPage,
        workId: Long? = null,
        personId: Long? = null,
        workUnitId: Long? = null,
    ): TvV1NavigationState =
        TvV1Navigation.open(current, destination, workId, personId, workUnitId)

    fun work(current: TvV1NavigationState, workId: Long): TvV1NavigationState {
        require(workId > 0) { "A V1 WorkId must be positive." }
        return open(current, TvV1CatalogPage.Details, workId = workId)
    }

    fun episode(
        current: TvV1NavigationState,
        workId: Long,
        workUnitId: Long,
        knownWorkUnits: Set<Long>,
    ): TvV1NavigationState {
        require(workId > 0 && workUnitId > 0)
        require(workUnitId in knownWorkUnits) {
            "The selected WorkUnitId is not part of the loaded Work."
        }
        return open(current, TvV1CatalogPage.Episode, workId = workId, workUnitId = workUnitId)
    }

    fun playback(
        current: TvV1NavigationState,
        mediaAssetId: Long,
        workId: Long?,
        workUnitId: Long?,
    ): TvV1NavigationState {
        require(mediaAssetId > 0) { "Playback requires a real MediaAssetId." }
        require(workId == null || workId > 0)
        require(workUnitId == null || workUnitId > 0)
        return open(current, TvV1CatalogPage.Player, workId = workId, workUnitId = workUnitId)
    }

    fun verifiedNeighbour(
        current: TvV1NavigationState,
        requestedAssetId: Long,
        neighbour: TvV1PlaybackAssetInfo,
    ): TvV1NavigationState {
        require(current.page == TvV1CatalogPage.Player) { "Only an active player may switch assets." }
        require(requestedAssetId > 0 && neighbour.mediaAssetId == requestedAssetId) {
            "Playback asset response did not match the requested MediaAssetId."
        }
        require(neighbour.workId > 0)
        // A new playback asset never inherits the previous WorkUnitId. Asset IDs are
        // *not* WorkUnit IDs. Keep the genuine WorkId returned by the asset endpoint.
        return current.copy(
            current = current.current.copy(workId = neighbour.workId, workUnitId = null),
        )
    }

    fun back(current: TvV1NavigationState): TvV1NavigationState = TvV1Navigation.back(current)

    /** Profile/account changes must discard routes belonging to the previous viewer. */
    fun clearForProfileSwitch(): TvV1NavigationState = TvV1NavigationState()
}
