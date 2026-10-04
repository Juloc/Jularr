# Cross-Spec Consistency Audit

Date: 2026-10-03  
Branch baseline: `dev`  
Status: **core + Admin consistency pass complete; no unresolved direct cross-spec contradiction remains after the fixes recorded below**.

This audit compares the current canonical domain/architecture/global UX contracts with the binding screen specs. It is a documentation consistency audit, not an implementation audit.

## 1. Authority used

1. `DOMAIN.md`
2. `DOMAIN-AUDIT.md`
3. `ARCHITECTURE.md`
4. `UX.md` + `INFORMATION_ARCHITECTURE.md`
5. binding screen `SPEC.md`
6. approved mockups, with text authoritative on conflict

## 2. Request / acquisition — aligned

Canonical consumer rule:

- exactly one acquisition action: `Request`;
- no consumer Add / Add & Monitor / Instant button;
- approval/auto-approval is backend policy;
- Request begins from an already resolved canonical target;
- personal Watchlist/Reading List/Favorite/Collection state stays separate;
- Manual Search/import/release selection stays Admin.

The global UX had an old multi-step Add flow and exposed Searching/Importing as normal user states. That has been removed.

Consumer state vocabulary is now normalized to:

- Waiting for approval;
- Requested / Approved where useful;
- Looking for media;
- Downloading;
- Preparing;
- Available / Partially available;
- Monitoring future releases;
- Needs attention / Failed;
- Cancelled where relevant.

`Wanted`, search jobs, ImportJob/importer phases, release candidates and downloader internals remain Admin/architecture terminology.

## 3. Request capability / Instant — aligned

The existing authorization capability name `Instant` may remain internally for compatibility.

Binding meaning:

- `Request` capability -> consumer presses Request; approval policy applies;
- `Instant` capability -> consumer still presses Request; the request may proceed immediately/auto-approve;
- Browse/Hidden cannot create acquisition requests.

No UI exposes a second Instant/Add action.

## 4. Canonical media identity — aligned

Watch/read/listen media remain:

`Work -> Structure -> Edition -> Version -> Asset/File -> Track`

Provider-native records never become canonical media identity.

Anime/Series provider season/part splits remain mappings/presentation targets over canonical Work/Season/Episode identity.

## 5. Games identity boundary — aligned

Games is deliberately outside MediaCore.

Shared Search/Discover/Home/Request uses a typed canonical target:

- normal media -> `Work`;
- Games -> Games-owned `Game`.

The shared UI/pipeline does not force Game/GameRelease into Work/Edition merely to reuse Search or Request.

Collections remain Work-based in V1. Games is therefore not silently inserted into `CollectionEntry(WorkId)`; adding cross-domain Collections later requires an explicit typed target contract.

## 6. Discovery / provider persistence — aligned

Discovery/provider responses are evidence, not canonical identity.

Durable provider entity/list data is persisted locally as provider evidence/snapshots where defined by the domain.

Linked Collections follow:

`provider list -> local snapshots -> canonical Work resolution -> local Collection membership -> local rendering`

Normal Linked Collection rendering and Smart Collection evaluation do not require live provider calls.

## 7. Library / Discover / Home boundaries — aligned

### Discover
Find new titles, including non-local titles.

### Library
Browse durable Library/monitoring context. No generic consumer Add/import menu and no Admin dashboard clutter.

### Home
Personal continuation/recommendation surface. It is not another Library index.

### Watchlist / Reading List
No extra top-level app is required. Personal list state is projected through Library filtering/deep links and may appear as a Home shelf.

This avoids a missing Watchlist screen while preserving canonical `WatchlistEntry` state.

## 8. Collections — aligned

Persisted modes:

- Manual;
- Smart;
- Linked.

Cross-media is a capability, not a Collection type.

Franchise/adaptation is not a parallel Collection persistence model.

Manual membership removal and Collection deletion never delete canonical media, progress, ratings or Requests.

## 9. Edition / Track semantics — aligned

