# Phase B — Auth lifecycle

Target persistence, not installed runtime. Auth owns Account credentials,
sessions and challenges; Profile owns media preferences. Internal IDs/joins
are long/bigint. Session, challenge and passkey management expose separate UUIDs;
UUIDs confer no access. Bearers/nonces/recovery codes use at least 32 random
bytes; SHA-256 digests are fixed 32-byte values. Passwords use the established
password-specific hasher, never the fast token digest. TOTP secrets and transient
protocol state are protected by the existing encryption/key-ring owner.

## Transactions and concurrency

- Service verifies credentials, protocol signatures, time windows and current
  capability flags. Only Logic performs DML. No HTTP/key-store I/O inside a DB
  transaction. Anonymous response shapes never disclose Account existence.
- Account mutations lock the Account before credential/session/challenge rows.
  Bound credential changes and invalidation share one short transaction.
  `CredentialRevision` is the single invalidation epoch, not a second session
  state table. Every session READ checks it and current Account enabled state.
- Refresh rotates the hash in the same session by old hash + rotation revision
  CAS. The old bearer becomes unusable; concurrent refresh has one winner.
  A failed response does not reinstate an old token or create a grace fallback.
- Challenge consumption requires exact purpose, nonce digest, expiry and bound
  Account revision. Consumption and the effect commit or roll back together.
  TTL/rate limits are purpose-specific Auth policy, not arbitrary schema defaults.
- TOTP verifies the actual RFC algorithm/window before committing a strictly
  newer accepted time step. Recovery codes are independently one-use; consumption
  and recovery effect share the transaction. Rotation deletes/replaces old codes
  explicitly; no code/plaintext secret appears in a READ DTO or log.
- WebAuthn verifies origin, RP ID, UV policy, signed challenge and public key with
  the existing protocol library. Logic locks Account/challenge/credential,
  consumes the challenge, and updates flags/counter atomically. A positive
  counter must increase; authenticators with zero counters are not rejected
  merely for having no counter. Revoked credentials cannot sign in.
- Profile transfer changes owner membership atomically. Before removing an
  Account/Profile link, Logic clears affected sessions' `ActiveProfileId` and
  revokes in-flight media consent. Endpoint/provider routing rechecks the live
  link; no stored permission snapshot grants continued access.

## External identity vs media consent

`AccountAuthChallenges` is the sole expiring/consumed lifecycle. Its typed
`AccountExternalAuthFlows` detail stores provider PIN, client identifier,
server-verified external identity, explicit Profile context and local return
path; never provider access tokens. Login, linking and media consent have
different fixed byte purpose codes. No free-text `media-final` state or second
Plex attempt queue survives the runtime port.

Login/link only writes `AccountExternalLogins`; media consent only writes
`ProviderMediaConnections`. Media needs the same enabled Account, browser nonce
and linked Profile, verified external identity and independent Admin media flag.
Login/link/media flags and account auto-provision remain off by default in the
canonical provider settings. Linking also needs recent local reauthentication;
provisioning retains explicit approval/capability policy. Disconnecting one
domain cannot unlink another or import watchlist/progress.

Protected credential storage is prepared outside the transaction using a new
opaque server-owned reference. Logic claims the challenge, rechecks policy and
current connection revision, and commits the new reference once. Failed CAS
discards the staged credential; a crash leaves only an unreferenced protected
object for cleanup, never a restored revoked grant. Network verification happens
before that transaction. Actual Plex/server/client behavior still requires the
controlled live integration validation; SQL fixtures do not prove it.
