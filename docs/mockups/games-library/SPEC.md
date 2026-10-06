# Games — Library/Home

Status: approved product/UX direction; approved visual reference will be uploaded by the owner into this folder.

Shared contract: `docs/mockups/games/SPEC.md`.
Games architecture: #725.
UX planning: #729.
Initial browser runtime: #771.

## Purpose

`/Games` is Jularr's dedicated consumer Games destination.

Games is intentionally not a type tab inside the normal media Library. It still uses the shared Jularr shell, Search/Discover, Request, profile, Activity, Acquisition, Downloader and Storage infrastructure.

## Desktop composition

Order:

1. normal Jularr app shell with Games selected;
2. compact Games header;
3. local library search + platform filter + sort + view controls;
4. `Continue Playing`, only when the current profile has resumable game activity;
5. `Deine Spiele` library grid/list.

Do not add a statistics/sidebar dashboard.

## Header

Show:
- `Games`;
- short secondary line where useful;
- local search: `Spiele durchsuchen…`;
- platform filter, default `Alle Plattformen`;
- sort;
- grid/list toggle only if list view remains useful after implementation.

No consumer Add, Downloader, BIOS or Runtime configuration button.

Finding/requesting new Games uses global Search/Discover + the shared Request flow.

## Continue Playing

Render only when at least one current-profile Game has resumable/recent play state.

Use wider landscape cards than the normal library grid.

Show:
- artwork;
- Game title;
- platform;
- concise last-played/save context;
- `Continue`.

Do not invent percentage completion for games.

`Continue` launches directly only when one valid LaunchPlan can be resolved without user choice. Otherwise it opens Play Options.

Home may independently surface a smaller shared `Continue Playing` row.

## Deine Spiele

Default view is cover/grid-first.

One card represents one canonical `Game`, not one ROM/file.

Show only glanceable information:
- cover/artwork;
- title;
- primary platform or compact platform summary;
- optional direct `Play`/`Continue` only when unambiguous;
- concise unavailable state only when actionable.

Do not place region, revision, runtime, BIOS, checksum or file-format badge walls on cards.

Multiple local releases are handled on Game Detail.

## Platform filter

Platform is the primary Games-specific filter.

Desktop/tablet:
- one compact selector;
- optionally a few recent/common quick choices if the final mockup benefits from them.

Do not create permanent tabs for every possible system.

## Sort

Initial useful choices:
- last played;
- title;
- recently added;
- platform.

Avoid speculative sort modes.

## Availability and acquisition

The Games collection is primarily locally available/imported Games.

A known Game may show a compact requested/downloading/preparing state, but this page is never a second Wanted/Downloader queue.

Missing/new Games are found through global Search/Discover.

## Navigation

- Game card -> Game Detail;
- direct Play/Continue -> LaunchPlan when unambiguous;
- ambiguous playable choice -> Play Options;
- Search new Game -> global Search/Discover with Games filter;
- acquisition/technical failures -> shared Activity/To-Do.

## Read model

The page should consume a dedicated Games read model rather than raw persistence/runtime DTOs.

Conceptually:

```text
GamesPage
- query/filter/sort
- continuePlaying[]
- games[]
- totalCount
- availablePlatforms[]

GameCard
- GameId
- Title
- Artwork
- PlatformSummary
- Availability
- LaunchCapability
- OptionalRecentPlay

ContinueGameCard
- GameId
- GameReleaseId
- Title
- Artwork
- Platform
- LastPlayedAt
- SaveAvailable
- LaunchCapability
```

`LaunchCapability` is a small application result such as:
- PlayNow
- NeedsChoice
- Unavailable(reason)

It must not expose EmulatorJS/native runtime implementation details to the page.

## Runtime relationship

#771 defines EmulatorJS as the first low-friction browser runtime.

The Games page does not instantiate EmulatorJS or construct core/file URLs.

Flow:

```text
Games page
 -> Play/Continue
 -> Games application service resolves GameRelease
 -> runtime capability resolver
 -> LaunchPlan
 -> Game Player
```

This keeps EmulatorJS replaceable and allows later runtimes without changing the library page.

## TV direction

Games is first-class on TV.

TV may render the same content model differently:
- Continue Playing first;
- large focusable cards;
- horizontal platform rows;
- remote/gamepad focus navigation.

Do not squeeze Desktop filters into the TV layout.

## Mobile

### Mobile navigation entry

The fixed Mobile bottom navigation remains:
`Home · Library · Calendar · Learning · Profile` (Learning joins it once it is no longer Unfinished, #870).

Games is a dedicated route, but not a permanent bottom-nav slot.

Entry can come from:
- Home Games / Continue Playing row or Games shortcut/row;
- global Search/Discover with Games context;
- Game Detail/back-navigation;
- deep link.

When Games is available and there is no recent Game activity, Home still exposes a lightweight Games entry so the local Games library is discoverable.

Use:
- compact header/search;
- platform/sort filter sheet;
- thumb-friendly grid;
- horizontal Continue Playing.

Phone-as-controller is a separate later game-session mode, not a Games-library interaction.

## Required states

- loading;
- empty library;
- no search/filter results;
- Continue Playing absent;
- local Game but no playable runtime;
- requested/downloading/preparing;
- metadata/artwork partial;
- error/offline;
- permission denied.

Empty library primary action:
- `Spiele suchen` -> global Search/Discover filtered to Games.

## Visual direction

Use the existing Jularr design system, not a new gaming theme.

Both existing skins use the same information architecture:
- Clean: dark/purple visual skin;
- Jularr Original: light Japanese watercolor/cherry-blossom skin with red accent.

The owner's approved Games mockup will be uploaded into this folder. Text spec wins on conflict.

## Must not implement

- no Games type tab inside normal Library;
- no separate Games Search/Request engine;
- no downloader queue;
- no BIOS/runtime Admin controls;
- no raw paths/files;
- no per-ROM duplicate cards;
- no fake game progress percentage;
- no controller pairing on the library page;
- no EmulatorJS-specific page logic.