Reading and Audiobooks can expose meaningful Editions.

For normal video:

- audio dub language -> audio Track;
- subtitle language -> subtitle Track;
- technical file/quality alternatives do not automatically become Editions;
- a video Edition is only for a genuinely distinct presentation/cut/publication.

Movie Detail and the shared Language/Edition selector now state the same rule.

## 10. Progress / sessions / continuation — aligned

One canonical `MediaProgress` owns consumption progress.

Exact resume and completed-through semantics remain distinct.

Playback uses `PlaybackPlan` + `ActiveSession`.

Compact continuation surfaces project existing state:

- Now Playing -> active playback/TTS;
- Continue Reading -> Reader progress.

They do not create new progress/session stores.

## 11. Profile / Settings ownership — aligned

Profile Activity contains personal media activity only.

Devices & Sessions is owned by:

`Profile -> Settings -> Devices & Sessions`

The Profile spec no longer defines a second embedded device/session manager.

Deleting Activity does not reset Progress.

Ratings use the universal normalized `UserRating` model.

Friends remains hidden until a real Friends/social capability/domain contract exists; no dead tab or UI-only social state is allowed.

## 12. Account / Profile / Login / Connections — aligned

- Account = authentication/security/roles/account sessions;
- Profile = personal media state/preferences;
- Login identity = authentication adapter identity;
- Connection = Profile-scoped sync/import/write-back link.

Signing in through Plex/Jellyfin/Trakt/AniList/MAL/etc. never silently enables sync unless the provider actually supports/configures that separate Connection capability.

## 13. Learning gating — aligned

Resolution remains:

`instance availability -> authorization/capability -> profile module preference -> detailed Learning settings`

Player/Reader Learning modes are optional presentation layers and do not create alternate playback/reading identities.

Turning Learning off preserves stored Learning state.

## 14. Instance modules / Setup Wizard — aligned

Admin -> Instance exposes only canonical modules whose complete runtime gate exists.

Setup Wizard no longer implies that conceptual future items such as Games, AI, Requests, Native Downloader or Generic Downloads automatically have valid independent module switches.

Games setup sections can exist only when Games is actually available in the setup context; the wizard must not fabricate an unenforceable Instance switch.

## 15. Visual skins — aligned

Clean and Original Jularr share layout, behavior and information architecture.

- Clean: neutral/minimal, purple default accent, no anime/sakura/ink decoration.
- Original Jularr: Japanese ink/watercolor/sakura treatment, red/pink default accent.
- Hue shifting uses semantic tokens.
- legacy headings containing `Clean Design` mean visual baseline only, not Clean-only product behavior.

Error/permission states now mark both Original J and Clean directions as approved.

## 16. Consumer/Admin boundary — aligned

Consumer pages never expose:
- provider IDs;
- release scoring;
- indexer results;
- downloader internals;
- raw import paths;
- job/log internals.

Admin Manual Search/Imports/Wanted/Activity/Media Detail own those concerns.

## 17. Calendar gate — intentionally open, not inconsistent

Calendar retains its explicit final owner-review gate for:
- final filter/status semantics;
- in-grid status treatment;
- event interaction;
- responsive behavior;
- Light/Dark visual QA.

Candidate consumer acquisition labels have been normalized, but that gate remains mandatory.

## 18. Mobile primary navigation — resolved

Locked Mobile bottom navigation:

`Home · Library · Calendar · Learning · Profile`

Games does not replace Learning in the Mobile bottom bar.

Games remains a dedicated destination, reached contextually on Mobile through Home Games/Continue Playing surfaces, global Search/Discover Games context, Game Detail/back-navigation and deep links.

When Games is available and no recent Game activity exists, Home must still expose a lightweight Games entry so the Games library remains discoverable.

Desktop and TV may keep Games as a primary destination where appropriate.

## 19. Non-blocking planning notes

