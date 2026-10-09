# Plex sign-in foundation

Status: **draft implementation** in #912; feature flags default off. Not ready for general deployment until CI, live Plex validation and provider settings end-to-end QA are complete.

## Current slice

- Verified Plex PIN sign-in resolves the stable Plex user ID against Plex's account endpoint. Plex credentials never pass through Jularr.
- Every Plex identity is stored once in `AccountLoginIdentities` and belongs to one internal Jularr account.
- An existing local account may link Plex after entering its current Jularr password. Email or display name never links accounts automatically.
- A new Plex identity can request creation of a passwordless Jularr account only when auto-provisioning is enabled. The account is disabled until its per-user media capabilities have been safely capped.
- New account approval is required by default, even when auto-provisioning is enabled.
- Existing password login and the canonical Jularr authentication cookie remain unchanged.
- PIN attempts are short-lived, persisted, browser-nonce-bound and consumed once. Plex tokens are used transiently to verify the account ID, never stored as login identifiers or returned to the browser.

## Configuration (Admin → Providers → Plex)

| Variable | Default | Purpose |
| --- | --- | --- |
| `Plex__ClientIdentifier` | unset | Stable, instance-specific Plex client ID, e.g. a generated UUID without braces |
| `Plex__LoginEnabled` | `false` | Expose Sign in with Plex |
| `Plex__LinkEnabled` | `false` | Let authenticated users link Plex |
| `Plex__AutoProvisionEnabled` | `false` | Allow verified Plex IDs to create internal Jularr accounts |
| `Plex__RequireApproval` | `true` | Keep newly provisioned users disabled pending approval |

The existing shared Admin → Providers UI contains a Plex Identity provider with separate checkboxes for login, linking, auto-provision and approval. All are disabled by default, except Require Approval, which is enabled by default. The client identifier is generated and persisted under `/data/integrations/plex-identity.json` on the first Admin save. No Plex server or personal token is stored here.

For declarative deployments, the existing environment keys below take precedence over the UI. When one is present, Admin marks this provider as externally managed instead of providing a second conflicting settings path. Existing accounts/linked identities survive a provider toggle.

The default-off feature flags must remain disabled in production until sign-in, migrations, callback routing and tests are verified. Auto-provisioning is an explicit owner decision, not implied by enabling Plex Login.

Plex auth callbacks need an externally valid HTTPS URL with correct forwarded protocol/host. Local loopback HTTP is allowed for development only. A stable client identifier must persist across application restarts and replicas.

## Reusing verified Plex PINs for a separate media grant

The canonical `PlexAuthClient.ResolveVerifiedIdentityAsync` now makes the
server-verified stable Plex account ID available together with an **internal,
JSON-ignored** access token. Existing `ResolveAuthenticatedAccountIdAsync`
still uses the same verification method and returns only the stable ID.

**Important:** Calling the verifier does not connect Plex media, create a
Profile Connection or enable sync. Once both draft feature branches are
integrated, a separate, browser-bound, explicitly confirmed connection flow
must call the verified-credential result and `PlexProfileConnectionStore`.
Never copy the token into login identities, browser forms, URLs, Jularr
sessions, diagnostics or logs.

## Remaining work before release

- Verify Admin → Providers → Plex switches, authorization and persistence through a real browser session, then complete per-provider diagnostics. The current Test checks PIN endpoint availability, not an end-to-end user sign-in.
- The Settings index now links to personal Plex sign-in management when linking is enabled or already configured. Full Profile-scoped Plex Connections, server grants, and sync remain deferred.
- Plex media-server and library integration, item matching, scoped user access and `In Plex öffnen` (#886).
- Full live-device Plex API compatibility validation; current PIN flow is the documented legacy PIN variant and should be checked against Plex's newer signed-JWT device authentication.
- User-facing Plex pages now use canonical localizable UI resources; verify the supported locales and mobile/browser-return UX visually.

No one should claim Plex library sync, external playback or complete #911 based on this foundational PR alone.
