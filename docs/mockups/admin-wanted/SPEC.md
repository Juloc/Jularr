# Admin Wanted — V1

Status: approved planning direction; mockup required before implementation.

Global UX rules: `docs/UX.md`.

## Purpose

Wanted is the Admin acquisition worklist for content Jularr still needs.

The list answers:
- What is missing?
- What is currently being searched for?
- What was requested/approved for acquisition?
- What failed?
- Which profile, language and version target applies?
- What should the admin do next?

Wanted is technical acquisition state, not user request moderation.

## Worklist

Primary states:
- All
- Requested
- Missing
- Searching
- Failed

`Requested` means an approved acquisition need that has entered the acquisition pipeline. User approval/moderation belongs to Admin Requests.

Desktop uses a compact table. Mobile uses stacked cards.

Recommended worklist fields:
- Work / structure unit
- Media type
- desired language
- acquisition / quality profile
- desired version or edition where applicable
- status
- last search result
- last search age
- actions

Selecting a Wanted item opens the reusable **Acquisition dialog**. Manual Search is not a second independent workflow/page.

## Acquisition dialog

The dialog is the main interaction surface for one Wanted target and uses exactly three primary tabs:

1. **Search** — default and primary tab
2. **Current** — current target and existing-data details
3. **History** — previous searches, grabs, failures and import outcomes

Desktop: large centered dialog or side-expanded modal with enough width for a dense results table.

Mobile/tablet: full-screen sheet/page presentation using the same tabs and data model.

TV: unsupported.

### Search tab

Search is the main tab.

Header context:
- target Work + Structure/unit
- effective acquisition profile
- target language(s)
- current monitored state
- temporary profile/language selectors for this search
- Refresh/Search action

Changing the temporary profile/language context re-evaluates candidate scoring for the dialog but does not silently change the persisted media configuration.

Results show normalized external ReleaseCandidates before import.

The default main score is always the score for the **currently effective profile + language target**.

Optional additional profile score columns can be enabled through the table column chooser. They are comparison data only.

Candidate columns are configurable. The default set should include:
- decision / warning state
- score
- release title
- indexer/source
- age
- size
- parsed quality
- languages
- audio
- subtitles
- release group
- release type: single / multi-episode / season pack
- parsed episode/volume/unit identity
- match confidence
- grab action

Additional provider/indexer metadata may be exposed as optional columns where available, but the UI must not depend on provider-specific schemas.

Filters:
- accepted / warning / rejected
- score range
- quality
- language
- audio
- subtitles
- size
- age
- source/indexer
- release group
- single / multi / season pack
- match confidence
- rejection reason
- profile score where useful

Default ordering follows the canonical automatic-selection hierarchy: decision eligibility/identity state first, then effective quality/fallback tier, preference score, coverage utility and configured source/tiebreak policy. Network response order is never significant.

### Candidate decision visibility

Do not hide releases only because Jularr thinks they are wrong or unsuitable.

Every returned candidate that can be normalized enough to display should remain visible and carry a clear decision state:
- Eligible
- Warning / manual review
- Rejected

Examples:
- likely wrong episode/unit
- unknown or ambiguous episode mapping
- wrong season
- profile score below minimum
- language mismatch
- quality below/above configured limits
- size outside profile limits
- blocked release/group
- already present / existing file preferred
- prior failed or blocklisted release

The row shows a compact warning/rejection indicator. Opening the row/detail drawer shows the exact reasons and score breakdown.

A candidate that appears to be the wrong episode/unit must therefore still be visible in Manual Search, clearly marked with Jularr's parsed identity and the requested identity side by side.

Automatic acquisition must never select a rejected identity mismatch.

Manual override may be offered only when policy allows it. For an identity mismatch, override requires an explicit confirmation and explicit target mapping; it must not silently teach the parser or rewrite canonical IDs.

Hard failures that cannot be safely grabbed remain non-overridable.

### Score presentation

The score is an evaluation result, not a property of the release itself.

The visible main score is calculated against:
- selected acquisition profile
- target language policy
- quality preferences
- custom format / release preferences
- source/indexer policy
- release-type preference such as season-pack preference
- size/age rules where configured

Candidate detail shows the score breakdown by rule.

Optional comparison mode may show the same candidate scored against other configured profiles without changing the active target.

