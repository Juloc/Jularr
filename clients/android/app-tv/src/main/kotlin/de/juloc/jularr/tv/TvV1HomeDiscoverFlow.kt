package de.juloc.jularr.tv

import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.catalog.TvCatalogV1Client
import de.juloc.jularr.core.api.catalog.TvDiscoverItemV1
import de.juloc.jularr.core.api.catalog.TvDiscoverShelfV1
import de.juloc.jularr.core.api.catalog.TvDiscoverScopeV1
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Recreates the Web Home recommendation feed from existing clean-start V1
 * Discover APIs. Scope/feed IDs are never substituted with another media type.
 *
 * Local shelves render immediately. Only missing film/series feed pairs are
 * queried from the external provider, concurrently but with a firm limit.
 * Provider outages never hide Continue Watching, local library or favourites.
 */
internal class TvV1HomeDiscoverFlow(
    private val catalog: TvCatalogV1Client,
    private val providerTimeoutMs: Long = 8_000L,
    private val providerConcurrency: Int = 4,
) {
    init {
        require(providerTimeoutMs in 1L..30_000L)
        require(providerConcurrency in 1..8)
    }

    suspend fun load(onUpdate: (List<TvV1HomeVideoFeed>) -> Unit): List<TvV1HomeVideoFeed> {
        val scopes = catalog.discoverScopes().filter {
            !it.isCatalog && (it.mediaTypeId == 1 || it.mediaTypeId == 2)
        }
        if (scopes.isEmpty()) {
            onUpdate(emptyList())
            return emptyList()
        }
        val cached = optionalProviderRead { catalog.discoverShelves() }
        val initial = tvV1HomeVideoFeeds(scopes, cached)
        onUpdate(initial)

        // An existing complete shelf is authoritative even when one item has
        // no WorkId: only provider-compatible, missing pairs are fetched.
        val available = initial.map { it.key }.toSet()
        val missing = scopes
            .filter { it.mediaSubtypeId == null }
            .flatMap { scope ->
                scope.feeds.filter { it in 1..4 }.distinct().map { feed -> scope to feed }
            }
            .distinctBy { (scope, feed) -> scope.slug to feed }
            .filterNot { (scope, feed) -> "${scope.slug}:$feed" in available }

        if (missing.isEmpty()) return initial
        val limit = Semaphore(providerConcurrency)
        val extra = coroutineScope {
            missing.map { (scope, feed) ->
                async {
                    limit.withPermit {
                        val items = optionalProviderRead {
                            withTimeoutOrNull(providerTimeoutMs) {
                                catalog.discoverFeed(scope.mediaTypeId, feed)
                            } ?: emptyList()
                        }
                        if (items.isEmpty()) null else TvDiscoverShelfV1(
                            scopeSlug = scope.slug,
                            discoverFeedTypeId = feed,
                            items = items,
                            catalogItems = emptyList(),
                        )
                    }
                }
            }.awaitAll().filterNotNull()
        }
        val complete = tvV1HomeVideoFeeds(scopes, cached + extra)
        onUpdate(complete)
        return complete
    }

    private suspend fun <T> optionalProviderRead(block: suspend () -> List<T>): List<T> =
        try {
            block()
        } catch (cancel: CancellationException) {
            throw cancel
        } catch (failure: AppApiException) {
            if (failure.statusCode == 401) throw failure
            emptyList()
        } catch (_: Exception) {
            emptyList()
        }
}
