# Admin Media Detail — V1

Status: **binding V1 planning specification; current Desktop/Mobile visual references are the implementation baseline.**

Global UX rules: `docs/UX.md`

Text specification wins over images on conflict.

The V1 goal is deliberately simple: one clear hierarchy, direct monitoring/search actions at the level the admin is looking at, and expandable local-file details. Do not turn this screen into a collection of unrelated Sonarr-style tabs.

## 1. Core hierarchy

The UI follows the media structure rather than splitting the same information across many pages.

For Anime / Series:

```text
Medium
  Season
    Episode
      File / Version
      File / Version
```

For Movie / Book / Light Novel / Manga / Audiobook in V1, monitoring/search defaults to the whole medium. Media-specific finer-grained monitoring can be added later only when it provides real value.

The underlying data remains canonical and provider-independent. External providers may change how episodes are grouped or numbered for display, but they never create a second stored episode universe.

## 2. Header

The header identifies the medium and contains the medium-level operational controls.

Show:
- artwork/poster where useful
- title
- concise type/year/status metadata
- monitoring state
- active acquisition state when relevant
- current default acquisition profile
- desired/default language policy in compact form
- **total local storage used by this medium** in a prominent compact summary
- compact availability/coverage summary appropriate to the media type

Primary monitoring control:
- Anime/Series: monitor/unmonitor the whole series
- Movie/Book/LN/Manga/Audiobook: monitor/unmonitor the medium

Monitoring in the header is a real operational control, not a passive badge.

## 3. Anime / Series display mode

Anime may be displayed in different provider-derived groupings.

V1 selector:

`View: Standard | AniList`

### Standard
Classic series grouping such as:
- Season 1
- Season 2
- Specials

This is the normal season/episode view using canonical series semantics and provider mappings.

### AniList
Display/group episodes according to AniList anime entries/relations where useful for Anime.

Important rules:
- this is display/grouping only;
- switching view never moves files;
- switching view never creates duplicate episodes;
- monitoring remains attached to the same canonical episode;
- acquisition/import/progress state remains unchanged;
- only grouping, numbering and provider-facing labels may change.

Provider structures are mappings/views over the canonical structure, not persistence models.

V1 does not require additional display modes such as Absolute numbering. They may be added later.

## 4. Monitoring hierarchy

### Medium level
The whole work can be monitored or unmonitored.

### Season level — Anime/Series only
Each season is independently monitorable.

Season monitoring states:
- On
- Off
- Partial

`Partial` means only some child episodes are monitored.

### Episode level — Anime/Series only
Each episode has a one-click monitor toggle.

### Inheritance
Season/episode acquisition settings normally inherit from the parent medium.

The UI must make inheritance understandable instead of copying independent settings everywhere.

Examples:
- `Inherited: Anime 1080p`
- `Inherited languages: DE + JA`
- explicit override only when the admin deliberately changes it

An override is stored only when required. Removing an override returns the unit to inherited settings.

## 5. Season rows

Anime/Series seasons are expandable.

Collapsed season row should show only useful merged information:
- season name/number
- monitoring state: On / Off / Partial
- available episodes, e.g. `12/13`
- missing episodes
- compact desired/available language coverage where useful
- active search/download/import state
- effective profile
- actions

Season actions:
- monitor/unmonitor
- Automatic search
- Automatic…
- Manual search
- Delete where semantically valid

Searching from a season applies to the canonical child episodes represented by that season/view.

Default automatic season search targets monitored items that are missing or eligible for an upgrade. It must not blindly reacquire every episode.

## 6. Episode rows

Expanding a season shows episodes.

Each episode row should provide a merged operational summary:
- episode number
- title
- air/release date
- monitor toggle
- overall state
- effective quality
- available audio languages
- available subtitle languages
- number of local files/versions
- active acquisition/import state
- actions

Overall states may include:
- Available
- Missing
- Searching
- Downloading
- Importing
- Failed

Do not flood the row with many decorative tags. Language/quality information must remain compact and readable.

### Configurable columns
Desktop data rows support a **Columns** control. The admin can choose which useful columns are visible without changing the underlying data model.

Candidate columns include:
- release/air date
- monitoring
- status
- quality
- audio languages
- subtitle languages
- file count
- total size
- release group/source
- profile/effective profile
- active acquisition state

The default column set must remain useful out of the box. Column customization is a presentation preference, not a per-row data mutation.

## 7. Expand episode → local files

An episode row can be expanded inline.

The expanded area shows each actual local file/version separately.

