# Admin Manual Search / Acquisition Dialog — V1

Status: **approved UX direction**. Desktop and Mobile mockups are binding visual references.

Global UX rules: `docs/UX.md`  
Wanted contract: `docs/mockups/admin-wanted/SPEC.md`

Approved visual references:
- Desktop: `file_0000000014508246b464e74277c1feed.png`
- Mobile: `file_00000000b6108243b429b2ff75d3e44c.png`

If an image and this specification conflict, this specification wins.

## Purpose

Manual Search is the Admin surface for inspecting concrete external release candidates and manually selecting one for an existing canonical acquisition target.

It is the **Search tab of the reusable Acquisition dialog/sheet**, not an independent media model or acquisition pipeline.

Entry points include:
- Admin Wanted
- Admin Media Detail
- other authorized Admin acquisition actions

The screen must answer quickly:
- What exactly are we searching for?
- Which indexer/provider produced each candidate?
- How did Jularr parse the candidate?
- Does it match the requested episode/season/unit?
- How well does it satisfy the active profile + language policy?
- Why is it accepted, warned or rejected?
- What will be grabbed if the admin selects it?

## Primary structure

Exactly three primary tabs:
1. **Suche**
2. **Aktuell**
3. **Verlauf**

`Suche` opens by default when Manual Search is invoked.

### Suche
External normalized ReleaseCandidates, filters, scoring, selection and grab.

### Aktuell
Current canonical target, monitoring/profile/language requirements and existing local state.

### Verlauf
Search, decision, grab, handoff, import, failure and blocklist history scoped to the same acquisition target.

Do not create additional top-level tabs for score, files, indexers or diagnostics.

## Target context header

The header is compact and operational. No consumer-style hero.

Show:
- small artwork thumbnail
- Work title
- requested Structure/unit, e.g. `S01E03 · We Need a Hero`
- acquisition state, e.g. `Fehlend`
- effective acquisition profile
- target languages
- last search time where useful
- primary `Suche starten` / refresh action

The target identity must remain visible while reviewing results.

Temporary search overrides may change profile/language evaluation for this search, but must never silently mutate persisted media settings.

## Search toolbar

Desktop uses one compact toolbar above the table:
- text search within returned results
- Filter
- Sortierung
- Indexer
- Typ
- Qualität
- Spalten

Active filters appear directly below as removable compact tags.

Mobile uses:
- result search field
- Filter button
- horizontally wrapping/scrolling compact controls for Typ, Qualität, Sprache, Score and Sortierung

Filters may include:
- eligible / warning / rejected
- score range
- quality
- language
- audio
- subtitles
- size
- age
- source/indexer
- release group
- release type
- season pack / multi-unit
- parsed identity / match state
- rejection reason

Successful results from available indexers remain usable when another indexer fails.

## Desktop results

Desktop is a dense Admin table and uses most of the dialog width.

Default columns:
- Score
- Release
- Typ
- Indexer
- Alter
- Größe
- Qualität
- Sprache
- Audio / Subs
- Parsed
- Match
- Aktion

A column chooser may expose additional normalized metadata.

Do not permanently show every provider-native field.

### Release row

A row can use a secondary line for:
- normalized release title
- pack/episode coverage
- parsed identity
- concise diagnostic reason

The complete raw title remains available in candidate details.

### Selection

Selecting a row:
- gives it a restrained selected border/state
- opens or updates the right-side candidate detail drawer
- does not immediately grab it

The actual grab/download action remains explicit.

## Desktop candidate detail drawer

The selected release opens in a narrow right-side Admin drawer.

Show:
- release title
- source/indexer
- release type
- normalized parsed identity
- season/episode/unit coverage
- quality
- languages
- audio/subtitles
- size
- release group where available
- total effective score
- score breakdown
- exact match/warning/rejection reasons
- episode/unit coverage where relevant
- primary `Auswählen und laden` action

For a season pack, coverage such as `E01–E12` must be directly visible.

The drawer replaces permanent large explanatory cards below the table.

## Mobile results

Mobile is a full-screen Admin acquisition surface, not a tiny desktop modal.

Each candidate is a dense stacked Admin card containing:
- selection control
- score
- release title
- release type
- indexer
- age
- size
- quality
- language/audio/subtitle metadata
- parsed identity
- match/warning/rejection reason
- expand/detail affordance

