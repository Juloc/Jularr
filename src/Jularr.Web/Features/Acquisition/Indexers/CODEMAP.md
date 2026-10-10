# Indexers

Direct Newznab connections and Prowlarr share one list (`IndexerStore`, `indexers.json`, API keys protected at rest). Usenet only.

## Owners

- `NewznabIndexer`: the only Newznab client. Builds `t=caps` / search requests (one comma-separated `cat`, no blank parameters, `/api` appended once), maps `<error>` documents (also HTTP 200) to `IndexerAuthenticationException`, `IndexerRateLimitedException` or `IndexerRequestRejectedException` (200-203: names the request shape, never the key).
- `IndexerCapabilities` / `NewznabCapsParser`: what the caps document says (functions, parameters, limits, category tree with names, server title).
- `IndexerCategoryMapper`: the one decision of which categories belong to which media type on one indexer: owner override, then names and the standard taxonomy for categories the indexer lists, then the standard default only for an indexer whose caps were never read. A type without a category is unavailable, never searched elsewhere; light novels share Books and are marked `Shared`.
- `IndexerSetupService`: Add (address + API key), Refresh (caps again, owner settings kept, a failed refresh keeps the working configuration) and Test search (bounded validation requests, one release of the newest feed, nothing is downloaded). Results are stored as `IndexerVerification`.
- `IndexerReadiness`: reads the stored verification into the levels and per-type states the Usenet page shows.
- `IndexerSearchCoordinator`: the one executor of Automatic and Manual Search. A refused structured function (`IndexerRequestRejectedException`) is dropped for the rest of the search and for an hour (`SearchEvidenceCache.RefuseMode`); the text fallbacks of the plan follow. A refused plain text search ends as `ParametersRejected`.

## Pages

`Pages/Legacy/Settings/Indexers/Edit` (address + key, advanced optional) and `Pages/Admin/Usenet` (cards, Test, Test search, Refresh capabilities). Wording of results: `Pages/Admin/IndexerSetupMessages`.

## Tests

`NewznabSetupTests` (requests emitted to the HTTP handler, setup, mapping, fallbacks, pages), `NewznabIndexerTests`, `IndexerCategoryTests`, `SearchPlannerTests`.
