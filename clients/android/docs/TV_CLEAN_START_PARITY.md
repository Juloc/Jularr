# Original Android TV → clean-start: verified parity / launch blockers

**Original UI**: `feat/android-tv-integrated-906-20261009`. **Integration**: PR #1468,
stacked on #1459/#1452. Media/catalog contract PR #1462, playback extras #1474.

This is a code/contract status, not a claim of Android TV hardware playback success.
Do not replace the original TV screens or add a backend endpoint without owner discussion.

## Recovered in the new V1 Android entry path

| Original function | V1 path / state |
| --- | --- |
| Server setup, six-digit pairing | `/api/v1/devices/pairings`, claim, device bearer; encrypted persistence |
| Device token refresh / revocation | Shared `DeviceTokenStore`, compare-and-rotate; 401 returns to pairing |
| Home, search, local library, video filters | `/api/v1/library`, video-only server filters |
| Continue Watching | `/api/v1/continue`, real work/asset/time identity; video only |
| Watchlist | `/api/v1/watchlist`, read + toggle in original details |
| Work, seasons, episodes, specials | `/api/v1/works/*`; WorkId and WorkUnitId retained; fractional episode numbers not truncated |
| Original episode detail UX | Restored V1 episode page with real WorkUnit metadata, media asset and resume progress; no fabricated vocabulary totals |
| Work credits, people and filmography | Actual catalog `/api/v1/works/{id}/credits`, `/api/v1/people/{id}` and person Works; movie/series filters |
| TV navigation and Back | Stack-aware V1 state machine for Search → Discover → Work → Episode/Player → Back, preserving original focus surfaces |
| Movie media choices | Work availability; only real playable MediaAssetId entries |
| Discover movie/series and TMDB details | Server discover scopes/shelves/search, source TMDB details; external IDs never reused as local WorkId |
| Video | Server playback sessions, authenticated Media3 Direct/HLS, heartbeat/final progress |
| Profile PIN selection | `/api/v1/profiles`, `auth/profile/{id}`, PIN endpoint; bearer revalidated |
| Server sign-out | `DELETE /api/v1/auth/session` then locally clears bearer |
| TV settings / app updates | Original TvProfileScreen + original APK update components |
| Playback preferences | `GET/PUT /api/v1/playback/preferences`; old settings edits only editable fields |
| Skip markers / chapters | Real asset markers/chapters, existing player UI controls |
| Trickplay sprites | Real V1 sheet geometry and 1-based JPEG URLs, same origin bearer |
| Audio track selection | Real V1 track keys; selected embedded stream index passed in a NEW playback session |
| Subtitle text | Real supported stream/sidecar WebVTT, attached to Media3 from app-private cache and deleted on dispose |

## Still not parity or not verified

1. **Controller**: `TvAppController`, `TvClientFlow`, `TvAppHost` still depend on old
   `JularrClientApi`, cookie sessions and `/api/client/v1`. The active app uses
   `TvV1CatalogHost` with original screen composables. Full legacy navigation state,
   account lists, history, and every original feature are **not** restored yet.
   The active V1 path now has tested stack-aware navigation, original TV detail
   composables, and a V1 episode detail page built from verified V1 DTOs.
2. **Multiaccount / device identity**: current encrypted tokens are scoped by *server
   origin*, not account identity. Additional sign-ins to the same Jularr server do not
   yet have the old multiaccount account-picking behavior. No username is fabricated.
3. **Discover**: provider search in the active TV screen currently covers movie/series.
   The API adapter supports books/manga/other external catalog types and person/credit
   details for non-video catalog types still need dedicated TV screens; film/series
   Work credits, person details and local filmography are now wired.
4. **Home**: full personalized shelf composition is not yet parity. HomeLayout exposes
   visibility/order; it does not materialize arbitrary recommendation rows.
5. **Playback**: the Media3 + V1 extras wiring has no confirmed real-device/live-server
   verification for HLS segment token rotations, codecs, FFmpeg remux, stop/restart,
   audio switching and subtitle track rendering.
6. **Special media failure scenarios**: NAS offline, storage wake, network loss during a
   session, app kill/resume, viewer switching and error recovery require device tests.
7. **Companion / TV learning overlay**: original word-level subtitle cue IDs and learning
   term-state actions are not described by the current playback extras V1 routes.
   Plain WebVTT is insufficient to safely reconstruct them. Do not generate fake IDs.
8. **Image subtitle formats (PGS etc.)**: existing server marks these unsupported and
   does not provide image overlays/burn-in through the V1 playback extras contract.
9. **Subtitles from third-party sites**: not in V1 extras. V1 supports real on-disk
   sidecars and textual embedded streams, not remote subtitle-provider searches.
10. **Tests**: Gradle/HTTP-contract/JSON tests and lint are not a real paired Android-TV
    E2E validation. Use actual clean-start server + real MediaAssets before release.

## Decisions requiring the owner

- Whether TV interactive learning cues, phone companion controls and storage wake are
  **must-have for release**; if yes, verify all existing V1 backend surfaces first,
  then discuss an explicit API contract before changing the backend.
- Whether TV multi-account selection per server is required at first release. The
  existing /api/v1 session and profile APIs are not a drop-in for the old cookie model.
- Whether unsupported image subtitles need client rendering, a server contract proposal,
  or an explicit unsupported message. Never silently treat unsupported streams as text.

## Quality gates before removing draft status

1. Latest CI green for #1468 (Android app-tv and app-mobile build/test/lint).
2. Genuine pairing → Home → Search → Work → Episode → HLS/DirectPlay → progress.
3. Device-token renewal and profile switching under concurrent HTTP/HLS traffic.
4. D-pad focus and Back behavior on original screens; no hidden/discarded feature.
5. Confirm user decisions on missing learning/companion/storage/image subtitles.
6. No unreviewed backend or Phone code changes, no invented Work/WorkUnit/Asset identity.