The selected candidate receives only a restrained outline/selection state.

A sticky bottom action area summarizes the selected candidate and exposes `Auswählen und laden`.

No large decorative artwork or consumer-style media cards.

## Tag / chip visual contract

This is binding.

All compact metadata/status tags use the same clean visual grammar:

- **no visible filled background**
- transparent or same background as the parent surface
- thin rounded border
- compact radius/pill shape
- small **outline/line icon**
- short text label
- restrained color only on border, icon and/or text when semantic color is needed

Examples:
- outline stack icon + `Season Pack`
- outline monitor icon + `1080p`
- outline globe icon + `JA`
- outline audio icon + `AAC`
- outline check icon + `Passend`
- outline warning icon + `Niedrigere Qualität`
- outline X/error icon + `Falsche Episode`

### Icons

Icons inside tags are also line/outline icons.

Do **not** put icons inside solid colored circles/disks.

Do not use:
- solid green circles behind checkmarks
- solid red circles behind X icons
- filled pastel pills
- large green/red status boxes
- saturated badge backgrounds

Semantic state is expressed through subtle border/icon/text color plus the written label.

Primary CTA buttons may remain filled with the Jularr accent; this rule applies to metadata/status tags, not primary actions.

## Score

The visible score is contextual to the current **profile + language target**.

It is not an intrinsic property of a release.

The score may include:
- title/identity match
- season/episode/unit match
- quality preference
- language policy
- audio/subtitle requirements
- custom format/release preferences
- source/indexer preference
- season-pack preference
- size/age rules
- other profile-owned rules

The selected candidate drawer exposes a score breakdown.

Optional comparison against other configured profiles may exist later, but the active profile score remains primary.

## Season packs and multi-unit releases

Season packs are first-class candidate types.

Clearly show:
- release type: `Season Pack`
- parsed season
- detected episode count, e.g. `12/12`
- coverage, e.g. `E01–E12`
- total size
- target episode inclusion
- active pack preference contribution to score where applicable

Example:
`Season Pack · S01 · 12/12 · E01–E12 · enthält Ziel E03`

A complete pack may score above a single episode if the active profile prefers packs.

A partial or wrong-season pack remains visible and receives a warning/rejection reason.

## Rejected and suspicious candidates

Manual Search is also diagnostic.

Returned candidates that can be normalized enough to display remain visible even when Jularr would not auto-grab them.

Examples:
- wrong episode
- wrong season
- ambiguous parsed identity
- language mismatch
- below required quality
- outside size rules
- blocked release/group
- previously failed
- local file already preferred

For identity mismatch show both:
- requested canonical target
- parsed candidate target

Examples:
- requested `S01E03`, parsed `S01E04` → `Falsche Episode`
- requested Season 1, parsed Season 2 → `Falsche Staffel`

Identity-invalid candidates can never be selected automatically.

A manual override, if policy allows one later, requires explicit confirmation and explicit target mapping. It must not silently retrain parsing or change canonical IDs.

Hard safety/integrity failures remain non-overridable.

## Current tab

Show factual target/current-state information:
- canonical Work / Structure/unit
- monitoring state
- active profile
- target languages
- desired Edition/Version where applicable
- current local Asset/File where present
- current quality
- current audio/subtitles/languages
- why this target is missing/wanted/upgrading

This tab is context, not a second metadata editor.

## History tab

Show target-scoped events:
- searches
- automatic decisions
- manual selections
- authorized overrides
- download-client handoff
- import outcome
- failures
- blocklist events
- actor
- timestamp
- profile/language context used for the decision

## Light / Dark

Both themes are required.

The approved references currently define the light Admin composition.

Dark mode must preserve:
- hierarchy
- table/card density
- transparent/outline tag grammar
- semantic border/icon/text colors
- readable selected/focus states

Do not convert outline tags into filled badges in Dark mode.

## Platform behavior

### Desktop
- large centered dialog
- compact target header
- dense configurable table
- right-side candidate detail drawer
- keyboard/mouse friendly
- no information dependent solely on hover

### Tablet
- large sheet/dialog
- reduced default columns or split list/detail
- touch-sized controls
- same candidate and scoring model