A Season Pack indicator is explicit and filterable; pack preference contributes to score only through profile rules.

### Current tab

Shows the current acquisition target and existing state:
- canonical Work / Structure identity
- requested unit(s)
- monitored state
- desired languages
- effective profile
- desired edition/version where applicable
- current local asset/file if one exists
- current quality/languages/audio/subtitles
- current profile score where applicable
- missing/incomplete reason
- source of the need: monitoring, approved request, upgrade, repair, manual admin action

This tab is factual context, not another metadata editor.

### History tab

Shows events scoped to this acquisition target:
- searches
- automatic decisions
- manual grabs
- rejected/overridden candidates
- download client handoff
- import result
- failure/blocklist result
- actor
- timestamp
- effective profile/language at decision time

History entries open their decision details where useful.

## Actions

Worklist:
- Automatic search
- Open Acquisition dialog on Search tab
- Open media detail
- Pause/unmonitor where applicable

Dialog:
- Search/refresh
- change temporary profile/language context
- filter/sort/configure columns
- inspect candidate
- grab eligible candidate
- explicitly override eligible warnings when authorized
- open provider/indexer health when a source failed

Bulk actions are allowed only for compatible Wanted targets.

## Light / Dark

Both are first-class Admin surfaces.

Use restrained Fluent-2-like styling:
- light/dark surfaces
- compact borders
- subtle tinted warning/status states
- no large decorative hero artwork
- no full-color chip wall

## Loading / Empty / Error / Partial states

Required:
- worklist loading
- worklist empty
- Search in progress
- no candidates found
- accepted/rejected mixed results
- all candidates rejected
- partial indexer failure
- indexer unavailable/rate limited
- ambiguous target identity
- grab queued
- grab failed
- history empty
- current data partial/unavailable

Partial provider failure must not discard valid results from providers that succeeded.

## Domain / architecture constraints

- Wanted references canonical Work -> Structure -> Edition -> Version -> Asset/File -> Track concepts.
- ReleaseCandidate is external/temporary acquisition data, not a parallel persisted media model.
- Provider-native identifiers/fields remain provenance/evidence, not canonical domain identity.
- Identity validation happens before quality/profile score can make a candidate eligible.
- Manual override must not bypass canonical target selection or create hidden duplicate structures.

## Must not implement

- No separate Anime/Series/Movie acquisition core.
- No independent Manual Search page with different logic.
- No hiding rejected candidates without a user filter.
- No single opaque score without a breakdown.
- No score treated as globally intrinsic to a release.
- No automatic grab of a candidate Jularr considers the wrong canonical unit.
- No silent parser correction from a manual override.
- No arbitrary provider-specific columns permanently hard-coded into the default table.
- No consumer exposure of this Admin workflow.


## 2026-10-06 automatic search/selection refinement

Binding generic behavior:
- `docs/ACQUISITION_SEARCH_PLANNER.md` / #864
- `docs/AUTOMATIC_RELEASE_SELECTION.md` / #865

Wanted remains the worklist. It does not own another query/scoring engine.

### Automatic search state

Wanted may surface compact operational context such as:
- effective search depth (normally Normal);
- next allowed search/backoff time;
- active fallback tier / next fallback transition;
- partial-indexer failure without hiding successful candidates;
- whether the target is waiting because enough evidence has not yet appeared;
- whether current local content is Temporary and still upgrade-wanted.

Do not expose raw provider query internals in the main list; deeper details belong to Manual Search/Search Trace.

### Multi-target coverage

A single pack/multi-unit release may satisfy several Wanted targets through one canonical acquisition plan.

Wanted must avoid showing those covered units as independent simultaneous downloads once the shared candidate is grabbed. Only confirmed covered targets transition to Grabbed/Downloading.

### Temporary vs final

A successfully imported fallback release may leave the target in a clear `Available, upgrade still wanted`/equivalent Admin state until the profile's final quality/score/fallback goal is reached.

Successful download/import alone does not mean the final acquisition goal is satisfied.

### Automatic decision hierarchy

Wanted/Auto uses the canonical hierarchy from `AUTOMATIC_RELEASE_SELECTION.md`; identity/safety/Require/Reject gates precede score, pack utility uses actual coverage, and upgrade selection requires configured material benefit.
