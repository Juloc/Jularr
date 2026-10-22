# Android TV original → clean-start /api/v1: parity and E2E acceptance

**Audit snapshot:** 2026-10-22, original `feat/android-tv-integrated-906-20261009`
vs. PR #1468 `feat/android-tv-v1-ui-integration` at
`075ab8f5876ddb769448d6177558a221889fa11a`.
**Test-only PR:** `test/android-tv-v1-parity-e2e`.
**Readiness status: NOT release-verified**. JVM tests with a local synthetic HTTP
server exercise the published V1 protocol but **do not run Compose, Android
Media3, a backend, NAS, actual DRM/codecs, a phone or D-pad hardware**.

Original `TvAppController`, `TvClientFlow`, `TvAppHost` and
`TvCompanionRuntime` still implement old `/api/client/v1` semantics; the
launched V1 entry deliberately uses `TvV1EntryHost`, `TvV1CatalogHost`,
`TvV1AppController`, and `TvV1PlaybackHost`, preserving much of the
original visual screen code. Do not mistake *source files present* for
*routes accessible from the V1 application*.

## A. Screen-by-screen parity inventory

| Original screen/feature | V1 entry / reuse | Category | Evidence / remaining work |
| --- | --- | --- | --- |
| `TvScreens` setup, login/account gate | `TvV1EntryHost` + original `TvSetupScreen`, device pairing | **Implemented and tested** (contract only) | V1 six-digit pairing, 5-second polling, encrypted device bearer; no cookie fallback |
| `TvAccountSelectScreen` multiaccount picker | Not connected to the new origin-scoped device session | **Missing client integration** | Current server-origin vault has one token per origin, not old saved multiaccount choice |
| `TvProfileScreen` / PIN profile selection | `TvV1ProfilesScreen`, `HttpTvV1ProfileClient` | **Implemented and tested** (contract); D-pad unverified | PIN `POST /api/v1/auth/profile/{id}/pin`, token rotation, capabilities reload and profile cache disposal |
| `TvHomeScreen` cover grid + continue | Original component wired from `TvV1CatalogHost` | **Implemented and tested** (data); live unverified | Source-filter video Library; true `/api/v1/continue` MediaAssetIds. Complete personalized Home shelves missing: HomeLayout supplies order/visibility only |
| `TvSearchScreen` with type filters | Original component with V1 server library search | **Implemented and tested** (API/navigation); D-pad unverified | Video movie/series scope only; no invented search results |
| Discover/catalog cards | `TvV1DiscoverScreen` on existing scopes/search | **Implemented and tested** (contracts); live unverified | Local movie/series + provider details; non-video provider screen parity missing |
| `TvAnimeDetailScreen` work/seasons/episodes | `TvAnimeScreen` + `TvV1EpisodeDetailScreen` | **Implemented and tested** (identity/mapping); live unverified | Real WorkId / WorkUnitId / nullable MediaAssetId; fractional episodes separate rather than truncated |
| Credits, people/filmography | `TvV1CreditsScreen`, `TvV1PersonScreen` | **Implemented and tested** (navigation/API); live unverified | Exact personId/WorkId preserved on Back |
| Movie versions / quality choice | `TvV1CatalogHost` `MovieEditions` panel | **Implemented and tested** (API/identity); live unverified | Only truly available playable MediaAsset IDs |
| `TvWatchlistScreen` | Original component with V1 watchlist read/write | **Implemented and tested** (contract); live unverified | WorkId-based toggles; exercise D-pad and permissions on device |
| `TvPlayerScreen` Direct/HLS, progress | Original Media3 screen in `TvV1PlaybackHost` | **Implemented and tested** (HTTP state); **not live** | Real stream, 10-second progress heartbeat, end/final update; live decoder/transport test required |
| Player audio/sidecar subtitles | Original audio/subtitle panels + V1 track and WebVTT APIs | **Implemented, not live tested** | Alternate audio starts a new session; Media3 text rendering and cleanup need physical verification |
| Player chapters, markers, preview | Original panels with real V1 markers/chapters/JPEG trickplay | **Implemented and tested** (geometry/mapping); live unverified | 1-based sprite sheets; autoplay/seek/focus must be checked with D-pad |
| Player previous/next/autoplay | V1 asset lookup and `TvV1AppController.verifiedNeighbour` | **Implemented and tested** (data/identity); live unverified | Server-supplied MediaAssetIds, stop old session before switch; no fabricated episode indexes |
| `TvStorageRecoveryScreen` / wake NAS | Server `storage_waking`/`storage_unavailable` errors + explicit retry | **Implemented, not live tested** | No TV-profile manual wake endpoint; Admin wake is prohibited |
| Offline download/player | Old offline components not connected | **Actual backend gap** | No matching profile-scoped V1 video manifest/download/sync API |
| `TvActivityScreen` historical list | `TvV1StatisticsScreen` shows clearly named aggregates | **Actual backend gap** for chronology | `/api/v1/statistics` cannot substitute timestamped playback rows |
| `TvCompanionPairingOverlay` remote/phone handoff | Old component present but **not active** in V1 player | **Actual backend gap** | Device-login pairing is not remote pairing; V1 has no command/revisioned hub |
| Learning overlay/word states from `TvPlayerScreen` | Current V1 player supplies `currentCue=null`; original callbacks intentionally disabled | **Actual backend gap** | WebVTT has no cueId, tokenized words, term state or mutation V1 API |
| `TvProfileScreen` playback preferences | Original settings UI via `TvV1SettingsHost` bridge | **Implemented and tested** (wire/mapping); live unverified | Advanced server fields preserved; speed-only save |
| `TvUpdateScreen` APK release checks | Original update UI present in `TvV1SettingsHost` | **Implemented, not live tested** | Test actual valid install/permission path; never overwrite production release |
| `TvSidebar`, `TvFocusMemory` | Reused in V1 Home/Search/Watchlist/Settings/Details | **Implemented, not live tested** for physical focus | Pure key memory test does not establish correct Compose focus paths |
| Back/Home/Settings/Profile/session routes | `TvV1Navigation`, `TvV1AppController`, native Android Back | **Implemented and tested** (pure navigation); live unverified | Root resets stale work; Player delegates Back to session cleanup |

