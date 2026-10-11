# Phase B — external media providers

Target only; PR 920 Plex and PR 937 Jellyfin remain their actual implementation
sources. No live server/ACL/client validation is claimed by this SQL draft.
Providers is the adapter registry, not a closed numeric enum. Its independent
login/link/media/provision gates are the sole default-off flags. PlexSettings
adds only client identifier and approval-on policy, no second set of flags.
Notification transports are not
metadata/media Providers.

## Consent and secrets

- Account login identities and Profile media consent remain independent. Auth's
  typed browser-bound challenge owns the PIN lifecycle. Media cannot create an
  Account, enable synchronization or modify progress/watchlist/request facts.
- `ProviderMediaConnections` records explicit consent, external identity and a
  revisioned opaque reference to the existing protected credential owner.
  `ProviderServerGrants` separately records Owner approval, verified external
  server owner, exact discovered HTTPS endpoint and explicitly selected sections.
- Secret files hold encrypted credential payload only, never a second mutable
  copy of grant/config/checkpoint metadata. New references are server-generated;
  no token, caller path, signed URL or token-bearing endpoint is stored here.
  Lost key rings render a connection unusable; no Admin-token substitution.
- Service performs authenticated discovery, ownership and exact endpoint/section
  verification outside the DB transaction. URI/DNS/redirect/SSRF protections are
  still mandatory in the adapter; a SQL endpoint shape CHECK is not SSRF proof.
- Logic locks Account before connection/grant/checkpoint. Reconnect uses expected
  revision CAS with a staged credential reference; disconnect increments revision
  and revokes pending media consent atomically. A late response cannot resurrect
  a revoked connection. Grant/section changes increment the grant revision and
  invalidate old catalog generations. No I/O inside that transaction.

## Catalog reconciliation and access

- A manual authorized scan uses the existing low-priority maintenance Operation,
  not another scheduler/queue. Each source fetch is at most 200 items, at most
  four pages per invocation. No recurring scan or synchronization is enabled by
  this schema. Current Owner role, grant/section/Provider, Operation lease and
  trusted worker are rechecked when committing each page.
- Logic first locks the current grant/checkpoint, checks expected generation,
  grant revision and cursor, then inserts the verified page set and advances the
  cursor in ONE short transaction. A zero-row CAS rolls the page back. Deferred
  integrity rejects an unaccepted page at COMMIT. Retried cursors cannot advance
  twice; an explicit restart deletes old derived items before incrementing the
  long generation. Completed scans have a null next cursor and completion time.
- `ProviderCatalogItems` is only the bounded-scan derived candidate map. Sources
  are current grant/section plus provider metadata positively matched against
  confirmed `WorkExternalIdentities`. Ambiguous identities are not mapped. An
  episode requires its exact WorkEpisode; never silently substitute its series.
  Cache rows confer no remote access, local file availability or entitlement.
- Internal candidate SQL rechecks enabled Account, current Profile membership,
  explicit media flag/connection and current grant generation. It deliberately
  omits protected references/endpoints/tokens. Service then verifies the exact
  item with that user's credential, intersects approved and user-accessible
  sections, and rechecks revisions before returning a token-free details link.
  List/detail GET does not invoke provider I/O; explicit external-open may.
- Admin grant lists are Owner-only, paged before bounded section aggregation.
  UI selection caps approved sections at 100. Outward candidates also use checked
  Page/Size/Offset and unique ordering. No whole-library fetch/filter in memory.

Revocation retains auditable metadata but makes the route unusable immediately.
Explicit retention deletes derived items, checkpoints, sections and then grants;
canonical Works/progress survive. Plex/Jellyfin protocol and client behavior
must pass the real configured-server tests in later integration, not be inferred
from these relational fixtures.