Per file show where available:
- filename
- size
- container
- quality/resolution
- video codec
- audio tracks/languages
- subtitle tracks/languages
- release group
- source/release
- path/location
- import/source information
- technical state if analysis failed

File actions may include:
- view technical details
- re-analyse
- rename/organize where supported
- re-match / fix assignment
- delete this file

Multiple files for one episode are first-class and must not be collapsed into one fake file.

## 8. Shared acquisition actions

The same acquisition grammar should be reused at Medium, Season and Episode level where the action makes sense.

### 1. Automatic search
One-click action.

Uses the effective inherited settings immediately:
- acquisition/quality profile
- desired languages
- monitoring rules

No configuration dialog.

### 2. Automatic…
Opens a small focused dialog before searching.

V1 options:
- profile
- desired language(s)
- scope when needed, e.g. missing only / upgrades allowed

Primary action: `Search`

These selections are temporary for this search by default.

If the UI allows persistence, it must be an explicit separate choice such as:
`Save as override`

Temporary search options must never silently rewrite the medium/season/episode defaults.

### 3. Manual search
Opens the full normalized release candidate view.

Manual search can show:
- source/indexer
- quality
- languages
- release group
- size
- score
- rejection reasons
- manual grab

The detailed Manual Search screen is specified separately.

## 9. Edit / incorrect assignment

`Edit` on a file/unit is not a raw database editor.

For an incorrectly assigned file, open a safe Re-match flow.

Possible target changes:
- another episode/chapter/unit in the same medium
- another Work
- another edition/version where applicable

The UI should explain the current assignment and the proposed new assignment before applying it.

Canonical IDs are not directly typed/edited as the normal UX.

## 10. Delete semantics

Delete must never be ambiguous.

Depending on context, distinguish explicitly between actions such as:
- Delete this local file
- Delete all local files for this episode
- Remove/unmonitor this season or medium
- Remove medium from Jularr

Do not use one generic trash icon for several destructive meanings.

All destructive filesystem/library actions require confirmation and must state what data/files will actually be removed.

## 11. Live operational state

Search/download/import state should update live where the application already supports live events.

Examples:
- `Searching`
- `Downloading 63%`
- `Importing`
- `Failed`

These states should replace or augment the normal availability state without requiring page reload.

Incoming live updates must not collapse an expanded season/episode or reset scroll position.

## 12. V1 media-type behavior

### Anime / Series
Full V1 hierarchy:
- Medium monitoring
- Season monitoring
- Episode monitoring
- expandable seasons
- merged episode summary
- expandable episode files
- acquisition actions on Medium / Season / Episode

Anime additionally supports the Standard/AniList display grouping.

### Movie
V1 uses medium-level monitoring/search.

Movies do **not** use Seasons/Episodes. The main content directly shows the known/local **version list** for the movie.

Examples:
- 4K HDR Remux
- 1080p Blu-ray
- 1080p WEB-DL
- 720p WEB-DL
- other configured versions/editions

Each version row can show:
- monitoring state
- availability
- quality/resolution
- source/container
- audio languages
- subtitle languages
- file count
- storage size
- active search/download/import state
- actions

A version expands inline to its real local file(s) and technical details, exactly like an expanded Episode shows its files.

The version list is visible directly in the main view; do not hide it behind a separate Files page.

Shared actions:
- monitoring
- Automatic search
- Automatic…
- Manual search
- Edit/Re-match
- explicit Delete

### Book / Light Novel
V1 uses medium-level monitoring/search.

The main content shows the medium's **editions/volumes and local files directly**.

For a normal Book:
- show editions such as original, translated, print/digital or other known editions;
- an edition expands to its actual EPUB/PDF/AZW3/etc. files;
- show language, format, source, size and availability compactly.

For a Light Novel:
- show volumes in the main hierarchy;
- a volume expands to its actual local edition/file(s);
- show language/translation provenance where useful;
- chapter-level monitoring is not required in V1.

The UI may summarize chapter ranges/counts as information, but V1 does not create an independent chapter monitoring model.

### Manga
V1 uses medium-level monitoring/search.

The main hierarchy shows **volumes directly**.

Each volume can show:
- monitoring state
- chapter range/count
- availability
- language(s)
- file count
- size
- source/format where useful

A volume expands inline to the real CBZ/CBR/PDF/etc. files and their details.

Chapter information can be shown inside the expanded volume, but chapter-level monitoring is not required for V1.

### Audiobook
V1 uses medium-level monitoring/search.