### Mobile
- full-screen Admin surface
- dense candidate cards
- filter controls adapted for touch
- expandable details
- sticky selected-candidate action area
- minimum touch targets follow global UX rules

### TV
Unsupported for Admin Manual Search.

## Loading / Empty / Error / Partial states

Required:
- search loading
- no candidates
- candidates found
- mixed eligible/warning/rejected
- all candidates rejected
- partial indexer failure
- indexer timeout
- rate limit
- malformed candidate
- ambiguous parsed identity
- selection changed
- grab queued
- grab failed
- forbidden/insufficient permission

Partial provider failure must not discard valid candidates from providers that succeeded.

## Domain / architecture constraints

- The search target references canonical `Work -> Structure -> Edition -> Version -> Asset/File -> Track`.
- ReleaseCandidate is temporary external acquisition evidence.
- Provider/indexer-native fields are provenance/diagnostics, not canonical identity.
- A candidate does not become a Version/Asset/File merely because it appeared in search.
- Canonical identity validation happens before profile quality can make a candidate eligible.
- Scoring evaluates suitability; it must not create or redefine media identity.
- Search, automatic acquisition and manual acquisition must use the same normalized candidate/scoring pipeline.

## Actions

Allowed actions include:
- start/refresh search
- change filters/sort/columns
- inspect candidate
- select candidate
- explicit grab/download when authorized
- inspect source/indexer health on provider failure
- return to Wanted/Media Detail context

Selection and download are distinct actions.

## Must not implement

- No separate Anime/Series/Movie manual-search cores.
- No standalone duplicate acquisition pipeline.
- No hidden rejected results by default.
- No automatic grab of an identity-mismatched candidate.
- No quality/profile score overriding invalid identity.
- No opaque score without inspectable reasoning.
- No global intrinsic release score.
- No silent persistent profile/language changes from temporary search context.
- No provider-native candidate persisted directly as canonical Version before acquisition/import.
- No silent parser learning from manual force-grab.
- No arbitrary provider-specific default column wall.
- No consumer-facing release table.
- No filled-background metadata/status tags.
- No solid-circle tag icons.
- No large success/error color blocks.
- No desktop-only hover dependency.


## 2026-10-06 Search Planner and automatic-selection diagnostics

Binding generic behavior:
- `docs/ACQUISITION_SEARCH_PLANNER.md` / #864
- `docs/AUTOMATIC_RELEASE_SELECTION.md` / #865

Manual Search remains a view/controller over the same Auto pipeline, not a second search/scoring implementation.

### Search depth

Manual Search may offer:
- Fast
- Normal
- Deep

Normal matches normal automatic query planning. Deep explicitly expands aliases/fallback queries/pagination within bounded provider budgets. Changing depth is temporary to this search and never mutates the Work/profile.

### Search provenance

Candidate details may show:
- query stage that found it;
- structured provider ID vs title/alias fallback;
- numbering form used;
- indexer/category/search mode;
- equivalent source/indexers merged into the logical candidate.

A compact example is `Found via TVDB + S01E03 · Indexer A`.

A deeper Search Trace may show raw-vs-deduplicated counts, partial provider failures, backoff/rate-limit reasons and query-stage progression, with credentials and secret-bearing URLs always redacted.

### Deduplicated candidates

Equivalent releases from several indexers should normally appear as one logical candidate with multiple source options rather than duplicate rows.

Candidate details show the available sources and the source Jularr would prefer/grab. Conservative dedupe must never hide materially different releases.

### Automatic-selection diagnostics

Candidate state now also exposes where applicable:
- identity confidence: Exact / Strong / Ambiguous / Conflict;
- Require/Reject gate result;
- active fallback tier;
- Temporary vs Final acceptance;
- canonical coverage utility for pack/multi-unit releases;
- upgrade-benefit decision;
- bounded reliability/source tiebreak contribution.

### Why not automatic?

Every normalized candidate should be able to explain why Auto would or would not choose it.

Where useful, show a diagnostic `What would need to change?` explanation, for example:
- minimum score threshold;
- permanent language requirement;
- fallback tier not active yet;
- identity ambiguity that scoring cannot fix.

This explanation never mutates the profile.

### Winner comparison

When several candidates are eligible, details may explain why candidate A ranks above B using the canonical hierarchy rather than only showing a total score.

Network response order must never determine the winner.
