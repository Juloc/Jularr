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


## PR #1493 integration (current TV branch)

All 10 self-contained files from PR #1493 are copied into the TV integration by
their original Git blob IDs. **No merge or rebase** of the parallel PR was done.

- `GET /api/v1/playback/assets/{mediaAssetId}`: the original Media3 player's
  previous/next controls now use the *actual server-supplied neighbouring asset
  IDs*. Each neighbour's metadata is separately verified; missing neighbours
  are hidden. The active session is ended before starting another. The original
  `workId` on navigation is updated from the server's asset record; no
  `WorkUnitId` is fabricated from a MediaAssetId or decimal episode number.
- Autoplay-next respects the profile's actual `autoplayNext` setting and only
  advances when a verified next asset exists. The server still decides the new
  stream format and resume position.
- `GET /api/v1/statistics?days=N`: an optional, explicitly labelled **aggregate
  statistics** panel is reachable from the original TV Settings when the
  `Statistics` module and `profile.media.read` permission are present.
  It shows actual watched time, sessions, completed counts, and top Works.
  It is **not** labelled or presented as the old chronological Activity timeline.
- Playback `storage_waking` and `storage_unavailable` failures are shown as
  actionable messages and permit explicit retry. The client does **not** call the
  admin-only NAS wake/check APIs. Transient progress-update failure does not
  forcibly replace the playing stream with an error screen; final saving remains
  attempted, with an explicit exit without a success claim if the connection
  cannot be restored.
- The source PR #1493 backend gap audit remains authoritative:
  `clients/android/core-api/docs/TV_LEGACY_FEATURES_V1_GAPS.md`.

**Still unimplemented:** old companion session/control/phone handoff,
word-level video learning cues, regular-profile manual NAS wake, offline video
manifest/sync, and chronological per-playback history. The APIs required for
these functions are not present in the reviewed V1 user services; no old
`/api/client/v1` fallback is permitted. User decisions are needed before
planning any new backend contracts.

The latest Android CI and live hardware tests remain separate quality gates.


## V1 controller and account sessions — latest integration

- `TvV1AppController` now owns the active TV entry's typed route transitions:
  actual Work IDs, WorkUnit IDs and verified MediaAsset IDs are checked before
  entering detail/episode/player screens; old WorkUnit identity cannot leak across
  verified previous/next playback assets.
- `TvV1CatalogHost` delegates relevant navigation to the controller while keeping
  the original Compose screen designs. Profile switches must drop the viewer's
  route history. **This does not mean the old cookie-dependent `TvAppController` /
  `TvClientFlow` have been fully migrated**; unsupported original functions
  must remain explicit until actual V1 contracts exist.
- Original settings now offer an **Active sessions** page when the backend reports
  `session.self.read`. These are real sessions for the *current account*, not
  a fabricated multi-account chooser. Users with `session.self.manage` may
  terminate a *different* session through the real
  `DELETE /api/v1/sessions/{sessionPublicId}` contract with confirmation.
  The current TV session is never offered there for remote revocation; it still
  signs out with `DELETE /api/v1/auth/session`.
- Contract tests cover session revocation of other sessions (current bearer
  preserved) and server-authoritative termination of the current session
  (local bearer cleared); typed-controller tests cover route identity and
  viewer-history isolation.
- Before release, verify that session permissions returned for a real paired
  Android TV are correct. **Multiple saved accounts on one server remain unported**:
  no credentials are reconstructed from other account sessions or the old
  cookie map.


## V1 client flow migration — newest increment

- `TvV1ClientFlow` is now the **active typed data controller** for Home,
  video-filtered library search, Watchlist and Work details. It reads
  clean-start `/api/v1` through the reviewed catalog adapter and hands
  presentation-only legacy models to the **original Compose TV screens**.
- `TvV1CatalogHost` delegates those reads to the new controller rather than
  duplicating HTTP orchestration inside the UI. The old `TvClientFlow` still
  exists for reference, but must **never** be called from the V1 entry.
- The controller validates WorkId response identity, tracks all real WorkUnitIds,
  keeps decimal-numbered specials, only exposes actual playable MediaAssetIds,
  and clears stale episode state on a different Work.
- Home and search filter movie/series on the server, not by downloading the
  first few pages of unrelated book/game media. Continue Watching only loads
  when the verified profile has Playback module and `profile.playback`.
- Unit tests exercise video filters, per-profile module permission checks,
  decimal episode mapping, and no invented MediaAsset IDs.
- Follow-up: cap of 20 × 100 works per screen is still a non-scalable
  transitional limitation. The eventual TV library should use server-side
  page loading/on-demand LazyRow pagination instead of claiming all Works were
  loaded. True multiple-saved-account switching also remains outstanding.