Show local audio version/files and language/narration information. Track-level monitoring is not required for V1.

These V1 limits prevent the first implementation from creating separate complex monitoring models for every media type.

## 13. Mobile / narrow layout

Mobile must preserve the same information architecture and actions as Desktop, but it is **not a shrunk desktop table**.

### Touch and sizing
- minimum touch target: 44×44 CSS px;
- primary buttons/toggles must be comfortably thumb-sized;
- episode/volume/version rows are taller than desktop rows;
- important text must not require zooming;
- no tiny icon-only clusters for primary actions;
- overflow actions may move into a clear More menu.

### Header on mobile
Keep the same core information, rearranged into large touch-friendly summary cards:
- media identity/poster/title
- monitoring/coverage
- missing count
- **total storage used**
- active profile
- languages

Long descriptions may collapse behind `More` rather than pushing operational content too far down.

### Navigation
The same detail sections remain available, but secondary tabs may collapse under `More` when width is limited.

### Hierarchy on mobile
- Season/Volume/Edition/Version rows become stacked touch-friendly cards/rows.
- Expanding still happens inline.
- Real files remain visible inside the expanded unit.
- Do not open a separate page just because the screen is narrow.
- Summary information can be combined into fewer fields than Desktop, as long as no operational state becomes ambiguous.

### Mobile row priority
Show first:
1. identity/number/title
2. monitoring state
3. availability/search state
4. quality/language summary
5. file count/size
6. expansion/action affordance

Less important columns are moved into the expanded details or More menu.

### Mobile actions
Automatic search, Automatic…, Manual, Re-match/Edit and Delete remain available.

Frequently used actions can use a sticky bottom action bar or large contextual buttons. Destructive actions stay visually separate and require confirmation.

### Responsive continuity
Switching between Desktop and Mobile must not change:
- canonical media identity
- monitoring state
- inherited profile/language settings
- selected provider grouping
- current acquisition state

Only layout and information density change.

## 14. Information density

The main screen should remain understandable without many separate tabs.

Prefer:
- hierarchy
- expandable rows
- compact merged state
- contextual dialogs/drawers for deeper details

Avoid:
- duplicate Files/Languages/Releases surfaces that repeat the same information
- badge/tag walls
- permanent technical metadata in the main row
- separate pages for simple per-file actions

Additional tabs should exist only when a workflow cannot be represented clearly in the hierarchy.

## 15. State requirements

The screen must account for:
- loading
- no local files yet
- fully available
- partially available
- missing
- searching
- downloading
- importing
- failed acquisition
- provider unavailable
- storage unavailable
- unauthorized action

## 16. V1 acceptance criteria

V1 is complete only when:

- the medium can be monitored/unmonitored from the header;
- Anime/Series seasons can be expanded and independently monitored;
- season monitoring can represent Partial;
- Anime/Series episodes can be monitored with one click;
- episode rows merge the useful availability/quality/language state;
- desktop rows support a configurable Columns presentation;
- total local storage for the medium is visible in the header/summary;
- episodes can expand to real individual local files/versions;
- every acquisition level exposes consistent Automatic / Automatic… / Manual actions where applicable;
- Automatic uses inherited defaults immediately;
- Automatic… can temporarily choose profile/languages without silently persisting them;
- incorrect assignments use a safe Re-match flow;
- destructive actions clearly state their scope;
- Anime supports Standard and AniList display grouping;
- switching Anime grouping changes only the view/mapping, never canonical stored episode identity;
- searches/actions from provider-derived groups resolve back to canonical episodes;
- active search/download/import states can update live without resetting the page;
- Movie/Book/LN/Manga/Audiobook stay medium-level for monitoring in V1 instead of inventing unnecessary child-monitoring models;
- Movie shows its version list directly in the main content and each version can expand to real files;
- Book shows editions/files directly;
- Light Novel and Manga show volumes directly with expandable real files;
- Mobile uses touch-sized controls and stacked responsive rows rather than a compressed desktop table;
- Mobile preserves the same actions and state while summarizing lower-priority columns into expanded details/More menus.

## Canonical Anime identity vs AniList display

The existing Standard/AniList display grouping is a **view switch only**. Anime is stored as one canonical Jularr Work with seasons/episodes; a Work may map to multiple AniList entries when AniList splits seasons, cours/parts or specials.

Admin may inspect and correct those mappings and switch between canonical and AniList-oriented presentation. Provider entries must deep-link to the matching canonical season/presentation target and must never create a second persisted episode/work tree. This is the same identity rule used by Discover/Search.