- `games/SPEC.md` remains a shared planning scaffold while child Games pages are approved individually; this is intentional.
- several specs still have visual-review/status metadata that may be tightened later, but this does not create domain/behavior conflicts.
- historical/current-implementation tables in `INFORMATION_ARCHITECTURE.md` may describe legacy entities/routes. They are implementation inventory only and do not override target contracts.

## 20. Result

Core Media, Request, Library, Collections, Discover, Detail, Player, Reader, Profile, Settings, Learning, provider persistence and Admin/consumer ownership are now mutually consistent at planning-contract level.

No new generic core consumer screen is required by this audit.

The previous Mobile Games-vs-Learning contradiction is resolved.


## 21. Admin navigation / route ownership — aligned

The global Admin navigation is normalized to one permanent destination per owning area.

Key results:
- History is the third tab of Activity / To-Do, not a second permanent sidebar destination;
- Manual Search is contextual inside the Acquisition dialog;
- Downloader is one destination with internal secondary navigation;
- Indexer/Search and other provider configuration belongs to Providers;
- external download-client configuration belongs to Downloader -> Externe Clients;
- AI provider/model configuration belongs to Admin AI;
- Resources/Health/Logs consolidate into System & Diagnostics;
- Sessions/Devices consolidate into Devices & Sessions;
- Scan/reconciliation starts from Storage/LibraryRoot and runs through Activity;
- Setup and repair/editor flows remain contextual.

Legacy routes may remain only as compatibility redirects/deep links while implementation catches up.

## 22. Storage / Downloader / Acquisition / Migration ownership — aligned

The owning split is consistent:

- **Storage** -> Mounts, LibraryRoots, managed storage roles, safe local paths, final destination defaults and import placement strategy;
- **Downloader** -> native Usenet transport, queue, verify/repair/extract, bandwidth/schedule and external download-client adapters;
- **Providers** -> Indexer/Search, Metadata, Subtitles, Translation, Reading Sources and other provider adapter configuration;
- **Acquisition Profiles** -> quality, upgrade, language, Release Rules, scoring, wait/delay and source/provider preference;
- **Migration adapter** -> source-manager path translation/coexistence state;
- **content importer/library layer** -> content-specific naming/organization inside the Storage-selected target;
- **Games Admin** -> BIOS/Firmware requirement/validation/binding, while Storage owns the restricted BIOS/Firmware path.

There is no permanent global Import & Routing page and Acquisition does not own filesystem naming/path policy. BIOS/Firmware is neither a Games LibraryRoot nor Generic Downloads.

## 23. Activity / History / domain diagnostics — aligned

One canonical operational model remains authoritative.

- Dashboard is a live projection, not a job store;
- Activity / To-Do owns live/actionable cross-system operations;
- History is the completed/resolved tab of that same operational area;
- Downloader Completed/Failed is downloader-specific technical detail over shared operational data;
- AI Jobs & Usage is an AI-focused projection plus AI usage telemetry, not another global job store;
- Notification delivery history is sink-attempt diagnostics and is explicitly distinct from Activity/History;
- System Runtime is worker/runtime health rather than a competing business-operation queue.

## 24. Setup / Instance module gating — aligned

Setup Wizard uses the same canonical stores as normal Admin pages.

It may not invent independent Instance switches for AI, Native Downloader, Games, Requests or Generic Downloads before complete runtime gates exist.

The Setup module list now mirrors the canonical `InstanceModule` contract instead of maintaining an incomplete Setup-only list. The Downloader and AI setup steps are conditioned on real capability/configuration availability rather than fake module toggles. The native-downloader on/off setting is explicitly a Downloader service setting, not an InstanceModule. Final setup health tests run only for configured owners.

## 25. Sonarr migration / coexistence — aligned

The old Anime-specific Sonarr document is explicitly an implementation bridge to the generalized Migration Center contract.

