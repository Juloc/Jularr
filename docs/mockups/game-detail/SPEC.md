# Game Detail

Status: approved UX/layout direction for Desktop, Mobile and TV. The owner will upload the approved mockup images into this folder.

Shared contract: `docs/mockups/games/SPEC.md`.
Games page: `docs/mockups/games-library/SPEC.md`.
Player: `docs/mockups/game-player/SPEC.md`.
Games architecture: #725.
UX planning: #729.
Browser runtime plan: #771.

## Purpose

Canonical consumer detail page for one Game.

It answers:
- what is this Game;
- which platform/release is locally available;
- whether the current profile can Play or Continue now;
- what recent save state exists;
- which materially different releases/versions are available;
- which screenshots and other consumer metadata are useful.

It is not an emulator configuration, file browser, downloader or Admin page.

## Shared information architecture

Desktop, Mobile and TV use the same underlying information hierarchy:

1. Game identity / hero;
2. primary Play / Continue action;
3. most recent resumable save, when one exists;
4. Overview;
5. Versions / Releases;
6. Save States;
7. Screenshots;
8. related/recommendation content only when useful.

The rendering may differ substantially per platform.

Do not force Desktop composition onto Mobile or TV.

## Canonical hero data

The hero may show:
- cover;
- backdrop;
- canonical title;
- optional short tagline;
- concise description;
- primary platform;
- release year/date;
- developer;
- publisher;
- small genre/theme chips;
- playability state.

Do not show:
- provider IDs;
- hashes;
- ROM filenames;
- BIOS paths;
- EmulatorJS core names;
- downloader/indexer data.

The visual mockups may use an illustrative fictional game; implementation uses canonical Jularr Game metadata.

## Primary action rules

### Resumable local Game

Primary action:
- `Continue`

Secondary play action may be:
- `Play from Beginning`

The most recent resumable save is shown as a separate compact card/row near the primary action.

### Local Game with no resume state

Primary action:
- `Play`

### Multiple meaningful launch targets

Primary action remains `Play` / `Continue`, but selecting it opens Play Options when Jularr cannot safely choose one release/runtime.

Do not force Play Options when there is one obvious playable target.

### Local but not currently playable

Replace Play with a concise unavailable state:
- compatible runtime unavailable;
- required BIOS missing;
- browser/device unsupported;
- release unavailable.

Normal users get the reason only.

Authorized Admin users may receive a deep link to the relevant Admin Games remediation page, but Admin forms never appear inline.

### Not local

Use shared `Request` behavior.

Do not create a Games-specific request/acquisition flow.

## Device-local Offline / install state

Game Detail distinguishes three separate concepts:

1. canonical/shared server Games library availability;
2. acquisition/request state for obtaining a GameRelease on the Jularr server/library;
3. current-device local install/package state for Offline play.

Do not collapse these into one generic `Downloading` state.