**Strict categories:** All entries labelled “tested” above mean **JVM contract/logic
tests**, not a demonstrated emulator or physical Android-TV run. New E2E-shaped
HTTP tests deliberately use synthetic but **server-schema-correct test IDs**
and never invent unavailable route/identifier relationships.

## B. Focus & navigation expectations from original UI

Original focused surfaces: `TvSidebar` and `TvFocusMemory` (per-route
keys), `TvHomeScreen`, `TvSearchScreen`, `TvWatchlistScreen`, episodes,
the original `TvPlayerScreen` remote input and learning/player overlay.
Active V1 navigation: `TvV1Navigation` + `TvV1AppController`.

1. On entering a screen, a visible actionable item receives focus; no hidden
   carousel item, artwork or inert text captures D-pad focus. On Home, Search,
   Details, Watchlist, Player, Settings, Profiles, Episodes, Credits, Person,
   Discover, Sessions and Statistics, press UP/DOWN/LEFT/RIGHT through all
   visible actions. Record a screenshot and the element highlighted at each
   transition, including empty/loading/error states.
2. On Home → Search → Work → Episode → Player, **Back** closes/stops playback
   and reports final progress before returning to Episode/Details/Search/Home
   in sequence. The physical remote Back action must not escape to the Android
   launcher while a player session remains active.
3. D-pad focus after Back should return to the originating card/control,
   including after MovieEditions/Person/Stats. `TvFocusMemory` persists per
   *legacy route* only; it is not evidence that every new V1 surface invokes
   the memory hooks. **Verify actual focus restoration on device**, especially
   the V1-only Discover/Person/Episode/Sessions routes.
