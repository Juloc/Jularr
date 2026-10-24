# Phone Companion V1 — implementation contract

Scope: issue #1508. This slice builds backend protocol and responsive browser phone remote. The native TV adapter and QR HUD remain exclusively in PR #1540, which must not import legacy `/api/client/v1` controllers or the old SignalR hub.

## Ownership and security

- The canonical `PlaybackSessions` row owns the playback lifecycle. The existing V1 playback-start service records the real authenticated owner `AccountSessions` identity and revision on session creation. Only a non-revoked `AndroidTV` device owner may offer invitations.
- Existing `AccountSessions`, the service gate and profile selection own authentication/authorization. A phone requires its **own** authenticated Jularr browser session (cookie + antiforgery) or a separate authenticated device session. An invitation alone never grants API access.
- The TV owner generates a random 256-bit, 5-minute invitation, stored SHA-256 hashed in PostgreSQL. A newer invitation replaces the old one; one claim atomically consumes the code. A claim requires **same account, same active profile**, a different session than the TV, valid credential revisions and an active playback session. No TV bearer leaves its device.
- A revocable grant UUID names a controller; it is **not** an independent bearer secret. Each command/read checks that the grant's creating controller session, its revision and active profile match the current caller and that the TV owner session is still current and the playback is open.
- All writes use the existing service transaction and Postgres tables (migration `0002_playback_companion.sql`). No second playback database, in-memory authorization map, cookie-based legacy fallback, or new hub.

## Endpoint contract

All paths below have prefix `/api/v1`; body and response are typed `V1` data. Browser mutations require the ordinary request antiforgery token, and a paired TV sends its existing rotating bearer.

| Actor | Method | Route | Input / result |
|---|---|---|---|
| TV | POST | `/playback/sessions/{playbackSessionId}/companion/invitations` | New `token,expiresAt` |
| Phone | POST | `/playback/companion/claims` | `{token}` → `{id,playbackSessionId}` |
| Phone | GET | `/playback/companion/{companionId}` | Real TV state, revision and last command result |
| Phone | POST | `/playback/companion/{companionId}/commands` | `{commandId,expectedRevision,action,positionMs}` → `{id,revision}` |
| Phone | DELETE | `/playback/companion/{companionId}` | Revokes that controller grant |
| TV | GET | `/playback/sessions/{playbackSessionId}/companion` | Current authoritative state and revision |
| TV | PUT | `/playback/sessions/{playbackSessionId}/companion/state` | `{revision,isPlaying,positionMs,durationMs}` |
| TV | POST | `/playback/sessions/{playbackSessionId}/companion/next` | Atomically takes next command or empty result |
| TV | PUT | `/playback/sessions/{playbackSessionId}/companion/commands/{commandId}` | `{revision,succeeded,isPlaying,positionMs,durationMs}` |
| TV | DELETE | `/playback/sessions/{playbackSessionId}/companion` | Revokes invitations and grants for this playback |

Only three actions are accepted: `play`, `pause`, `seek` (absolute milliseconds). The phone sends a UUID as idempotency key and the revision it read. A stale revision, missing recent TV heartbeat or an outstanding unexpired command rejects new control writes. Commands expire in 12 seconds, are delivered **at most once** and are never optimistically counted as successful. The TV must execute the real player action before acknowledging it. If an HTTP response is lost after dispatch, there is intentionally no automatic replay.

The authenticated web UI is at `/Companion`. The TV displays a QR to `https://<jularr-origin>/companion-pair.html#invite=<token>`; the public static landing clears the URL fragment before redirecting to the ordinary authenticated Companion page, preserving the short-lived one-time token only in same-origin sessionStorage across the login redirect. The Companion page clears the stored invitation before attempting a claim and uses only `/api/v1` with the phone\'s own session. It polls state every 2 seconds, disables controls when reported state is over 15 seconds old, shows connection loss and acknowledgement failures, and supports explicit disconnect.

Per-IP limits: invite/claim 10/min, command 30/min, reads/state/dispatch 120/min. Expiry, profile switches, token rotations, logouts, playback end and revocation are checked from persistent database state rather than relying on app notification.

## TV adapter for PR #1540 (only these hooks)

1. Upon playback start, keep the existing V1 `playbackSessionId` and V1 bearer. For “Phone remote” create an invitation and render its QR and expiration in the existing player HUD; allow replacement.
2. When the TV starts a companion session, read the current revision. Report **real native player** `isPlaying/positionMs/durationMs` every ~2s, and again after actual transitions; refresh revision after a conflict.
3. Poll `companion/next` while the session is active; send recognized play/pause/absolute seek to the current Media3 player. Acknowledge with actual resulting state; send `succeeded=false` when execution fails. Never use legacy playback-session API or SignalR.
4. On Stop/End, end the canonical playback session. On profile switch, sign-out, account swap and explicit remote disconnect, revoke companions where connectivity permits; server-side checks protect against crashes. Drop command delivery on a different/new playback session. No second TV coordinator or duplicate backend rules.

## Acceptance/verification

Critical automated cases: invitation once/expiry/replacement; owner-only invitation; same-account/profile proof; command UUID replay and stale revision; single delivery; acknowledgement from the TV; grant revoke; playback end; TV token rotation. Further coverage to add before release: wrong profile/account with independent session credentials, parallel claims under contention, controller rotation and revocation while commands are in flight, network partition between dispatch and ack, and native device/phone integration.

Release acceptance additionally requires **real Android TV + phone E2E**: scan QR, play/pause/seek, multiple accounts/profiles, network drop/reconnect, logout/end, interrupted command, and displayed command feedback. This PR alone does not pass that hardware gate. A Draft PR must not be merged on implementation existence alone.

All new routes have versioned typed contracts; legacy routes remain untouched here and are removed only by the TV owner in PR #1540.
