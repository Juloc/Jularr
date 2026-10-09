# Plex media-server adapter — implementation slice for #886

Status: *read-only adapter / work matching only*. This **does not** enable the `In Plex öffnen` button yet.

## Canonical responsibilities

- `PlexResourceDiscoveryClient` enumerates owned and shared Plex Media Servers from the authenticated account's **official resources API**. It keeps each server access token internal and non-serializable, filters out non-server devices and non-HTTPS connections, and ranks local non-relay routes above relay routes. Discovery does not connect to arbitrary URLs or store secrets.
- `PlexServerSelectionService` requires that an Admin-selected HTTPS endpoint appears in the discovered server's connection list, and re-reads the actual accessible Plex libraries using that server's grant **before approving specific movie/TV section IDs**. Invalid or inaccessible IDs are rejected; no guesswork.
- `PlexServerGrantStore` persists the approved machine ID, endpoint and library IDs under `/data/integrations/plex-servers.json` with a Data Protection-encrypted server token. Server listing returns metadata only, and removing a grant deletes the encrypted local token. Simply discovering a server never writes a token.
- `PlexServerCatalogScanService` scans exactly one selected library page per call, with **AdminSystem authorization enforced in the service** and a bounded cursor. It returns read-only Plex rating keys and matching Jularr Work IDs, without changing media availability, request status or progress. No periodic scan is scheduled yet.
- `PlexWorkMatcher` matches the entire 200-item page with one database query; ambiguous IDs and episodes remain unmatched rather than causing mismatched playback links.
- `PlexServerSelectionService` now enforces **AdminSystem** itself for library discovery and approvals; UI visibility alone never authorizes a grant.
- `PlexLibraryClient` reads a **pre-approved HTTPS Plex Media Server endpoint** with a separately supplied access token. It lists movie/TV sections and paginates titles, requesting `includeGuids=1` to retrieve external provider IDs.
- HTTP client registration disables redirects before credentialed requests are sent. URLs, section IDs, tokens and client identifiers are validated, responses are limited to 8 MiB, and each page to 200 items.
- It accepts Plex GUID identifiers for TMDB, IMDb and TVDB only and ignores Plex-native title/ratingKey as evidence of Jularr identity. No filename, poster or title guessing.
- `PlexWorkMatcher` resolves Plex movie/show metadata to a *single* existing canonical Jularr Work using **confirmed** provider identities. If none or more than one Work match, returns no result. An episode cannot be silently treated as a matched series.
- No background sync, account sign-in, user access policy or request/progress mutation is performed by either adapter.

## Personal Plex media connections (separate from Account login)

`PlexProfileConnectionStore` now implements the persistence foundation for an explicitly consented Plex media connection, scoped to one Jularr profile:

- Stores one encrypted Plex access token and stable verified Plex user ID per profile in `/data/integrations/plex/profiles`. It uses the existing provider Data Protection convention and owner-only atomic credential files.
- Offers only token-free status (connected identity, usability and consent flags) to Settings. A lost key ring makes the media connection unusable until reauthorization; it does not silently attach another identity.
- Disconnect affects only the selected profile media grant, **not** the Account-level Plex sign-in identity in #912, other users' connections, watchlists, requests, or Jularr progress.
- Sync starts **off** and remains unavailable until an independently consented sync engine is implemented. A Plex sign-in never enables it automatically.
- `SaveVerifiedAsync` is a trusted backend API, **not** an HTTP endpoint. Before calling it, the future connection handler must verify the Plex token and stable account ID server-side, prove ownership of the current signed-in Jularr profile, confirm user intent, and meet admin policy. This integration is not yet implemented.
- `PlexProfileConnectionService` is the only intended surface for current-profile status, disconnection and media permissions in future web/client handlers. It derives the V1 profile identifier from the authenticated Jularr Account principal rather than accepting a caller-supplied profile ID.
- For a user to see a Plex library, both scopes must permit it: the Admin must approve the server and section, **and** the user's independently connected Plex token must be able to list that section on the approved endpoint. Admin tokens are never substituted for user tokens. Unauthorized, forbidden and not-found Plex responses yield no accessible libraries.
- This is a **library-access check**, not proof that an individual movie/episode is playable. Actual media-item verification and a deep link must be implemented before showing `In Plex öffnen`.


## Required before opening media in Plex

1. Use the discovery adapter in the admin-scoped Plex server connection flow, alongside the existing Account-scoped Plex login identity and a new Profile-scoped media Connection. Add explicit user consent, secure grants and revocation in the existing provider architecture. Encrypted tokens and revocation only; Plex login must not automatically imply sync.
2. Discover PMS resources using the authenticated Plex identity, verify server ownership or share permissions, and select exact machine IDs and libraries as Admin. Never accept a user-provided arbitrary server URL/token from a public route; test SSRF protections for the connection step. Do not expose token-bearing URLs to a browser.
3. Connect the confirmed server selection service to the existing **Admin → Providers** permissioned UI with a Plex authorization/consent flow and explicit server/library choice. The backend contracts exist, but no route currently invokes them; it cannot connect servers by itself.
4. Build a bounded reconciliation service with checkpointed paging/backoff and a persistent mapping of server/machine+ratingKey+WorkId and verified IDs, guarded by library access permissions.
5. At request time, *revalidate* that the signed-in user's Plex Connection can access the selected item. Only then expose `In Plex öffnen` as a secondary action in media details (#886), never as an alternate Request approval or Jularr Playback action.
6. For episodes match the exact Plex episode and Jularr WorkEpisode; do not fall back to a series without explicitly labeling the fallback.
7. Use provider-supported link patterns verified against a real Plex instance; do not invent a stable Plex deep-link contract.
8. Test with a real configured Plex test server, movies, shows, episodes and shared-library users. Also check client UX on mobile/desktop/TV, access revocation, SSRF and redirect handling.

## Credential lifecycle and failure behavior

- Plex server grants already use the common `ProviderCredentials.ProtectorFor`/ASP.NET Data Protection contract and atomic `/data/integrations` writes; no second token persistence service is introduced.
- If a restore or key-ring rotation makes a stored grant undecryptable, the grant is treated as disconnected instead of causing an unhandled cryptographic failure during scans. The administrator must reconnect. The token-free `IsUsableAsync` API supports connection status without exposing credentials.
- Server revocation goes through `PlexServerSelectionService.RevokeAsync` with the existing `AdminSystem` authorization policy; it removes the encrypted grant and prevents subsequent scans.
- Tests cover key loss, the continued visibility of nonsecret connection metadata, admin-only revocation and denial after revocation.

The server adapter can be integrated separately from the Plex login branch #912. Both reuse the universal Work model, not another media catalog.