When the current client exposes a managed Games Offline/install adapter (#851), the detail page may show a secondary device-local action/status:

- **Offline installieren** / **Für Offline installieren**;
- **Wird auf diesem Gerät installiert**;
- **Offline spielbar**;
- **Update verfügbar**;
- **Offline-Installation fortsetzen**;
- **Offline-Installation erneut versuchen**;
- **Lokale Installation entfernen**.

The action resolves through the Games-owned install/package owner and may project into the shared `Downloads & Offline -> Games` manager.

Ready means more than raw files existing. The Games owner must be able to resolve the required:
- canonical `GameRelease`;
- complete verified release resources;
- runtime capability;
- required BIOS/firmware where applicable;
- local package/install metadata.

External launcher installs use a separate capability vocabulary such as:
- **Installiert**;
- **Offline spielbar**;
- **Launcher erforderlich**;
- **Online-Anmeldung erforderlich**;
- **Offline-Status unbekannt**.

Jularr must not claim ownership of external install bytes or offer generic remove/migrate actions for them unless the integration actually owns those operations.

### Offline package update/removal

For Jularr-managed local Game packages:
- a failed update must not destroy the last verified playable generation;
- install/update should switch to the replacement only after complete verification;
- normal package removal/update preserves profile saves unless the user explicitly removes save data;
- multi-file/multi-disc releases remain one logical install;
- saves/playtime remain Games-owned state, not MediaProgress.

When a genuine cross-device save conflict exists after Offline play, route it through the Games save owner/future reconciliation contract (#862); never silently overwrite one divergent binary save with another.

## Recent save / resume card

When resumable state exists, show one prominent latest-save card near the Play action.

Useful fields:
- thumbnail/screenshot;
- `Saved 2 min ago` or equivalent;
- save/slot name;
- optional in-game location if metadata is available;
- action to Continue/open Save States.

Do not fabricate in-game location/level metadata if the runtime/game does not provide it.

The card represents profile state, not shared Game metadata.

## Overview

Default content area.

Show:
- description;
- platform;
- release date/year;
- developer;
- publisher;
- genres;
- optional estimated/recorded play time only when meaningful;
- optional player-count/local-multiplayer metadata when reliable.

Keep this compact.

No technical runtime/file information belongs here.

## Versions / releases

One canonical Game may have multiple `GameRelease` entries.

Version cards/rows may show:
- artwork/cover variation where available;
- region/territory;
- platform;
- revision/version;
- NTSC/PAL/NTSC-J or other release-standard label where relevant;
- language summary;
- local availability;
- playability;
- preferred/default indicator.

Examples represented in the approved mockup:
- USA;
- Europe;
- Japan.

Do not duplicate the canonical Game page per region.

Do not expose raw paths/hashes/core IDs by default.

If there is only one release and no user-facing reason to choose another, the Versions section may be omitted or simplified.

## Save States

Per-profile consumer surface.

Each Save State card/row may show:
- screenshot thumbnail;
- user-visible slot/name;
- created/updated time;
- optional play-time/location/level metadata when known;
- overflow actions.

Possible actions:
- Continue/Load;
- rename;
- delete;
- duplicate/export only when intentionally supported.

Normal SRAM/in-game save and manual Save States remain distinct internally.

Do not turn this section into a generic filesystem manager.

## Screenshots

Show a horizontal gallery/grid of screenshots captured from this Game.

Rules:
- reuse Jularr image/gallery behavior;
- `View all` only when there are enough items;
- selecting an image opens the normal image viewer;
- no separate screenshot-management application is created here.

Screenshots created from the Player may appear here after persistence succeeds.

## Favorite / secondary actions

Mobile may expose `Favorite` as a direct secondary action because there is limited horizontal space and it is a common consumer action.

Desktop/TV may place Favorite in:
- a secondary button when useful; or
- `More` when the visual composition is cleaner.

The action semantics are shared; platform placement may differ.

`More` may include only consumer-relevant actions, for example:
- Favorite / unfavorite;
- add/remove collection;
- choose version;
- manage user-owned Save States;
- report metadata issue where supported.

No Admin/runtime configuration.

## Desktop layout

Approved Desktop direction:

### Shell
- normal Jularr Desktop navigation remains visible;
- Games is selected;
- detail page uses the available wide content area.

### Hero
- large backdrop across the upper content area;
- cover anchored on the left;
- title and concise metadata beside it;
- description/tagline in hero;
- small genre chips;
- strong Play/Continue action row;
- latest-save/resume card aligned to the action area.

### Primary actions
Typical order:
- `Continue`;
- `Play from Beginning` when resume exists;
- Favorite or More depending on available width.

### Content below hero
Use a wide, scan-friendly layout:
- Overview card;
- Versions cards/row;
- Save States cards/row;
- Screenshots gallery.

Sections can share a row on wide screens when this improves density.

Do not use a permanent right-side technical sidebar.

### Tabs
A compact tab row may be used:
- Overview;
- Versions;
- Save States.

However, the approved visual direction also permits the first-page sections to remain visible below the hero on wide Desktop.

Implementation should not duplicate the same content both as full sections and again inside tabs.

Choose one coherent rendering at implementation time while preserving the approved information hierarchy.

## Mobile layout

Approved Mobile direction is a dedicated vertical composition, not a squeezed Desktop page.

### Top
- back;
- Jularr / Games context;
- compact More action.

### Hero
- backdrop across top;
- cover overlaps/anchors into hero;
- title;
- short tagline;
- compact platform/year/developer/publisher line;
- concise description.

### Primary actions
Full-width or near-full-width:
- `Continue` / `Play`;
- Favorite;
- More.

Latest-save card sits directly beneath primary actions.

### Navigation
Compact horizontal tabs:
- Overview;
- Versions;
- Save States.

Do not show empty tabs.

### Content
Stack vertically:
- About This Game;
- compact metadata grid;
- Versions;
- Save States;
- Screenshots.

Rows/cards are touch-sized and use overflow menus rather than tiny inline controls.

### Mobile constraints
- no Desktop sidebar;
- no dense metadata table;
- no raw file/runtime data;
- Play/Continue remains visually obvious without excessive sticky chrome;
- safe areas/notches are respected.

## TV layout

TV is a first-class Game Detail surface, not a scaled web page.

### Navigation
- compact/icon-focused TV rail;
- Games selected;
- strong focus states.

### Hero
- very large backdrop;
- large cover;
- very large title;
- only essential metadata;
- short description;
- large focusable action buttons.

Primary focus order:
1. `Continue` / `Play`;
2. `Play from Beginning` where applicable;
3. `More`;
4. latest-save card.

### Lower content
Remote/gamepad-friendly horizontal rows:
- Overview summary;
- Versions;
- Save States;
- Screenshots.

Cards use large targets and strong focused borders/glow.

No tiny ellipsis-only interaction may be required for the primary path.

### TV controller boundary
Controller assignment/pairing is not permanently shown on Game Detail.

It appears only at launch/player boundary when needed.

Game Detail may show a small compatibility hint such as playable/runtime unavailable, but no permanent controller dashboard.

## Desktop / Mobile / TV parity

All clients must resolve the same:
- canonical Game;
- available GameReleases;
- PlayCapability;
- RecentPlay;
- SaveSummary;
- screenshots.

Clients may choose different visual composition but cannot invent conflicting availability or save state.

## Runtime abstraction

#771 makes EmulatorJS the first browser runtime.

Game Detail remains runtime-neutral.

Conceptual flow:

```text
GET Game Detail
 -> GameDetail read model
 -> available GameReleases
 -> runtime capability service

Play / Continue
 -> ResolveLaunch(GameId, optional GameReleaseId, optional SaveStateId)
 -> LaunchPlan
 -> Game Player
```

The page receives only consumer-facing capability:

```text
PlayNow
NeedsChoice
Unavailable(reason)
```

It never receives or constructs:
- EmulatorJS CDN/core IDs;
- ROM/disc URLs;
- BIOS file paths;
- native process commands.

Unsupported systems remain valid Games with a normal Detail page; they simply have no compatible LaunchPlan until a runtime supports them.

## Read model

Conceptually:

```text
GameDetail
- GameId
- Title
- AlternateTitle?
- Tagline?
- Description
- Cover
- Backdrop
- Developer
- Publisher
- ReleaseDate/Year
- Genres[]
- Platforms[]
- PreferredRelease
- Releases[]
- PlayCapability
- RecentPlay
- SaveSummary
- SaveStates[]
- Screenshots[]

GameReleaseSummary
- GameReleaseId
- Platform
- Region
- Revision
- VideoStandard?
- LanguageSummary
- DiscSummary?
- Availability
- PlayCapability
- Preferred

RecentPlay
- LastPlayedAt
- LatestSaveStateId?
- SaveThumbnail?
- DisplayLabel?
- OptionalLocation?

SaveStateSummary
- SaveStateId
- Thumbnail
- DisplayName
- UpdatedAt
- OptionalPlayTime
- OptionalLocation
- OptionalLevel

PlayCapability
- State: PlayNow | NeedsChoice | Unavailable
- Reason?
```

Do not bind Razor/native clients directly to EmulatorJS objects.

## Required states

Every platform defines:
- loading;
- metadata partial;
- local and playable;
- local with resume state;
- local and needs launch choice;
- local but unsupported runtime;
- missing BIOS;
- browser/device unsupported;
- requested/server-acquiring;
- current-device Offline downloading/installing/verifying when supported;
- current-device Offline ready/update available/failed when supported;
- external launcher Offline capability where integrated;
- not local/requestable;
- no Save States;
- screenshots empty;
- save unavailable;
- error/offline;
- permission denied.

Unavailable sections disappear instead of rendering empty decorative panels where possible.

## Themes

The information architecture is theme-independent.

### Clean
- dark/navy surfaces;
- purple accent;
- restrained glow;
- existing Clean Jularr card/detail language.

### Jularr Original
- warm light/cream surfaces;
- Japanese watercolor/cherry-blossom treatment;
- red accent;
- same data/actions/layout hierarchy.

Do not create separate feature implementations per theme.

## Visual baseline

The approved mockups cover:
- Desktop Game Detail;
- Mobile Game Detail;
- TV Game Detail.

The owner will upload them into this folder.

Mockups define visual direction; this text spec defines behavior/data/boundaries and wins on conflict.

## Must not implement

- no emulator settings on consumer detail;
- no BIOS upload on consumer detail;
- no raw file/path display as primary UX;
- no EmulatorJS-specific UI dependency;
- no second Request/Search flow;
- no duplicate canonical Game per region/revision;
- no forced release chooser when one obvious playable target exists;
- no empty tabs;
- no permanent controller setup on Detail;
- no achievement/mod/DLC scope in V1;
- no MediaCore Work/MediaProgress identity for Games just to share Offline UI;
- no second Games-specific consumer downloads manager;
- no silent deletion of profile saves when a managed local Game package is updated or removed.
