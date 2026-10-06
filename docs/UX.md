# Jularr UX specification

Status: planning baseline. This document defines information architecture, navigation, platform behavior and required screens before major feature implementation resumes. Visual details are finalized through approved mockups.

## 1. UX principles

- One shared Jularr UX/layout system with two visual skins: **Clean** and **Original Jularr**.
- Clean is the compact Fluent 2 / Windows 11-inspired neutral baseline and never uses decorative Japanese/anime background artwork.
- Original Jularr applies the established Japanese ink/watercolor/cherry-blossom visual treatment over the same layout/components.
- When an Original Jularr screen intentionally uses the Jularr mascot/anime character, it uses the canonical reference at `docs/assets/original-j/jularr-mascot-reference.png`. The mascot is optional; screens do not invent replacement characters. Pose/expression/props may vary while identity stays canonical.
- Clean does not use the Original J mascot by default. Mascot presence never changes information architecture, feature availability or behavior.
- No unnecessary explanatory text, duplicated headings or nested pages when a direct interaction works.
- Media is the visual focus; administration is information-dense but structured.
- User UI and Admin UI are distinct modes.
- Responsive behavior is intentional for Desktop, Tablet, Mobile and TV.
- Light, Dark and System brightness modes are first-class for both visual skins.
- User-selectable accent/theme colors use shared semantic design tokens; derived colors must remain accessible.
- Clean defaults to the purple Jularr accent/mark; Original Jularr defaults to the established red/pink accent/mark.
- Accent changes may hue-shift permitted branded/decorative tokens consistently, but must not recolor third-party provider logos or semantic error/success meaning.
- Clean and Original are skins, not separate page implementations or information architectures.
- Legacy screen titles that say `Clean Design` describe their visual baseline only; unless a spec explicitly says otherwise, the same behavior/information architecture applies to Original Jularr.
- Every screen defines loading, empty, partial, error and ready states.
- Permission-restricted actions disappear from UI but are also enforced server-side.
- Never expose half-implemented controls.

## 2. Global user navigation

### Desktop/tablet wide
A top header spans the page over a persistent left navigation:
- header left: Jularr brand and, inside Admin or Settings, the mode name; centre: the **global Search**; right: theme, notifications and the account menu (Profile, Settings, sign out);
- Global Search lives only in the header, never in the sidebar. Discover owns a prominent search of its own, so the header field is omitted there and Ctrl/Cmd+K focuses Discover's field;
- the sidebar is navigation only: no account, theme or offline utilities.

Persistent left navigation:
- Home
- Library
- Games
- Calendar
- Learning, while it is finished; today it sits in the Unfinished section (below)
- Search/Discover is globally available in the header rather than a redundant permanent destination.

