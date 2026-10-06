# Games — Shared UX Contract

Status: shared Games UX contract. Non-player consumer/Admin UX is approved; common Game Boy and Nintendo DS player direction is approved; further platform-player work is intentionally paused.

Global UX rules: `docs/UX.md`.
Architecture planning: #725.
Acquisition/import foundations: #389, #697, #715.

## Purpose

Games is a consumer-facing library/play area backed by a strongly isolated Games module.

Shared Jularr infrastructure may search, acquire, download and hand off a completed download. Game-specific identity, releases, platforms, import, runtimes, BIOS/firmware, saves and play behavior remain inside Games.

## Consumer navigation

Games is a deliberate exception to the normal shared Library navigation.

Games has a dedicated consumer destination and is not a type tab inside the normal Library.

Platform navigation:
- Desktop/wide Tablet: Games may be a permanent primary/sidebar destination alongside Home and Library.
- TV: Games may be a primary destination when available.
- Mobile: Games is **not** a permanent bottom-navigation item. The fixed bottom bar is `Home · Library · Calendar · Learning · Profile`; Learning joins it once it is no longer Unfinished (#870).

Mobile enters the dedicated Games destination contextually through Home Games/Continue Playing surfaces, global Search/Discover with Games context, Game Detail/back-navigation and deep links. Home must keep Games reachable even when there is no recent play activity, without replacing the Learning bottom-nav slot.

This is a UX/navigation boundary only. It does **not** create a second search, request, acquisition, downloader, storage or activity architecture.

Planned game-specific surfaces:
- `games-library/SPEC.md`
- `game-detail/SPEC.md`
- `game-player/SPEC.md`
- `game-player-nintendo-ds/SPEC.md`
- `game-player-playstation/SPEC.md`
- `game-touch-controls/SPEC.md`
- `game-play-options/SPEC.md`

Games stays integrated into the rest of Jularr:
- Home may show `Continue Playing` and game recommendations/recently relevant items;
- global Search / Discover includes Games and can open the Games detail/request flow;
- shared Request flow is reused;
- shared Activity/Operations status is reused;
- Profile/history may include game play activity where useful.

Do not create a second Games-only search/request/acquisition stack.

The dedicated Games destination exists because Games needs platform browsing, saves, runtime/playability and controller-aware presentation that would overload the normal media Library.

## Games visual-system rule

Games uses Jularr's existing themes and component language.

For player surfaces specifically:
- do not imitate original console hardware as page chrome;
- do not wrap emulator screens in fake device/console frames;
- keep platform-specific UI modern and functional;
- use the Clean light Jularr skin or Original Jularr skin over the same information architecture.

Platform differentiation comes from behavior, controls, metadata and screen topology, not decorative hardware cosplay.

## Admin navigation

Planned game-specific Admin surfaces:
- `admin-games/SPEC.md`
- `admin-games-runtimes/SPEC.md`
- `admin-games-runtime-editor/SPEC.md`
- `admin-games-bios/SPEC.md`
- `admin-games-bios-editor/SPEC.md`
- `admin-games-import-resolution/SPEC.md`

Existing shared Admin areas remain authoritative for:
- Games LibraryRoot -> Storage
- game metadata providers -> Providers
- acquisition/indexers/downloader -> Acquisition / Downloader
- cross-system failures -> Activity / To-Do / History

Game-specific surfaces may deep-link to these areas.

## Core identity

Consumer UX may expose:
- Game
- Platform
- GameRelease
- region/revision/language where useful
- local availability
- runtime/play availability
- per-profile save state

Do not expose provider-native IDs as product identity.

## TV and controller direction

Games should feel first-class on TV rather than like an Admin-only ROM catalogue.

The long-term interaction model is:
- open Games on TV;
- choose a playable game;
- use one or more connected gamepads where the selected runtime supports them;
- optionally pair phones as temporary controllers through a Jularr-coordinated session;
- assign paired inputs to player slots;
- start/continue the game.

Phone-controller pairing is session-scoped. A paired phone must not gain general Jularr/server access.

This capability is planned architecture, not a V1 implementation blocker. Actual controller count and platform support remain runtime/capability-driven.

## Play contract

```text
Game detail
 -> choose playable release/runtime only when needed
 -> Games creates LaunchPlan
 -> Browser/approved runtime receives only required game assets
 -> current profile save scope
```

No emulator core is implemented by Jularr.

## Isolation

Browser runtimes receive controlled asset/save access only.

Server/local runtimes, if supported later, receive only explicitly required:
- selected game files
- required BIOS/firmware
- current profile save directory
- explicitly granted device/host capability

They get no direct PostgreSQL, Jularr configuration/secrets or unrelated library access.

## Shared states

Every relevant surface must account for:
- available/playable
- available but no compatible runtime
- requested/downloading/preparing
- metadata incomplete
- missing BIOS/firmware
- runtime unavailable/degraded
- save unavailable/conflict only if the save layer reports one
- permission denied
- loading/error/offline

## V1 exclusions

Do not make V1 depend on:
- achievements
- mod managers
- DLC management
- cheat databases
- netplay
- server-side gameplay streaming
- automatic emulator installation for every OS
- large controller-profile engines
- phone-as-controller / multi-controller TV pairing as a V1 requirement
- modern storefront integrations

## Planning / approval rule

Each page/dialog is reviewed separately. Once a surface is approved, its own SPEC is authoritative for that surface.

Current phase:
- Games Library, Game Detail and conditional Play Options are approved;
- Home and Discover reuse their existing shared pages with Games integrated into those specs;
- Admin Runtimes, Runtime Editor, BIOS/Firmware, BIOS Add/Replace and Import Resolution are approved;
- Storage, Providers and Setup Wizard remain shared owners for their concerns and have been aligned with Games;
- common Game Boy player/touch-control direction and Nintendo DS extension are approved;
- additional platform-player work, including PlayStation 1 visual approval, is intentionally paused until resumed explicitly.

Do not invent extra Games-specific Home, Discover, Request, Provider, Storage, Downloader or Activity pages.