4. Sidebar Home/Watchlist/Settings reset previous Work/Person navigation state.
   Profile switching must also discard cached library/watchlist and keyboard
   search state belonging to the previous profile. A new account/device-server
   must never inherit the old account's highlight + media identity.
5. Player DPAD_CENTER toggles/activates focused buttons only; LEFT/RIGHT seek,
   skip panels, chapters, subtitle and audio lists, Back/dismiss and next-episode
   buttons follow the original player's focus transitions. Every user action
   must work with remote alone; no touch pointer or keyboard assumed.
6. Test repeat navigation to the same selected item and 20+ nested routes.
   Stack caps at 16; Back never displays an unrelated WorkId/PersonId.
   A missing/null MediaAssetId never yields a play action.

## C. JVM automated test inventory

| Test class | Covers | Boundary |
| --- | --- | --- |
| Existing `TvV1ConnectedFlowContractTest` | Pair → Home/Search → Work/Episode → start/update session | Local HTTP only |
| **New `TvV1ParityConnectedJourneyTest`** | Pair → PIN profile → rotating credentials → **video-filtered** Home/Search + Continue → fractional WorkUnit → real MediaAsset + neighbours → markers/chapters/audio/WebVTT/trickplay → end/update → next session with audio index | Does not compose screens or play media |
| **New `TvV1ParityNavigationMatrixTest`** | Back stack, original Work/Person/WorkUnit isolation, sidebar reset, nested route cap, focus-memory keys | Pure Kotlin; no physical D-pad |
| **New `TvV1ParityFailureContractTest`** | `storage_waking`, `storage_unavailable`, explicit retry, missing metadata, network refused, user skip modes | Does not wake real NAS or interrupt a Media3 stream |
| Existing `TvV1DeviceSessionTest`, `TvV1ProfileClientTest`, `TvV1PlaybackAssetNavigationTest`, `TvV1PlayerUiMappingTest`, `TvV1PlaybackExtrasTokenRaceTest` | Token/401, profile and session identity, audio/skip/trickplay mapping, server concurrency | API contract / pure state |

All HTTP test fixtures use documented `/api/v1` methods and JSON
shapes. They simulate response payloads, not real media playback. CI may
run all `:app-tv:testDebugUnitTest` and `:core-api:testDebugUnitTest`.
Expected focused invocation, from `clients/android`:

```bash
gradle :app-tv:testDebugUnitTest :core-api:testDebugUnitTest :app-tv:lintDebug
```

The repository's Android workflow also runs other module tests and lint.
CI green is a *necessary but insufficient* release gate.

## D. Manual Android TV / emulator test procedure

**Environment:** Android Studio/Android SDK + Google TV Android API 35/36
emulator or **real Android TV / Google TV** (prefer real device for codecs,
DPAD, HLS and wake). This execution environment has neither Android SDK,
`adb`, `emulator`, nor a connected test backend/NAS, so no emulator/device
or true E2E run was possible here.

**Prepare:** isolated `clean-start` test instance and at least two
authorized profiles (one PIN-protected); use a device pairing account that
can approve a TV; configure one playable movie and a series with two
consecutive *real stored* video MediaAssetIds, one fractional episode
if available, and ideally a file with multiple audio tracks, textual subtitle
sidecar, chapter metadata, real skip markers, extracted Trickplay sheets.
For NAS tests, use a disposable/test NAS and the Owner-configured
Wake-on-Demand policy; never interrupt real household storage.
Record the **actual** WorkId, WorkUnitId and MediaAssetIds discovered
through `/api/v1`. Do not substitute work IDs, TMDB IDs or ordinal episode
numbers for assets.

