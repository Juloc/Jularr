# Plex sign-in foundation

Status: **draft implementation** in #912; feature flags default off. Not ready for general deployment until CI, live Plex validation and Admin/User connection UI are complete.

## Current slice

- Verified Plex PIN sign-in resolves the stable Plex user ID against Plex's account endpoint. Plex credentials never pass through Jularr.
- Every Plex identity is stored once in `AccountLoginIdentities` and belongs to one internal Jularr account.
- An existing local account may link Plex after entering its current Jularr password. Email or display name never links accounts automatically.
- A new Plex identity can request creation of a passwordless Jularr account only when auto-provisioning is enabled. The account is disabled until its per-user media capabilities have been safely capped.
- New account approval is required by default, even when auto-provisioning is enabled.
- Existing password login and the canonical Jularr authentication cookie remain unchanged.
- PIN attempts are short-lived, persisted, browser-nonce-bound and consumed once. Plex tokens are used transiently to verify the account ID, never stored as login identifiers or returned to the browser.

## Configuration (environment variables)

| Variable | Default | Purpose |
| --- | --- | --- |
| `Plex__ClientIdentifier` | unset | Stable, instance-specific Plex client ID, e.g. a generated UUID without braces |
| `Plex__LoginEnabled` | `false` | Expose Sign in with Plex |
| `Plex__LinkEnabled` | `false` | Let authenticated users link Plex |
| `Plex__AutoProvisionEnabled` | `false` | Allow verified Plex IDs to create internal Jularr accounts |
| `Plex__RequireApproval` | `true` | Keep newly provisioned users disabled pending approval |

The default-off feature flags must remain disabled in production until sign-in, migrations, callback routing and tests are verified. Auto-provisioning is an explicit owner decision, not implied by enabling Plex Login.

Plex auth callbacks need an externally valid HTTPS URL with correct forwarded protocol/host. Local loopback HTTP is allowed for development only. A stable client identifier must persist across application restarts and replicas.

## Deferred after this slice

- Admin → Providers UI for Plex login/connection/auto-provisioning flags, consent and integration diagnostics.
- User Settings → Connections UI and Profile-scoped Plex sync grants.
- Plex media-server and library integration, item matching, scoped user access and `In Plex öffnen` (#886).
- Full live-device Plex API compatibility validation; current PIN flow is the documented legacy PIN variant and should be checked against Plex's newer signed-JWT device authentication.
- Polished localization and cross-device sign-in/browser-return UX.

No one should claim Plex library sync, external playback or complete #911 based on this foundational PR alone.