Bottom/profile area:
- Profile
- Settings
- Admin, only when authorized
- **Unfinished**: one subordinate, collapsible section that closes the sidebar (#870)

### Unfinished destinations (prerelease)
Destinations whose core feature is still incomplete or deferred are grouped in one `Unfinished` section instead of looking finished beside the core pages. It is the last, visually subordinate section of the sidebar and of the Admin and Settings lists, without warning colours. The set is one flag in `UiNavigationCatalog`: the Learning destination and Learning settings (#441), Offline settings (#839, #861), personal AI settings and Admin AI (#638), and Admin Subtitles (external provider search is partial, sync is missing; see INFORMATION_ARCHITECTURE.md Bazarr table). Finished destinations stay in normal navigation. Permission and instance-module gating still apply; routes never change for regrouping. On Mobile the section is reached through Profile -> Unfinished, never from a bottom-navigation slot.

Library contains normal watch/read/listen media rather than giving every media type a permanent top-level sidebar entry. Games is the deliberate exception because its platform browsing, saves, runtimes and controller/play model require a distinct consumer surface while still reusing shared Jularr infrastructure.

### Mobile
Bottom navigation optimized for frequent use:
- Home
- Library
- Calendar
- Learning, once it is no longer Unfinished (it is reached through Profile -> Unfinished until then)
- Profile

Search is globally accessible from the top/app bar; it never takes a bottom-navigation slot, and neither does Unfinished. Activity belongs under Profile rather than occupying bottom navigation.

Games does not consume a Mobile bottom-navigation slot. When Games is available, its dedicated route is reached contextually from Home Games/Continue Playing content, global Search/Discover Games context, Game Detail/back-navigation or deep links. Home must keep Games discoverable even when the profile has no recent Game activity.

### TV
Remote-first primary destinations:
- Home
- Library
- Games when the Games destination is available to the active profile/installation
- Calendar where useful
- Search
- Profile

Learning appears when the TV interaction is meaningful; detailed learning workflows may hand off to phone/tablet.

Optional module destinations disappear when disabled by instance policy, authorization or the active Profile's module preference.

## 2a. Login and Profile selection

Binding specification: `docs/mockups/login-profile-selection/SPEC.md`.

Jularr distinguishes Account authentication from the active personal Profile:

```text
Login -> Profile selection when needed -> consumer app
```

- Account owns authentication/security, linked login identities and account-level authorization.
- Profile owns personal media state/preferences such as progress, ratings, history, Learning and personal Connections.
- One Account may expose multiple Profiles when instance policy allows it.
- A single usable Profile may open directly on Desktop/Mobile; TV prioritizes explicit Profile selection on shared screens.
- `Switch profile` keeps the Account session; Logout ends it.
- Profile switching never merges progress/history/preferences.

Login is intentionally sparse: branding, credentials, primary Sign in, configured external login providers and optional Passkey/recovery actions. Do not show welcome/marketing copy.

External login providers are capability-driven. Examples include Plex, Jellyfin, Trakt, AniList, MAL or Google when the adapter, instance configuration and policy actually enable Login. Provider identities resolve to an internal Jularr Account and never replace its internal ID.

Login capability is separate from personal synchronization Connections. A provider can support Login, sync, both or neither.

Visual rules:
- Clean Login: plain Light/Dark background, purple default accent, no decorative background artwork.
- Original Jularr Login: same controls/layout with Japanese watercolor/cherry-blossom skin and red/pink default accent.
- Both use the same semantic tokens and support accent hue shifting.

## 2b. Error / permission / availability states

Binding specification: `docs/mockups/error-permission-states/SPEC.md`.

Shared consumer state family:
- Not found / hidden
- Forbidden
- Module unavailable
- Resource missing
- Session expired
- Internal error (500)

Unauthenticated protected routes redirect to the normal Login flow instead of rendering a separate “Bitte einloggen” error page.

Original J uses the approved dark-haired anime character with state-specific emotion/action and Japanese ink/sakura styling. Clean remains visually separate: neutral background, modern minimal illustration/iconography, no anime/sakura/torii motifs.

Hidden routes/resources must not leak feature existence. 500 must never expose raw exception details. All states use normalized application errors and shared components.

## 3. Search and Discover

Search and Discover are one coherent surface.

Interaction:
- tapping/clicking global search opens search UI immediately;
- empty query shows Discover content;
- typed query shows live/local/provider results;
- submitting an empty query remains Discover;
- filters can transition into a full Discover result page.

Discover rows resemble modern streaming discovery:
- For You
- Trending
- Anime
- Series
- Movies
- Books / Light Novels
- Manga
- Games
- genres/themes such as Horror

Rows scroll horizontally. Selecting a row title opens the corresponding filtered Discover view.

Discovery includes items not yet in the local library. Detail pages clearly distinguish available, requested/downloading and discover-only states.

## 3a. Media Preview / Quick View

Binding specification: `docs/mockups/media-preview/SPEC.md`.

Media Preview is an optional cinematic Quick View used mainly in Discover/Search, recommendations and TV browsing. It is not a mandatory intermediate page.

Desktop cards do not expand/reflow on hover. A secondary Quick View action opens the Preview; ordinary card activation opens Detail.

Video Preview:
- wide hero/trailer;
- autoplay only after Preview is actually open/visible;
- muted by default;
- one active trailer maximum;
- external trailer embed preferred over proxying public trailers through Jularr;
- fallback is backdrop, then cover-derived presentation.

Reading Preview uses the same shell but does not force video. It shows cover/backdrop, concise metadata, language/Edition context, short synopsis, progress/next structure where relevant, and Read/Continue/Request.

Preview actions reuse canonical Play/Read/Listen/Request contracts. Trailer playback is promotional and does not create an ActiveSession or MediaProgress.

Clean and Original Jularr use identical structure/behavior; only visual skin tokens differ.

## 3b. Consumer acquisition status vocabulary

Binding Instant Play / Start Watching specification: `docs/mockups/instant-play/SPEC.md`.

All consumer surfaces project the same user-facing Request/acquisition vocabulary.

Preferred labels:
- Waiting for approval;
- Requested / Approved where needed;
- Looking for media;
- Getting episode / movie / media, with reliable progress where useful;
- Preparing;
- Available / Partially available;
- Monitoring future releases;
- Needs attention / Failed;
- Cancelled.

`Search`, `ImportJob`, downloader/importer names, importer phases, verification and post-processing are internal/Admin concepts. Consumer pages may show reliable progress, but should use `Looking for media`, `Getting episode/movie/media` and `Preparing` instead of exposing `Searching`, `Downloading` or `Importing` as the primary Instant Play product states.

This vocabulary is a projection over the canonical Request -> Wanted -> Search -> Download -> Import pipeline; it does not create a second state machine.

## 4. Home

Home is personalized, not a duplicate library index.

Priority sections:
- Continue Watching / Reading / Listening
- Continue Playing, when the profile has resumable Games activity
- Up Next
- optional Watchlist / Reading List shelf when useful
- Recommendations based on the user's library/progress
- Recently relevant additions only when useful
- media-type/genre recommendation rows

Avoid a generic `Recently Added` section dominating Home.

Cards show only useful glanceable information: artwork, title, progress and a small amount of media-specific status. Avoid tag clutter.

## 5. Library

Binding specification: `docs/mockups/library/SPEC.md`.

Library is the profile-facing catalog for media with durable Jularr Library/monitoring context. Discover/Search remains the place for finding new media.

Structure:
- one Library destination;
- internal `Library | Collections` switch;
- media-type scope: All, Anime, Series, Movies, Manga, Light Novels, Books, Audiobooks;
- Search within Library;
- Filter;
- Sort;
- Grid/List where useful.

Consumer Library does not expose a generic Add/import menu. New media comes through Discover -> Request. Manual file/folder import and repair are Admin flows.

Cards reuse one shared media grammar: poster/cover, title, one useful progress/status line, preferred-language availability and optional progress bar. Normal card activation opens Detail. Cards do not expand/reflow on hover; optional Quick View uses the shared Media Preview contract.

Library must not become an Admin dashboard: no statistics sidebar, active-genre panel, release/download internals or duplicated Home-style Continue shelf.

Personal Watchlist/Reading List is a profile-state projection of Library, not a separate acquisition model or mandatory top-level destination. Home/profile links may open Library with the personal-list filter already applied. For written media, the same canonical personal-list state may be labeled Reading List.

Mobile uses a two-column poster/cover grid where width permits. TV is remote-first with large posters and strong focus treatment, not a scaled Desktop grid.

## 5a. Collections

Binding specification: `docs/mockups/collection-detail-edit/SPEC.md`.

Collections live under `Library -> Collections` and reuse normal Library media cards.

V1 modes:
- Manual: explicit membership/order;
- Smart: rules + include/exclude overrides;
- Linked: local Collection synchronized from an external provider/list.

Cross-media is not a separate Collection type. Franchise/adaptation grouping should use canonical relations through Smart/derived views.

Landing stays simple: Search, Filter, Sort, New Collection and mosaic Collection cards. Do not require a permanent Manual/Smart/Linked chip row.

Details use a compact header + Library toolbar/grid. Manual gets Add/Reorder, Smart gets Edit Rules, Linked gets Sync/last-sync. Linked Collections render from local state and remain readable when the provider is offline.

TV is browse-first; complex editing belongs to Web/Mobile/Tablet.

## 6. Canonical media detail page

All media detail pages share a common skeleton while adapting content.

### Header/hero
- artwork/backdrop
- poster/cover
- title and useful alternate title where configured
- concise metadata
- progress/status
- primary action is state-dependent: Play / Continue / Read / Listen when usable; Start watching / Watch now may represent a playback intent that transparently acquires the smallest required playable unit when Playback + instant acquisition are permitted; otherwise Request is the explicit acquisition action;
- secondary actions are personal-state/context actions such as Watchlist/Reading List, Favorite, Collection and options where supported;
- never expose a separate consumer `Add to Library` acquisition action.

### Content
Media-specific sections appear only when relevant.

Anime/Series:
- season selector
- episode list/cards
- availability/language state
- progress

Movie:
- single primary playback action
- available versions/languages

Book / Light Novel / Manga:
- one shared Reading Detail page family
- Parts/Volumes/Chapter structure where meaningful
- context-sensitive labels: Book may use Parts, Light Novel/Manga usually Volumes
- edition/language selector
- Read/Continue
- translation state where applicable
- format-specific behavior moves to the Reader rather than creating separate detail-page designs

Audiobook:
- chapters/tracks
- Listen/Continue
- edition/language/narration details

### Related
- related/adapted works
- recommendations

User pages do not expose release-group/import internals by default.

When Jularr Playback is disabled/unavailable for the instance, consumer media pages remain valid manager-only surfaces: missing media uses Request, successful acquisition ends at Available/Monitoring, and Jularr does not show Start watching, Watch now, Starting playback or Player controls.

## 7. Episode / chapter / volume interaction

Do not force unnecessary intermediate pages.

Episode selection can open a compact detail surface or start playback depending on explicit user action. Chapter selection opens Reader. Admin-only diagnostics are separate.

Progress, availability and language are visible without noisy badges everywhere.

## 8. Video player

The detailed platform contract belongs to issue #403; UX baseline:

### Mobile
- single tap toggles controls only
- large central Play/Pause
- double tap left/right: -10/+30 seconds
- left vertical gesture: brightness where supported
- right vertical gesture: volume where supported
- pinch + Fit/Fill/Zoom
- real landscape fullscreen where platform permits
- screen/control lock
- learning subtitles independent from transient controls

### Desktop
- single click video = Play/Pause
- large central Play/Pause safe hit target
- double click = fullscreen
- visible volume slider
- keyboard controls
- timeline hover preview and chapters

### Tablet
Touch model from Mobile but layout uses extra space. Learning details may use a side sheet in landscape.

### TV
Remote-first focus model, large controls, direct media keys, seek via directional input, TV-safe layout.

### iOS/iPadOS
Explicit Safari/PWA compatibility path; preserve custom inline Jularr UI where WebKit permits and degrade deliberately when system surfaces are required.

Shared menus:
- quality
- speed
- audio
- subtitles
- Fit/Fill/Zoom

Technical Direct Play/Remux/Transcode diagnostics stay outside normal user controls unless requested.

## 9. Learning subtitles

Player controls, normal subtitles and interactive Learning subtitles are separate layers.

Learning in the Player is an optional live mode:
- Learning UI exists only when the instance enables Learning, the current profile has Learning personally enabled, permissions/capabilities allow it and the content supports it;
- when available, the Player exposes an explicit **Learning On/Off** toggle during playback;
- **Learning Off is normal playback**: no Learning hit targets, forced pauses, extra layout reservation or altered gestures;
- switching Learning On/Off does not recreate the PlaybackPlan/ActiveSession, seek, change tracks/quality or otherwise restart playback;
- turning Learning Off closes Learning details and returns immediately to normal Player interaction.

When Learning is On, interactive subtitles:
- remain visible while player controls hide;
- never accidentally trigger generic player tap gestures;
- allow word/sentence interaction;
- preserve playback position and state when entering/exiting learning details;
- adapt layout to Mobile/Tablet/Desktop/TV.

## 10. Reader

Reader prioritizes content and removes application chrome while reading.

Core:
- typography controls
- theme/background
- font size/spacing
- chapter navigation
- progress
- table of contents
- language/edition switch
- translation availability/action
- annotations/highlights/bookmarks
- optional learning interaction

Mobile controls appear on tap and otherwise disappear. Desktop can expose compact side controls without shrinking the reading column unnecessarily.

## 10a. Continuation surfaces

Binding specification: `docs/mockups/continuation-surfaces/SPEC.md`.

Jularr has two distinct compact continuation surfaces:

- **Now Playing** for active video/audio/audiobook/TTS playback. It projects the existing `ActiveSession` and keeps the main transport centered. Desktop uses thumbnail + Work/unit/time on the left, centered Previous/-10/Play-Pause/+30/Next, direct settings where space permits, and Settings overflow with volume inside it. The top edge is the playback/buffer/chapter timeline and can scrub the active session.
- **Continue Reading** for Book/LN/Manga Reader progress. It shows cover, Work/chapter/page progress and one Continue Reading action. It is not a playback toolbar, has no `...` overflow and does not expose Reader controls. Its top progress line is informational, not a scrubber.

Only one persistent continuation slot is shown. Active Now Playing wins over Continue Reading. Dismissing Continue Reading never resets reading progress. TV uses normal Continue Watching/Reading browse surfaces instead of requiring a floating bar.

## 11. Calendar

Unified calendar for:
- upcoming anime/TV episodes
- expected/release dates
- relevant book/manga volume/chapter releases when known

Views:
- responsive month/week/list depending on device
- filters by media type/library status
- selecting item opens canonical detail

Mobile defaults toward a useful agenda/list presentation when month grid becomes cramped.

## 12. Learning

User Learning home:
- Continue Learning
- due reviews
- courses/languages
- media-derived learning
- progress

Vocabulary / Sentences:
- binding specification: `docs/mockups/vocabulary-sentences/SPEC.md`
- one shared Learning Library shell with Vocabulary and Sentences tabs
- Vocabulary remains canonical LearningUnit/Variant/Card state
- Sentences reuses canonical LearningContext/Sentence Practice data rather than inventing a second sentence store

Progress / Achievements:
- binding specification: `docs/mockups/progress-achievements/SPEC.md`
- bounded learner analytics over canonical Learning state/events
- Course progress, Review/FSRS state, Vocabulary state and gamification remain separate
- unavailable XP/time/Streak/Achievement metrics are omitted until their canonical contracts exist

Script Trainer / Kana:
- binding specification: `docs/mockups/script-trainer/SPEC.md`
- one trainer flow with sequential Overview -> Practice -> Feedback -> Summary states
- Japanese currently provides Hiragana/Katakana via the ScriptTrainer toolkit
- Kana uses canonical LearningUnit/LearningCard/Review state; no second Kana scheduler
- handwriting Writing mode stays hidden until real stroke/evaluation capability exists

Course detail:
- binding specification: `docs/mockups/course-detail/SPEC.md`
- canonical Curriculum -> Level -> Chapter -> Lesson structure
- curriculum progress separated from FSRS review mastery
- exact resume/start action
- compact responsive curriculum navigator rather than a game-map hierarchy

Lesson/review surfaces are distraction-minimized and touch/keyboard friendly.

Personal AI/provider configuration belongs in Settings, not inside every learning screen.

## 13. Profile

Contains:
- user identity/profile switch where applicable
- Activity/history
- watch/read/listen history
- quick link to Devices & Sessions under Settings
- quick link to Settings

Activity is not a main mobile navigation item.

## 14. User Settings

Grouped, searchable settings rather than one long form.

Sections:
- Appearance
- Language & regional
- Library/display preferences
- Playback
- Audio & subtitles
- Reader
- Ratings
- **Modules & Features** — personal On/Off for instance-enabled, permitted optional modules
- Learning
- Notifications
- AI / personal provider
- Connections
- Devices & Sessions
- Profile & Privacy
- Account/security

Module availability resolves as:
`instance module -> authorization/capability -> profile module preference -> detailed feature setting`.

A profile module toggle can only narrow availability. It cannot enable an instance-disabled or unauthorized module. Turning a module Off preserves its stored state and does not stop shared instance work required by other users.

Appearance supports Visual style (Clean / Original Jularr), Light/Dark/System and configurable accent/color scheme through shared tokens. Visual style changes skin/branding only; it never changes page structure or feature availability.

## 15. Request flow

Binding specification: `docs/mockups/add-request-flow/SPEC.md`.

There is exactly one consumer acquisition action: **Request**.

Request starts from an already selected/resolved canonical Work/target. The Request surface does not contain media search or title selection.

Normal Request UI:
1. compact media identity;
2. Scope only when structural selection is meaningful;
3. Included content derived from Scope;
4. Language / Edition where relevant;
5. privileged Advanced override only when permitted;
6. Cancel + Request.

There is no Add/Add & Monitor consumer path and no numbered wizard.

Approval policy is backend behavior:
- a normal Request may wait for Admin approval;
- an authorized/policy-matched Request may be auto-approved immediately.

Both still use the same visible `Request` action.

Personal state such as Watchlist/Reading List, Watching/Reading/Listening, progress, completed state, ratings and external sync is separate from acquisition and never embedded into the Request dialog.

Manual local import and release selection remain Admin/advanced workflows.

## 16. Request / missing media UX

Consumer surfaces project one shared state vocabulary:
- Available / Partially available;
- Request;
- Waiting for approval / Requested;
- Looking for media;
- Downloading;
- Preparing;
- Monitoring future releases where relevant;
- Needs attention / Failed;
- Cancelled where relevant.

Do not expose `Wanted`, `Searching`, `Importing`, release candidates, downloader state or importer phases as normal consumer product states.

Status/details behavior is owned by `docs/mockups/request-status-details/SPEC.md`.

## 17. Admin mode/navigation

Entering Admin expands/replaces navigation with explicit admin destinations while preserving a clear way back to normal Jularr. Admin pages whose feature is incomplete (today AI and Subtitles) form the last, subordinate `Unfinished` group.

The target Admin shell uses **one permanent navigation destination per owning area**. Tabs, editors, repair dialogs and compatibility routes do not become duplicate sidebar entries.

Old pages that have no owning area in this architecture yet form one subordinate **Legacy** group after the area groups (before Unfinished): today the anime Acquisition page, Import settings (naming), Mapping, Sonarr, Sessions, Scans, Logs, Health, Transcoding, Localization and API Keys. A page leaves Legacy when its owning area replaces it or its route redirects there. `UiNavigationCatalog.Admin` is the one table behind the sidebar and the phone/tablet tab row; an area gets its entry there, in the group named for it, when its page is built (Storage, Notifications, Backup & Restore, Migration, General Settings and Acquisition Profiles are not built yet).

### Overview
- Dashboard
- Activity / To-Do
  - History is the third tab of this operational area; a deep-linkable History route may remain for compatibility, but it is not a second permanent sidebar destination.

### Media
- Library
- Wanted
- Requests

Library here is a navigation bridge to the canonical Library/media-management context; it does not create a second Admin-only library identity/store. Authorized management actions may open Admin Media Detail from that canonical library context.

Admin Media Detail and Manual Search are contextual workflows opened from Library/Wanted/Requests/related objects. Manual Search is the Search tab of the reusable Acquisition dialog and is not a permanent sidebar page.

### Acquisition & integrations
- Downloader
  - Übersicht, Queue, Server, Verarbeitung, Geschwindigkeit & Zeitplan, Einstellungen, Externe Clients are secondary navigation inside Downloader.
- Providers
  - Indexer/Search, Metadata, Subtitles, Translation, Reading Sources and other supported provider families live here.
- Acquisition Profiles
  - quality/upgrade, Release Rules, language, wait/source policy and score testing.

AI provider/model configuration stays in the dedicated **AI** Admin area rather than being folded into generic Providers.

### Administration
- Storage
- AI
- Users & Permissions
- Devices & Sessions
- Notifications
- Backup & Restore
- Migration
- System & Diagnostics
- Instance
- General Settings
- Appearance


### Cross-cutting Admin ownership notes

The following concerns intentionally do **not** get additional permanent top-level sidebar entries:

- **Naming / organization** — configured from the relevant LibraryRoot/Storage or canonical library-management context. Naming templates/policies belong to the content importer/library layer; Storage owns the target root/path/placement policy. Legacy `/Settings/Naming` and `/Settings/ReadingNaming` become compatibility/contextual routes after parity.
- **Identity / mapping correction** — opened contextually from Admin Media Detail, To-Do, Reconciliation or another owning workflow. Legacy Mapping Review/Segments routes do not become a permanent "Mapping" Admin area.
- **UI translations** — contextual child of General Settings → Language & Localization. Personal language remains User Settings; Translation providers remain Providers.
- **API keys / automation** — until a broader API/Webhook/Automation contract is approved, API Keys remains a transitional technical sub-surface. A future **API & Automation** owner may absorb API keys, webhooks and automation credentials; do not create a new main destination merely for the existing API-key page.
- **Administrative audit** — durable typed audit evidence is surfaced through System & Diagnostics/contextual links, not as another top-level Admin destination.

### Storage ownership and lifecycle

Binding specification:
- `docs/mockups/admin-storage/SPEC.md`

**Storage** is the only permanent Admin owner for:
- physical Mounts and LibraryRoots;
- capacity/free-space/reserve;
- safe path/routing and placement capabilities;
- storage lifecycle policies (#414);
- Review candidates and policy conflicts;
- optimize/tier/delete Dry Runs;
- physical root-to-root migration/evacuation;
- storage forecast/growth/top-waste and lifecycle history.

Do not create permanent sidebar entries named Cleanup, Optimize, Tiering or Storage Migration. They are secondary Storage surfaces/contextual workflows.

Admin Media Detail may expose contextual `Storage` / `Optimize` / `Keep` actions for one Work, but they open the same canonical Storage Lifecycle engine.

System & Diagnostics may show read-only capacity/health summaries only. Activity / To-Do owns running/failed lifecycle operations. Migration Center owns external-system migration, not physical media movement between Jularr roots.

Destructive UX requirements:
- always show why the item is eligible and what protects/blocks it;
- show before/after requirement coverage when a version/track may change;
- show logical vs estimated physical savings where they differ;
- show reversibility/grace/reacquisition state;
- no bulk destructive confirmation without a Dry Run unless the already-enabled Automatic policy itself is executing;
- Compact mode must never hide destructive-action context.


### Games
When Games is actually available, Games-specific Admin configuration is one contextual/dedicated area with secondary surfaces for Runtimes and BIOS/Firmware. Game LibraryRoots remain in Storage, provider configuration in Providers, acquisition/downloader work in the shared acquisition areas and failures in Activity / To-Do.

### Contextual / non-sidebar flows
The following are reachable by deep link or from their owning page, but are not permanent sidebar destinations:
- Admin Media Detail;
- Manual Search / Acquisition dialog;
- Download Assignment;
- Library Reconciliation / Folder Import Mapping;
- Games Runtime Editor;
- BIOS/Firmware Add or Replace;
- Game Import Resolution;
- Setup Wizard after first-run completion.

### Legacy-route consolidation
Existing routes may remain temporarily as redirects/deep links while implementation is migrated, but must not remain parallel configuration owners:
- `/Admin/Resources`, `/Admin/Health`, `/Admin/Logs` -> System & Diagnostics;
- `/Admin/Sessions`, `/Admin/Devices` -> Devices & Sessions;
- `/Admin/History` -> Activity / To-Do → History;
- `/Admin/Usenet` -> Downloader;
- `/Admin/Sonarr`, `/Settings/SonarrMigration` -> Migration / the Sonarr manager adapter;
- `/Settings/Indexers`, provider-specific configuration pages and `/Admin/ReadingSources` -> Providers where the concern is provider configuration;
- `/Settings/DownloadClients` -> Downloader → Externe Clients;
- legacy sections of `/Settings/Acquisition` -> Acquisition Profiles, Storage, Downloader or Backup/Restore according to the owning contract;
- `/Admin/Scans` -> scan/reconcile actions originate from Storage/LibraryRoot and their running/result state is surfaced through Activity / To-Do / History.

Only show destinations actually implemented, permitted and backed by their owning runtime capability.

### Admin UI density

Binding specification:
- `docs/mockups/admin-instance/SPEC.md`

The entire Admin UI has one profile-scoped presentation preference:
- **Detailliert**
- **Kompakt**

This is a shared Admin-shell preference, not a per-page setting and not an instance-wide module setting.

Compact mode is table/list-oriented and reduces repeated descriptions, card padding, row height and form spacing where safe. Detailed mode exposes more inline explanation and context. Both modes use the same data, permissions, validation and actions; business behavior may never depend on the selected density.

The preference applies across Admin Dashboard, Instance, Storage, Downloader, Providers, AI, Users/Permissions, Activity/To-Do/History, Wanted/Requests, Backup/Restore, Migration, System & Diagnostics and future Admin screens using the shared Admin shell.

Compact mode must not hide errors, warnings, destructive-action context or required information. On touch/mobile layouts, minimum touch-target sizes remain intact even when Compact is selected.

## 18. Admin dashboard

The Admin Dashboard is the live operational/health surface for Jularr. It is not a media-library statistics page.

Binding screen specification:
- `docs/mockups/admin-dashboard/SPEC.md`

Approved current visual reference:
- `docs/mockups/admin-dashboard-clean-live.png`

The detailed screen spec defines live service health, CPU/RAM/GPU/network/load, active streams with Direct Play/Remux/Transcode diagnostics and controls, downloads, Jularr tasks, storage, warnings and live activity. Global UX rules in this document still apply.

## 19. Admin media detail

Admin Media Detail uses a direct hierarchy-first V1 instead of distributing the same information across many technical tabs.

Binding screen specification:
- `docs/mockups/admin-media-detail/SPEC.md`

V1 centers on monitoring, automatic/manual acquisition, expandable Anime/Series seasons and episodes, and real per-file details. Anime may switch between Standard and AniList display groupings, but provider groupings are views/mappings over the canonical stored episode structure and never create parallel persisted episode models.

## 20. Wanted / Missing

Binding screen specification:
- `docs/mockups/admin-wanted/SPEC.md`

Wanted is the technical acquisition worklist for requested/approved, missing, searching and failed acquisition needs across all media types. User request moderation is separate in Admin Requests.

Selecting a Wanted target opens the reusable Acquisition dialog with exactly three primary tabs:
- Search
- Current
- History

Search is the default/main tab. Current explains the active profile/language/desired version and existing local state. History shows target-scoped search, grab, import and failure events.

## 21. Manual Search

Binding screen specification:
- `docs/mockups/admin-manual-search/SPEC.md`

Manual Search is the Search tab of the reusable Admin Acquisition dialog rather than an independent acquisition workflow.

It is Sonarr/Radarr-like in diagnostic depth but uses Jularr's visual language and canonical identity model.

Normalized candidates may expose:
- title
- source/indexer
- age
- size
- quality/format
- languages
- audio/subtitles
- release group
- release type such as single/multi/season pack
- parsed target and match confidence
- effective-profile score
- rejection/warning reasons

The primary score is contextual to the selected acquisition profile + language target. Optional comparison columns may show scores for other profiles.

Rejected and suspicious candidates remain visible by default so the admin can understand why automatic acquisition did not choose them. This includes likely wrong-episode/unit matches. Identity mismatches are clearly marked and can never be automatically grabbed; any permitted manual override requires explicit confirmation and target mapping.

Candidate columns are configurable and all meaningful fields are filterable. Default ordering follows the canonical selection hierarchy: identity/eligibility state, quality/fallback tier, preference score, coverage utility and configured source/tiebreak policy. Network response order is never meaningful.

## 22. Downloads / Imports inside Activity

There is no standalone permanent Imports page in V1.

Operational flow:
- active downloads and import processing appear in Admin Activity;
- successful completion appears in History;
- failed/ambiguous work that requires human intervention appears in To-Do.

To-Do can open two distinct mapping flows:

1. **Download Assignment** — for one or more files from a completed download job. Desktop is a compact editable table with one file/episode per row. Binding spec: `docs/mockups/admin-download-assignment/SPEC.md`.
2. **Library Reconciliation / Folder Import Mapping** — a dedicated multi-step Admin page for folders/files discovered by Library Scan / Reconciliation that cannot be associated reliably. It supports expandable folder trees, split/merge, batch/per-file mapping, organization policy, rename/move dry-run and execution. Binding spec: `docs/mockups/admin-folder-import-mapping/SPEC.md`.

Both flows map into the canonical media hierarchy. Neither asks the admin to type raw database IDs or arbitrary destination paths as the normal UX.

## 23. Storage admin

Binding screen specification:
- `docs/mockups/admin-storage/SPEC.md`

Storage separates **physical Mounts** from **logical storage roles**.

Physical Mounts own capacity/health. Multiple LibraryRoots may live on the same Mount without pretending to be separate disks.

Storage roles include:
- LibraryRoots for final specialized libraries;
- Native Download Workspace for Jularr's built-in Usenet downloader;
- Generic Downloads Root for content that has no specialized library;
- optional future managed workspaces such as backup/cache/transcode.

The Native Download Workspace is temporary operational storage for incomplete download, verification/repair, extraction and staging. It is distinct from final Generic Downloads.

Path selection uses a server-side safe browser restricted to permitted Mounts. Native downloader transport/server settings live in the dedicated Downloader Admin area, not Storage or Acquisition.

## 23a. Native Downloader Admin

Binding specifications:
- `docs/mockups/admin-downloader/SPEC.md`
- `docs/mockups/admin-downloader-overview/SPEC.md`
- `docs/mockups/admin-downloader-queue/SPEC.md`
- `docs/mockups/admin-downloader-servers/SPEC.md`
- `docs/mockups/admin-downloader-processing/SPEC.md`
- `docs/mockups/admin-downloader-speed-schedule/SPEC.md`
- `docs/mockups/admin-downloader-settings/SPEC.md`
- `docs/mockups/admin-downloader-external-clients/SPEC.md`

Downloader is one Admin destination with secondary navigation:
- Übersicht
- Queue
- Server
- Verarbeitung
- Geschwindigkeit & Zeitplan
- Einstellungen
- Externe Clients

The Downloader area owns native Usenet transport/download mechanics, technical queue/history, server health, verify/repair/extract behavior, bandwidth/concurrency/scheduling and optional external-client adapters.

It does **not** duplicate:
- Storage mounts/workspaces;
- Wanted or Manual Search;
- AcquisitionProfile scoring;
- Indexers;
- post-download manual assignment;
- global Activity/History;
- host-wide system telemetry.

Overview may show downloader-specific throughput, workspace pressure and bottlenecks. Queue owns deep per-job diagnostics. Server owns NNTP configuration/health. Processing owns Verify/Repair/Extract/Cleanup. Speed & Schedule owns bandwidth, concurrency and timed actions. Settings owns general retry/duplicate/retention/cache behavior. External Clients is compatibility-only; native Usenet remains the normal/default path.

## 23b. Acquisition Profiles & Scoring

Binding specification:
- `docs/mockups/admin-acquisition-settings/SPEC.md`

Acquisition is centered on one reusable **Acquisition Profile** rather than separate Sonarr-style Quality Profile, Custom Format, Release Profile and Delay Profile screens.

One profile owns:
- quality/tier ordering and upgrade cutoff;
- minimum acceptance score and upgrade-until-score;
- normalized language policy;
- reusable Release Rules with profile-specific effect/score;
- explicit hard Require and Reject rules;
- per-quality/group size policy where supported;
- wait/delay and source/provider preference;
- default-per-media-kind and per-Work assignment;
- one canonical Score-Test/explanation flow.

Shared Release Rule definitions answer *what is detected*; the effect inside a profile answers *what this profile does with it*. A shared rule can therefore score differently or reject in different profiles without duplicating its matcher.

Normal workflow stays inside the profile. A secondary Rule Library is allowed, but there is no required permanent Custom Formats sidebar destination.

Automatic acquisition and Manual Search must use the same canonical Search Planner (`docs/ACQUISITION_SEARCH_PLANNER.md`). Automatic acquisition, Manual Search and Score-Test must use the same canonical selection/scoring explanation (`docs/AUTOMATIC_RELEASE_SELECTION.md`). Sonarr Quality Profiles, Custom Formats, Release Profiles and Delay Profiles are migration inputs translated into the Jularr model, not parallel runtime models.

Provider credentials remain in Providers, downloader transport in Downloader and paths in Storage.

### Import/routing ownership

There is no standalone permanent **Import & Routing** Admin destination.

Ownership is split by why the setting exists:
- **Storage → LibraryRoot** owns the default final destination per media/content type and the effective import placement policy: `HardlinkOrCopy`, `Hardlink`, `Copy`, or `Move`.
- **Downloader → Externe Clients** owns remote-path mappings needed because an external downloader reports a different path than Jularr sees.
- **Migration / coexistence adapter** owns source-path mappings needed by Sonarr/Radarr/legacy integrations.
- **Acquisition Profile** owns release selection, scoring, language, wait/delay and source/provider preference, never filesystem paths.
- the native Jularr downloader normally requires no remote-path mapping.

All local mapping targets resolve into permitted Storage. A Work-level target-root override may supersede the content-type default through the canonical library/monitoring contract.

## 24. Provider settings

All provider types use a common configuration pattern:
- provider card/list
- enabled state
- health
- configuration
- Test button
- capabilities
- priority/order where relevant

Secrets are write-only/masked.

Do not build completely different settings UI for every provider.

## 25. AI admin

Dedicated Admin AI surface:
- configured providers/models
- server default model per task/category
- availability policy: disabled, admin-only, shared instance, user/group policy where supported
- task permissions
- health/test
- generated artifact visibility policy

Admin can invoke appropriate server AI tools from Admin workflows. Normal users use personal AI unless instance policy explicitly grants shared AI.

## 26. Users & permissions

User list -> user detail.

User detail:
- roles/groups
- capabilities
- media/request permissions
- learning permissions
- AI policy
- profile restrictions
- active devices/sessions where appropriate

UI is capability-based; avoid scattering hard-coded `IsAdmin` assumptions through pages.

## 26a. Admin Devices & Sessions

Binding specification:
- `docs/mockups/admin-devices-sessions/SPEC.md`

Devices & Sessions consolidates the current Admin Sessions and Admin Devices surfaces into one cross-user area with:
- Live Sessions
- Geräte
- Anmeldungen & Sicherheit

The Admin Dashboard keeps only the live operational summary. User detail reuses the same data/components filtered to one account/profile, and profile self-service exposes only the current profile's own devices/sessions.

Current device removal is explicitly **not** permanent authentication revocation: the existing KnownDevice registry can forget a device and end matching playback, but a true `Gerät abmelden` requires a revocable per-device authentication/session contract.

## 27. Admin Requests, Activity and History

Binding screen specifications:
- `docs/mockups/admin-requests/SPEC.md`
- `docs/mockups/admin-activity/SPEC.md`
- `docs/mockups/admin-history/SPEC.md`

Requests moderates user requests and hands approved acquisition needs into Wanted.

Activity / To-Do is the live/pending operational work queue for imports, remux, repack/replace, subtitles, translations, metadata, AI and maintenance.

History is the past operational record with category/date filters and actor/result details.

## 27a. System & Diagnostics

Binding specification:
- `docs/mockups/admin-system-diagnostics/SPEC.md`

System & Diagnostics consolidates the existing Health, Resources and Logs technical surfaces into one Admin destination with tabs:
- Übersicht
- Ressourcen
- Abhängigkeiten
- Logs
- Diagnose
- Updates
- Runtime

The existing `/Admin/Resources` telemetry is reused as the Resources tab and remains scoped to the Jularr + PostgreSQL stack and mounts visible to Jularr. The standalone Resources navigation entry is retired after feature parity; the old route may remain as a compatibility redirect.

Boundaries:
- Dashboard = live operational glance and current activity;
- System → Ressourcen = detailed stack telemetry/history;
- Storage = mount/path/LibraryRoot configuration;
- Runtime = global worker/job runtime only;
- Downloader/Provider/AI keep their own technical settings.

Overview may show small current resource values, but detailed charts/history live only in Resources. System must not become a second Storage/Downloader/Provider/AI configuration hub.

## 27b. Admin Appearance

Binding specification:
- `docs/mockups/admin-appearance/SPEC.md`

Admin Appearance owns the instance default visual style and whether profiles may override theme/accent. It also exposes the current admin's existing profile-scoped `Detailliert | Kompakt` preference as a shortcut; that preference remains separate from instance appearance settings.

Visual contract:
- **Original Jularr** uses the established Japanese ink/watercolor/cherry-blossom treatment with red/pink default accent and may use anime/Japanese decorative references.
- **Clean** is neutral/minimal with the purple default accent and never uses decorative anime/Japanese artwork. Clean theme previews should use representative film/series or real configured library media rather than anime artwork purely for decoration.

Both styles are skins over the same routes, components, permissions and information architecture.

## 27c. Admin General Instance Settings

Binding specification:
- `docs/mockups/admin-general-settings/SPEC.md`

General Instance Settings owns only instance-wide identity and regional/default presentation values such as instance name, default UI language, locale, timezone, time/date/number formatting and normalized metadata/title-language defaults.

This is a normal settings page, **not a Setup Wizard continuation**. It must not show numbered setup steps. Setup status may appear only as a compact read-only instance-information block with a link to reopen Setup.

The Setup Wizard and this page must use the same canonical settings store. Appearance, Modules, Storage, Providers, Acquisition, AI, Backup and Runtime remain in their owning Admin areas.

## 27d. Admin Notifications

Binding specification:
- `docs/mockups/admin-notifications/SPEC.md`

Admin Notifications owns the instance's technical notification delivery layer:
- registered/configured notification sinks;
- channel health and tests;
- read-only canonical event catalog/audience/severity;
- delivery diagnostics/history once backed by a durable delivery-attempt store.

Current implementation has a real **In-App** sink only. Push/Digest exist in the model but must not appear as working transports until a real sink/configuration contract exists. Future E-Mail, Web Push, Webhook/Home Assistant or other channels plug into the shared `INotificationSink` pipeline rather than creating parallel notification systems.

Profile Notification Settings continue to own each user's event preferences. Activity/History remain operational-job surfaces and are not duplicated by notification delivery history.

## 27e. Admin Backup & Restore

Binding specification:
- `docs/mockups/admin-backup-restore/SPEC.md`

Backup & Restore manages versioned Jularr **application-state** backups, automatic scheduling/retention, validation and compatibility-aware restore.

Normal backups include canonical database/application/configuration state and explicitly registered durable non-database state. They do **not** copy canonical media payload files.

Backup destinations are Storage-owned Backup Targets. Restore uses preflight + semantic preview + explicit confirmation, creates a verified Pre-Restore safety backup before destructive mutation, validates after restore and reports partial/failure states explicitly. Legacy Acquisition-only backup bundles are migration inputs, not a second permanent backup system.

## 27f. Admin Migration Center

Binding specification:
- `docs/mockups/admin-migration/SPEC.md`

Migration Center is the preview-first import/handover surface for older Jularr data and supported external managers/media servers.

It normalizes source state into canonical Jularr contracts:
- Media Core / Library;
- Storage LibraryRoots;
- Monitoring/Wanted;
- Acquisition Profiles + shared Release Rules;
- Accounts/Profile progress where the source supports them.

Source-specific path translation belongs to that migration/coexistence adapter and resolves only into permitted Storage. External downloader mappings remain with Downloader → Externe Clients. There is no standalone global Import & Routing/Remote Path Mapping destination.

Dry Run is mandatory before persistent migration. Unsupported or ambiguous source semantics remain visible in preview/report rather than being silently guessed.

The generalized manager-coexistence contract is approved. Manager-style integrations expose, only when their adapter supports the required safety capabilities, the Work-level modes **Extern verwaltet**, **Gemeinsam** and **Jularr verwaltet**. Defaults are configured per integration × media/content type with optional per-Work overrides. At most one external manager may co-manage a Work with Jularr. **Gemeinsam** is a permanent advanced mode and fails closed when the external manager cannot be observed. After handover to **Jularr verwaltet**, the integration may remain connected for read-only conflict observation; disabling external monitoring is an explicit, previewed handover option.

## 28. Responsive profiles

### Mobile
- bottom navigation
- one-column flows
- sheets/fullscreen dialogs rather than tiny desktop modals
- >=44px touch targets
- thumb-friendly primary actions
- no hover-only behavior

### Tablet
- adaptive two-pane layouts where useful
- touch remains primary
- sidebar may collapse/expand based on width

### Desktop
- persistent sidebar
- keyboard/mouse optimized
- denser tables and hover affordances allowed
- resizable/split views only where they improve real workflows

### TV
- focus navigation
- large typography/targets
- safe areas
- no dense admin UI; Admin remains web-first unless a TV-specific need exists

## 29. Shared components

Before page implementation, define/reuse:
- AppShell / navigation
- PageHeader
- SearchBox
- MediaCard
- MediaRow
- Poster/Cover
- ProgressIndicator
- StatusIndicator
- FilterBar
- EmptyState
- ErrorState
- Skeleton/loading state
- Button/IconButton
- Menu
- Dialog/Sheet
- Tabs/segmented control
- DataTable/admin list
- Form fields
- Toast/notification
- ProviderCard
- JobStatus

No local clone of a component just to alter spacing/color.

## 30. Visual rules

### Admin light baseline

For the currently approved Admin planning mockups:
- use a clean light theme;
- use the compact Jularr Admin shell/sidebar;
- avoid decorative background artwork on operational Admin pages;
- status/type tags use borders and only lightly tinted backgrounds;
- small category/status icons may use accent colors;
- avoid fully saturated colored pills/badges;
- keep dense information structured and calm;
- Mobile keeps the same hierarchy but uses larger touch-friendly cards/controls.


- Shared spacing/radius/type/color tokens.
- Robotic/tag-heavy metadata presentation is avoided.
- Use icons where meaning is established; pair with labels when ambiguity exists.
- Accent color is used selectively, not as large saturated surfaces everywhere.
- Cards remain visually calm and consistent.
- Media artwork keeps consistent aspect ratios.
- Focus, hover, pressed, disabled and selected states are all defined.
- Dark mode is designed, not generated by simply inverting colors.

## 31. State requirements for every screen

Each mockup/spec must explicitly account for:
- loading
- empty
- ready
- partial/degraded provider/storage state
- recoverable error
- forbidden/permission state where relevant
- offline/PWA state where relevant

An agent may not treat only the ideal populated state as complete.

## 32. Navigation/back behavior

- Browser Back works predictably.
- Closing a detail sheet returns to prior context/scroll position.
- Mobile back never unexpectedly exits playback/reader without expected platform behavior.
- Admin mode retains its own navigation context.
- Deep links to Work/Episode/Chapter/Admin diagnostics are valid where authorized.

## 33. Mockups required before implementation

Approve at minimum:
1. Home — desktop/mobile
2. Library — desktop/mobile
3. Search/Discover — desktop/mobile
4. Anime/Series detail — user
5. Movie detail — user
6. Reading detail (Book / Light Novel / Manga) — user
7. Audiobook detail — user
8. Player — mobile/desktop/tablet/TV
9. Reader — mobile/desktop
11. Calendar — desktop/mobile
12. Learning home + lesson/review
13. Settings — desktop/mobile
14. Admin dashboard
15. Admin media detail
16. Wanted/Missing + Manual Search
17. Activity / To-Do + Download Assignment + Library Reconciliation wizard
18. Storage/path browser
19. Native Downloader — Overview/Queue/Server/Processing/Speed/Settings/External Clients
20. Providers/settings
21. AI admin
22. Users/permissions

Mockups are binding UX references. Agents must not redesign them during implementation without updating/approving the spec.

### Mockup repository convention

Each substantial screen should use its own folder:

```text
docs/mockups/<screen>/
  SPEC.md
  desktop.png
  mobile.png
  tablet.png
  tv.png
```

Only create platform images that are actually needed. `SPEC.md` contains the binding screen-specific requirements; `docs/UX.md` contains global/shared UX rules. Existing root-level mockup assets may remain temporarily until moved without losing binary history.

## 34. Implementation gate

A vertical slice may start only when:
- canonical domain ownership is known;
- relevant architecture contracts are known;
- screen/flow is described here or in a linked spec;
- required major mockup is approved;
- loading/empty/error/responsive states are defined;
- acceptance criteria exist.

This prevents implementation agents from inventing product structure while coding.

### Canonical search identity and Anime provider view

Search is canonical-first across the product. Series/Anime use one Jularr work with seasons/episodes even when a provider such as AniList models seasons, parts or specials as separate media entries.

Default search groups results by media type and deduplicates provider hits into canonical Jularr works. When the Anime filter is active, the result surface may switch between **Jularr** (canonical grouped work, default) and **AniList** (provider-native entries). A specific season/part query may deep-link directly to that season/provider presentation, but still opens the same canonical work. Home, Discover, Library, Calendar and Requests must resolve through the same identity layer rather than inventing surface-specific duplicates.