```bash
cd clients/android
gradle :app-tv:assembleDebug
adb devices
adb install -r app-tv/build/outputs/apk/debug/app-tv-debug.apk
adb shell am start -n de.juloc.jularr.tv/.TvMainActivity
# Use remote keys, not touch:
adb shell input keyevent KEYCODE_DPAD_UP
adb shell input keyevent KEYCODE_DPAD_DOWN
adb shell input keyevent KEYCODE_DPAD_LEFT
adb shell input keyevent KEYCODE_DPAD_RIGHT
adb shell input keyevent KEYCODE_DPAD_CENTER
adb shell input keyevent KEYCODE_BACK
# Capture visual and diagnostic evidence:
adb shell screencap -p /sdcard/tv-parity.png
adb pull /sdcard/tv-parity.png
adb logcat -d > tv-parity-logcat.txt
```

Use server access logs/trace IDs to compare actual `/api/v1` calls and
HTTP status. Redact `Authorization` and profile PIN before sharing logs.

| Case | Remote action / deliberate server condition | Expected observable result |
| --- | --- | --- |
| TV-01 Pair | Fresh APK, enter test instance, approve displayed six-digit code via web UI | Pairing and profile step appear; no `/api/client/v1` calls or cookie substitution; expired code renews |
| TV-02 Profile | Choose unlocked, then locked with valid/invalid PIN, switch back | Wrong PIN never changes data; valid PIN rotates bearer and refreshes permissions/library; old profile titles do not remain |
| TV-03 Home | Open Home and Continue | Only video Works; Continue uses real playback asset/time. D-pad traverses cards and Sidebar |
| TV-04 Search/Discover | Type term, filter category, open provider-only hit, Back | Matching server hits; external ID not treated as local WorkId; Back restores search term/card and focus |
| TV-05 Details | Open Series → season → episode, credits → person → different Work → Back | Titles/cards/images and real Work/WorkUnit identities retained; no phantom playable media; correct focus |
| TV-06 Fractional/editions | Open special episode 1.5 and film with two actual editions | No truncation to episode 1; choose a **real** MediaAssetId; unavailable asset not started |
| TV-07 Play/Resume | Start Direct, record 40s, Back, reopen; repeat with HLS/FFmpeg | Playback resume near last saved position; no double-counted completion; stream remains authenticated |
| TV-08 Next/Autoplay | Reach end of first series asset with autoplay on, then off; press Prev/Next | Only server-reported neighbour playable; old session ended first; autoplay off remains, no unknown target |
| TV-09 Audio | Select a supported different embedded stream mid playback | Old session closed, new session with actual `audioStreamIndex`; sound/language changes, position remains close |
| TV-10 Subtitles | Select embedded WebVTT then external sidecar, then Off | Correct language and visible text; no PGS rendered as text, no stale captions after Off; temp files cleaned up |
| TV-11 Chapters/Skip | Seek via chapter, skip Intro/Outro in Button/Automatic/Off modes | Actual cue times only; skip honors preferences, ends do not loop |
| TV-12 Trickplay | Scrub across the 1st to 2nd and final sprite sheet | Correct cropped images, 1-based sheet URLs, bearer included in requests, no image when extraction missing |
| TV-13 NAS | Stop disposable NAS, attempt session, allow Wake-on-Demand and retry | Explicit storage_waking/unavailable without fake availability; server controls wake, TV makes no admin wake calls |
| TV-14 Network loss | Disconnect TV Wi-Fi during stream, heartbeats and before exit; reconnect | Visible recovery; playback not incorrectly completed; subsequent retry and final progress only on server confirmation |
| TV-15 Revocation | Revoke device session while playback/metadata is in flight | 401 → paired-device gate; no previous profile content/bearer leaking after profile switch |
| TV-16 App lifecycle | Background with Home; kill/relaunch mid-video | No stale Media3 session or wrong account; resume from server's last confirmed position; record missed final update |
| TV-17 D-pad/Back | Run focus checks in section B on **every** screen, empty/error/loading cases included | Visible focus and deterministic Back, no focus trap or accidental navigation |
| TV-18 Updates/settings | Change playback prefs, inspect versions and self-update with test APK | Server profile settings persist; update action uses expected Android permissions, no unrelated account reset |