Binding target:
- ownership at Work level only;
- modes **Extern verwaltet**, **Gemeinsam**, **Jularr verwaltet**;
- default per integration instance × media/content type;
- optional per-Work override;
- at most one external manager + Jularr per Work;
- **Gemeinsam** is permanent/advanced and fails closed without reliable observation;
- **Jularr verwaltet** may keep Sonarr connected for read-only conflict observation;
- monitoring handover is explicit/previewed;
- source path translation belongs to the Sonarr migration/coexistence adapter.

Legacy `/Settings/SonarrMigration`, `/Admin/Sonarr` and Anime remote-path mappings are migration inputs, not target owners.

## 26. Backup / Migration boundary — aligned

Artifact routing is explicit:
- recognized current/versioned Jularr backup -> Backup & Restore;
- legacy Acquisition backup bundle -> Backup & Restore legacy Acquisition-only flow;
- old Jularr database/layout that is not a supported backup -> Migration Center;
- external manager/media-server/folder/JSON/CSV migration -> Migration Center.

The same artifact must not appear as two competing restore/import workflows.

## 27. Admin density / permissions — aligned

`Detailliert | Kompakt` remains one profile-scoped Admin-shell preference. Appearance may expose it only as a shortcut to the same state; no page owns a second density setting and no business logic changes with density.

Authorization remains server-side/capability-based. Hidden navigation is not authorization, and no Admin screen may create its own disconnected permission model.

For AI specifically, Users & Permissions owns base Account/Profile/group/role eligibility for shared instance AI. Admin AI owns only AI-service-specific narrowing and limits; effective access is the intersection of the two contracts.

## 28. Remaining Admin implementation gaps — not spec contradictions

Current `dev` still contains legacy implementation routes/navigation such as standalone Resources, History, Sessions, Scans, Logs, Usenet and System/Sonarr settings.

These are implementation migration tasks. They no longer represent competing target contracts.

Provider-specific operational pages such as subtitle completeness/manual subtitle work may remain feature surfaces, but provider **configuration** must converge on Admin Providers rather than being duplicated.

## 29. Updated result

The Admin planning contracts are consistent enough to stop inventing new generic Admin pages.

Next work should be:
1. implement/consolidate the target Admin shell and redirects without losing current functionality;
2. migrate legacy settings/state to their canonical owners;
3. execute dependency-ordered implementation, with Anime/Movie/TV request -> acquisition -> import -> playback as the primary product path.

## 27. Notifications — aligned

Canonical notification architecture is now centralized in `docs/NOTIFICATIONS.md`.

The Notification Settings, Topic Editor, Quiet Hours, Digest, Center, Bell/Quick View, Toast/Popup, Persistent Banner and Admin Notifications specs now use the same ownership model:

- one `JularrEvent` / EventLog boundary;
- one event-definition catalog;
- one profile notification preference owner;
- profile channels are In-App / Push / E-Mail;
- Digest is timing, not a transport;
- Quiet Hours delays eligible attention/external delivery but does not hide the durable In-App inbox;
- Notification Center/Bell/Quick View share one inbox state;
- Toast is transient attention;
- Banner is current-state driven and never owned by inbox read state;
- external/integration channels such as Webhook/Home Assistant are not normal personal profile channels by default;
- delivery failures never replay the originating business operation.

Resolved cross-spec contradictions:

- Request status belongs to the Requests topic, not Media. The approved Topic Editor image remains a layout reference where it shows an illustrative Request row under Media; text/architecture is authoritative.
- The current single `NotificationMode` model is explicitly V1-only; the target is Enabled + selected channels + Immediate/Digest timing.
- In-App remains immediate durable inbox state even when external delivery timing is Digest; Digest does not become an In-App transport.
- Quiet Hours and Digest share one restart-safe delivery scheduler rather than separate page-owned queues.
- persistent Banner state is separated from event/Toast delivery.
- inbox recurrence time, read time and dismissal state are distinct; read/unread changes do not reorder the feed.
- Profile audience no longer has a target semantic fallback to Admin when ProfileId is missing; target routing is explicit/fail-closed.

The remaining work is implementation parity against this contract, not another UX ownership decision.