For each case record: device model/OS/API, server SHA, app SHA,
real WorkId / WorkUnitId / MediaAssetId (IDs only, never tokens),
permissions/modules, test steps, expected/actual outcome, screenshot/log
and Pass/Fail/Blocked. Do **not** mark any TV-07..TV-18 as passed based
solely on simulated HTTP tests.

## E. Defects and release blockers (code-referenced; no product code touched)

| Priority | Location (new V1 branch) | Evidence / cause | Suggested owner fix / verification |
| --- | --- | --- | --- |
| P0 release gate | `TvV1PlaybackHost.kt:106–159`, `TvPlayerScreen.kt` | No live proof of HLS/Direct codec start, first segment auth or retry after bearer rotates | Live two-device/server test with HLS + 401 rotation, network/cancel/seek; capture real HTTP status and Media3 errors |
| P1 (coverage) | `TvV1CatalogHost.kt:68–78`, `TvV1Navigation.kt`; `TvSidebar.kt` | Nav unit tests prove stack identity, not actual Compose focus restoration on new pages | Add emulator Compose focus instrumentation, or manually execute TV-17. Fix only failures with exact focus target / origin |
| P1 (possible behavior risk) | `TvV1PlaybackHost.kt:103–135,249–280,342–379` | Interruption/process death before final update may leave last 10-second heartbeat as only persisted position | Verify TV-14/16. If confirmed, use lifecycle-aware stop/close and bounded final progress retry after owner approval; never claim a successful save on network loss |
| P1 (coverage) | `TvV1CatalogHost.kt:179–280` | Server errors, permissions and pagination are handled, but current JVM suite does not render their loading/error/focus states | Test 403/404/500 and empty list in Compose instrumented UI; do not show fabricated cards |
| P2 parity | `TvV1EntryHost.kt:20–28`, `TvV1CatalogHost.kt:46–70` | V1 uses its own entry/controller; original `TvAppController`, `TvClientFlow` and cookie-based account picker remain dormant | Main agent to retain intended original screen functions on V1; decide account picker per server |
| Backend decisions | `TvV1PlaybackHost.kt:479–480` (inactive term/phone callbacks), `TvV1CatalogHost.kt` | Original Companion and word-level subtitle actions not connected and no matching V1 service | Owner must decide new backend contracts for companion, cue/term state, timeline history and TV-profile wake |

**Do not file a confirmed production bug from an unexecuted device
scenario.** The P1 rows describe reproducible risks/coverage blockers,
not a proven crash. If TV-17 or an emulator test reproduces one, record
**actual** file/line, recorded screen, focused action and HTTP trace,
then hand the specific fix to the main agent.

## F. Non-negotiable backend gaps vs. unimplemented UI

- **Backend missing:** Companion pairing/control/phone handoff as a shared
  playback room; timed term/cue IDs + vocabulary state; chronological per-
  playback-history records; user-authorized manual mount wake; offline
  video/download manifest/sync; image/PGS subtitle rendering by the V1
  playback API; remote internet subtitle-provider acquisition.
- **Client incomplete:** original per-server multiaccount selection,
  personalized Home shelf parity, some nonvideo Discover screens, portions of
  original `TvAppController`/old sidebar entry flow.
- **Implemented but not live proved:** Direct/HLS Media3 playback, selected
  FFmpeg track, subtitle rendering, chapter/skip/sprite panels, NAS wake/error,
  Back/focus at real-TV scale, APK update and app resume.

The present tests never call the admin NAS wake API, never fall back
to `/api/client/v1`, and never manufacture media/cue/account IDs in app code.
