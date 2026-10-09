# Admin Acquisition Profiles & Scoring — V1

Status: approved planning direction. Existing generic `QualityProfileStore`, `ReleaseScorer` and acquisition registrations on `dev` are the implementation starting point. Approved mockups uploaded to this folder are visual references; this text remains binding.

Global UX rules: `docs/UX.md`.
Manual Search: `docs/mockups/admin-manual-search/SPEC.md`.
Wanted: `docs/mockups/admin-wanted/SPEC.md`.
Providers: `docs/mockups/admin-providers/SPEC.md`.
Downloader: `docs/mockups/admin-downloader/SPEC.md`.
Storage: `docs/mockups/admin-storage/SPEC.md`.
Migration: `docs/mockups/admin-migration/SPEC.md`.

If an image and this specification conflict, this specification wins.

## Purpose

Admin → Acquisition owns the reusable decision policy that answers:

- which releases are acceptable;
- which qualities/formats are preferred;
- when an existing file should be upgraded;
- how languages and release attributes influence the result;
- which releases are explicitly rejected;
- whether Jularr should wait for a preferred release before grabbing;
- which sources/providers are allowed/preferred;
- which profile is the default for a media kind or explicitly assigned to a Work.

The goal is at least the practical decision power of Sonarr quality profiles + custom formats + release profiles + delay profiles, but presented as one coherent Jularr model rather than several disconnected concepts.

It does **not** own:

- indexer/provider credentials or provider health;
- native Usenet servers or downloader processing;
- Storage paths/mounts;
- Wanted items;
- Manual Search candidate presentation;
- post-download mapping/import assignment;
- canonical media identity.

## Product model

The main user-facing object is one **Acquisition Profile**.

An Acquisition Profile combines:

1. general identity/default assignment;
2. quality and upgrade policy;
3. language policy;
4. reusable Release Rules and their effect in this profile;
5. size/age limits where meaningful;
6. wait/delay/source policy;
7. usage/assignments;
8. live score/test preview.

The normal admin workflow is:

`Profile öffnen → Regeln/Qualität ändern → Testen → Speichern`

not:

`Custom Format separat anlegen → Quality Profile öffnen → Score zuweisen → Release Profile konfigurieren → Delay Profile konfigurieren`.

## Current implementation on dev

Existing canonical pieces that must be reused:

- `QualityProfileStore`
- `QualityProfileState`
- `QualityProfile`
- `ReleaseScorer`
- `ReleaseScoreRule`
- `ReleaseRuleField`
- `ReleaseRuleMatch`
- `ReleaseCandidate`
- `ReleaseScoreResult`
- `MediaAcquisitionRegistry`
- per-media default profile registrations
- media-kind defaults
- per-Work profile assignment

Current generic scoring already supports:

- allowed qualities;
- quality order;
- upgrades on/off;
- upgrade cutoff quality;
- minimum score;
- absolute minimum/maximum size;
- Must Contain;
- Must Not Contain;
- required regex;
- rejected regex;
- score rules over:
  - raw title
  - release group
  - source
  - resolution
  - video codec
  - bit depth
  - HDR format
  - audio codec
  - audio language
  - subtitle language
  - dual audio
  - multi audio
  - Proper
  - Repack

The current store is media-type-agnostic and already supports:
- a default profile per registered media kind;
- a Work-specific override.

Do not replace this with media-specific parallel stores.

## Required implementation evolution

The existing backend is a good base but is not yet sufficient for the approved target UX.

Required additions should extend the generic model, not create a second engine.

Needed target capabilities include:

- quality groups / equal-quality tiers;
- upgrade-until-score;
- normalized language policy;
- reusable shared Release Rule definitions;
- multiple conditions per rule;
- explicit AND/OR/NOT condition grouping;
- negated conditions;
- first-class `Reject` rule effect;
- source/indexer/provider matching/preference;
- release type / pack matching where parser data exists;
- per-quality or per-quality-group size policy;
- video-friendly size normalization such as MB/min or bitrate when supported;
- wait/delay policy;
- preferred acquisition/download path where multiple supported paths exist;
- rule/profile import/export;
- Sonarr migration into the Jularr model.

Exact persistence schema can evolve, but the semantics in this document are binding.

## Primary page structure

Desktop uses a profile list on the left and the selected profile editor on the right.

Primary profile tabs:

1. **Allgemein**
2. **Qualität & Upgrade**
3. **Release-Regeln**
4. **Wartezeit & Quellen**
5. **Verwendung & Test**

Language controls may live in Allgemein or Qualität/Release policy depending on final shared component design, but remain part of the same Acquisition Profile.

Do not create permanent top-level pages named:
- Custom Formats
- Release Profiles
- Delay Profiles

for the normal Jularr workflow.

## Profile list

The left panel shows reusable profiles.

Each row may show:

- name;
- media kind(s);
- primary language(s);
- target/cutoff quality summary;
- active/default indicator;
- assignment count;
- overflow actions.

Filters:

- search;
- media kind;
- language;
- status/default.

Actions:

- Neues Profil
- Duplizieren
- Importieren
- Exportieren
- Löschen when safe

### Delete safety

A profile cannot be deleted while it is:

- a media-kind default;
- assigned to one or more Works;
- referenced by another canonical acquisition policy.

The existing `QualityProfileStore.DeleteAsync` already blocks default/Work references; preserve and extend that safety.

The UI must show **where it is used** instead of only returning a generic delete failure.

## 1. Allgemein

Fields:

- Name
- enabled/available state if profile disabling is supported
- applicable media kinds
- preferred/default languages
- fallback languages where supported
- optional description
- default assignment per media kind

### Default profile assignment

A profile may be the default for one or more registered media kinds.

Examples:
- Anime
- Series
- Movie
- Book
- Light Novel
- Manga
- Audiobook

Only kinds registered in `MediaAcquisitionRegistry` are available.

### Per-Work override

Individual Works may explicitly use another profile.

The profile editor does not need to list thousands of assignments inline; `Verwendung` owns the detailed list.

## 2. Qualität & Upgrade

### Quality ladder

Show allowed qualities as an ordered drag/drop list.

Each quality row can expose where relevant:

- allowed checkbox;
- quality key/name;
- source/resolution/format;
- group/tier;
- min/preferred/max size;
- upgrade/cutoff relation.

Ordering is significant.

Higher rows are preferred unless a quality group declares items equal.

### Quality groups / tiers

A group means several quality keys are considered equal for quality rank.

Examples:

`1080p WEB`
- WEB-DL 1080p
- WEBRip 1080p

`4K WEB`
- WEB-DL 2160p
- WEBRip 2160p

Rules:

- grouping affects quality comparison/rank;
- score rules can still differentiate members inside an equal-quality group;
- a release can only belong to one effective quality tier;
- group ordering is explicit.

The UI should make this simpler than editing raw quality keys.

### Upgrade policy

Controls:

- Upgrades aktivieren
- Upgrade bis Qualität / Ziel-Tier
- Mindestscore für akzeptierten Download
- Upgrade bis Score

Semantics:

- `MinimumScore`: a candidate below this score is rejected.
- `Upgrade bis Score`: once the existing release reaches this score at the relevant quality/tier, same-quality score-only upgrades stop.
- quality upgrades stop at the configured target quality/tier.

Do not conflate minimum acceptance score with the upgrade stop score.

### Size policy

The existing absolute min/max size support remains valid.

Target UX should also support media-appropriate sizing where practical.

For video:
- MB/min
- bitrate-derived range
- or equivalent normalized measure

For books/documents:
- absolute size may remain more appropriate.

For each quality or quality group, allow where supported:

- Minimum
- Preferred
- Maximum

`Preferred` influences ranking only if the scoring model explicitly supports it; it must not be decorative.

If the backend only supports min/max initially, hide Preferred rather than fake it.

### Quality acceptance and scoring are separate

Quality determines whether/rank class a release belongs to.

Release Rules determine additional preference/rejection.

A higher-resolution release is not automatically the best release if it violates language/rule policy.

## 3. Release-Regeln

This is Jularr's integrated replacement for the normal UX of Sonarr Custom Formats + Release Profiles.

### Core model

A Release Rule has two conceptual layers:

#### Shared definition

Answers:

`What does this rule detect?`

Examples:

- Dual Audio
- SubsPlease
- Dolby Vision
- x265 / HEVC
- Atmos
- Unwanted Groups
- Dub Only
- Season Pack

#### Profile effect

Answers:

`What should this Acquisition Profile do when the rule matches?`

Examples:

- Erfordern
- Bevorzugen +50
- Benachteiligen -25
- Ablehnen
- Nur Information / 0

The same shared rule definition can have a different effect/score in different profiles.

### Why this is not a separate Custom Formats page

The normal workflow stays inside the profile:

`Release-Regeln → Regel hinzufügen`

The admin chooses:

- **Vorhandene Regel verwenden**
- **Neue Regel erstellen**

A secondary **Regelbibliothek** is allowed inside the page/dialog for reuse and management.

There is no required permanent Admin sidebar destination named `Custom Formats`.

### Rule table

Default columns:

- Aktiv
- Name
- Bedingungen
- Wirkung
- Score
- Reihenfolge
- Aktionen

One rule per row.

### Rule actions

Supported effects:

- **Erfordern / Require** — hard eligibility requirement
- **Bevorzugen / Prefer** — positive score
- **Benachteiligen / Avoid** — negative score
- **Ablehnen / Reject** — hard rejection independent of an arbitrary giant negative score
- **Nur Information / Info** — match visible in diagnostics but no score/reject effect

The backend must implement `Require` and `Reject` explicitly before the UI exposes them.

Do not emulate hard requirements/rejections through magic score values such as `+10000` or `-10000`.

### Conditions

A rule may contain one or more conditions.

Target fields include, as parser/domain capability permits:

#### Generic
- Raw Title
- Release Group
- Provider / Indexer / Source Provider
- Release Type
- Pack Type / Season Pack / Multi
- Size
- Age
- Proper
- Repack

#### Video
- Source
- Resolution
- Video Codec
- Bit Depth
- HDR format / Dolby Vision
- Audio Codec
- Audio Channels
- Atmos / object audio where parser data exists
- Audio Language
- Subtitle Language
- Dual Audio
- Multi Audio

#### Reading/document
- document format such as EPUB/PDF/CBZ where normalized
- language
- retail/source marker
- edition/release attributes where parsed

Only conditions meaningful for the profile's applicable media kinds are shown by default.

### Match operators

At minimum:

- Equals
- Not Equals
- Contains
- Does Not Contain
- Regex
- Not Regex

Additional numeric operators where appropriate:

- <
- <=
- >
- >=
- between

Regex belongs under advanced behavior when a normalized field/operator can express the same rule.

### Condition groups

Rules must support explicit boolean structure.

At minimum:

- **ALLE** conditions must match (AND)
- **EINE DAVON** must match (OR)
- **NICHT** / negation for a condition or group

The UI must make this visible.

Do not copy implicit/opaque grouping semantics that admins have to memorize.

### Rule editor

Right-side editor or sheet:

- Name
- optional description
- condition group mode
- conditions
- `+ Bedingung`
- effect
- score when effect uses score
- advanced settings
- save/cancel

Live validation:
- invalid regex
- incompatible field/operator
- duplicate/redundant condition
- impossible AND group where detectable

### Shared rule library

The lower/secondary Rule Library may filter:

- All
- Video
- Audio
- Language
- Release
- Source
- Size
- Other

Each library row shows:

- rule name
- concise normalized condition summary
- category
- usage count
- add-to-profile action

### Editing shared rules

If a shared rule is used in multiple profiles, editing its **definition** must not silently change all profiles.

Before a shared-definition edit, offer:

- **Für alle Profile ändern**
- **Als eigene Kopie bearbeiten**

Changing only this profile's effect/score does not alter the shared definition.

## Languages

Language policy should be normalized, not encoded only through title regex.

Support as appropriate:

- required audio languages;
- preferred audio languages;
- required subtitle languages;
- preferred subtitle languages;
- fallback policy;
- dual/multi-audio preference.

Language rules may be represented as shared Release Rules under the hood, but the normal profile editor may expose common language choices through simpler controls.

Do not require users to build regex for normal language requirements.

## 4. Wartezeit & Quellen

This integrates the useful concept of delay profiles into the Acquisition Profile.

### Purpose

Allow Jularr to wait briefly for a preferred release instead of grabbing the first merely acceptable candidate.

### Controls

Where supported:

- preferred acquisition path/source class;
- Native Usenet delay;
- external/torrent delay when such acquisition path is supported;
- grab immediately at target/highest desired quality;
- grab immediately at score >= X;
- optional maximum wait;
- provider/source allowlist;
- provider/source preference or penalty;
- capability/content-type-specific provider preference;
- whether configured fallback-only providers may be used for this profile/media kind.

### Source/provider restrictions

The profile may say:

- all eligible release-search providers;
- only selected providers;
- prefer selected providers;
- avoid selected providers;
- allow or disallow the configured fallback tier for this profile/media kind.

This references Provider IDs/capabilities.

For Reading Sources, the shared Admin Provider configuration is the canonical owner of whether a source is enabled, its declared capabilities and whether it normally participates or is **Fallback only**. An Acquisition Profile may narrow the eligible set or preference for a media kind, but it must not duplicate provider configuration.

Example for Light Novels/Books:

`normal Reading Sources → normal acquisition/search → fallback-only Reading Sources such as Internet Archive when no acceptable result exists`

The exact order is capability-aware: a reference/preview source remains reference/preview even when it is the only fallback result. A profile cannot promote it into an importer.

It does **not** own:
- provider URL/API key;
- provider enable/disable state;
- provider capability declarations;
- mandatory licensing/rights/access checks;
- rate limits;
- provider health.

Those remain in Admin Providers.

### Implemented on dev: one profile owns wait and sources for every media kind (#396)

**Wait.** The wait is the profile's fallback ladder (`FallbackTiers`): the qualities of the profile are taken at once, and after N minutes of the title being wanted the listed qualities are allowed too, as a temporary choice while the title stays wanted for the better one. The one `ReleaseSelectionEngine` applies it for Movie, TV, Anime, Book, Light Novel, Manga and Music from a stored start (the request's creation time, or the Anime unit's became-wanted time), so a restart never starts a wait over. Manual Search, the profile test and Automatic Search read the same reason ("allowed from fallback tier N, after M minutes (from <time>)"), which names when the release becomes eligible. Book, Light Novel and Manga used to rank with "wanted since now", so their ladder could never be reached; they now use the request's creation time.

**Sources.** `QualityProfile.SourcePolicy` holds the indexer entries the profile may search (`AllowedEntryIds`, empty = every indexer that takes part) and the entries that win ties (`PreferredEntryIds`), both by the canonical entry id of the Indexer settings. Every search of a profile (Movie, TV, Anime, Book, Light Novel, Manga, Music; Automatic and Manual alike) applies it through `SearchOptions.WithSourcePolicy`, and `IndexerSearchCoordinator` enforces it. A restricted profile is never widened: when none of its indexers is enabled the search asks nobody, reports "no other indexer was asked" and counts as an unavailable source, not as a failed search; an allowed indexer that is down is not replaced by another one. A caller's own restriction (the legacy Anime tag restriction) is only narrowed by the profile. A preferred source is moved ahead of the configured priority for the same release, so it is the first one tried and wins ties; it never makes an unacceptable release acceptable.

**Admin.** Admin → Acquisition Profiles has one "Wait & sources" section (waiting steps, the indexer allow and prefer lists; an indexer the profile still names but that was deleted stays listed as "Removed" until it is unticked, so deleting an indexer never widens a profile) and a Test: one release name, its size, how long the title has been wanted and the indexer that found it, run through the shared engine with the profile exactly as edited (identity is assumed), answering taken now, taken as a temporary choice, waiting until a time, needs a decision, not taken, or not found because the indexer is not searched.

**Not yet:** a per-Work profile override for Book, Light Novel and Manga requests (their requests carry no Work id, so only the kind default applies); "grab at once at score >= X" and a maximum wait; a source penalty and the fallback-only provider switch per profile; a preferred downloader path.

### Downloader path

If multiple download paths are available:

- Native Downloader
- compatible external client

the profile may express a preference only if routing architecture supports it.

Downloader credentials/configuration remain in Downloader.

## 5. Verwendung & Test

Two purposes:

1. explain where the profile is used;
2. test exactly how the profile evaluates a release.

## Verwendung

Show:

- media-kind defaults using this profile;
- Works explicitly assigned to it;
- number of assignments;
- links to affected Works where practical.

Actions may include:

- set as default for media kind;
- bulk reassign selected Works;
- clear explicit override and return to kind default.

Bulk operations require preview/count before save when many Works are affected.

## Score-Test

The test surface is a first-class feature.

Input options:

- paste a release title;
- select/use a real normalized candidate from Manual Search where context exists;
- optionally enter size/source/provider when not derivable from the title.

### Test result pipeline

Show the same decision stages used by automatic acquisition:

1. **Target / identity match**
2. **Hard rejection rules**
3. **Quality eligibility**
4. **Release Rules / score contributions**
5. **Minimum score**
6. **Wait/delay policy**
7. **Upgrade decision** when comparing against an existing local release

Example result:

`Akzeptiert · Score 125 · WEB-1080p · keine Wartezeit`

or:

`Abgelehnt · Regel "Dub only"`

### Rule breakdown

Show every relevant evaluation:

- base quality / tier
- matched positive rule +score
- matched negative rule -score
- hard reject
- unmatched optional rules may remain hidden by default
- total score
- minimum score comparison
- upgrade comparison where requested

### Parsed release information

Show normalized parser output:

- title/target
- source
- resolution
- codec
- HDR
- audio
- languages
- subtitles
- release group
- release type
- size
- provider/indexer

Only show values actually parsed/known.

### One scorer everywhere

Automatic search, Manual Search and Score-Test must use the same canonical decision/scoring engine.

No hidden second UI-only scorer.

Manual Search may add identity confidence/rejection context around the same profile score.

## Candidate decision order

The target decision order follows `docs/AUTOMATIC_RELEASE_SELECTION.md`:

`Target/Identity → Safety + Require/Reject → Quality Tier → Fallback Tier → Preference Score → Coverage Utility → bounded Reliability/Source Tiebreak → Wait Policy → Upgrade Decision`

Important:

- identity mismatch cannot be repaired by a high score;
- Require/Reject and hard safety cannot be overridden by positive preference score;
- quality/rules are independent dimensions;
- fallback relaxation must be explicit;
- waiting does not make an otherwise rejected release acceptable;
- network response order is never a tiebreak.

## Import / Export

### Jularr format

Support import/export for:

- full Acquisition Profile;
- shared Release Rule definitions;
- profile effects/scores;
- quality groups;
- wait/source policy where supported.

Use a versioned Jularr schema.

Import validates before writing.

### Sonarr migration

Sonarr data is an **import source**, not a permanent parallel model.

Migration mapping target:

- Sonarr Quality Profile → Jularr Acquisition Profile quality/upgrade policy
- Sonarr Custom Format definition → shared Jularr Release Rule definition
- Custom Format score in a Sonarr Quality Profile → Jularr profile effect/score
- Sonarr Release Profile Must Contain / Must Not Contain → Jularr Release Rules
- Sonarr Release Profile preferred terms → scored Jularr Release Rules
- Sonarr Delay Profile → Jularr Wait/Source policy
- Sonarr Tags → migration matching/assignment evidence; do not require tags as the permanent runtime mechanism when direct profile assignment is sufficient
- Sonarr monitoring state → monitoring/import migration contract, not profile scoring itself

Imported values must be reviewable before commit.

### Hard-reject migration

If Sonarr used extreme negative scores to simulate rejection, migration may **suggest** converting them to explicit `Reject`, but must not guess silently where intent is ambiguous.

Preview the conversion.

## Relationship to Monitoring

Acquisition Profile answers **what release is acceptable/preferred**.

Monitoring answers **what media/unit is wanted and when**.

They are related but distinct.

A monitored Work/unit references the effective Acquisition Profile.

Do not copy quality/release rules into per-Work monitoring rows.

## Relationship to Wanted

Wanted shows targets that need acquisition.

Each Wanted target resolves its effective Acquisition Profile.

Wanted may show:
- profile name
- language target
- current quality target

Editing the reusable profile happens here, not inline in Wanted.

## Relationship to Manual Search

Manual Search shows normalized candidates and the exact profile evaluation.

Candidate rows may expose:
- quality
- score
- matched rule summary
- rejection reasons

Opening score details reuses the same explanation contract as Score-Test.

## Relationship to Providers

Provider/indexer configuration remains in Admin Providers.

This includes the Reading Sources family. Admin Providers owns:
- adapter registration/configuration;
- enable/disable;
- capabilities;
- priority;
- Normal vs Fallback-only participation;
- health/backoff;
- credentials/endpoints where applicable;
- licensing/access notes and mandatory safety constraints.

Acquisition Profile may reference:
- allowed provider IDs;
- preferred/avoided provider IDs;
- whether the configured fallback tier is eligible for the profile/media kind.

It never stores provider credentials and never overrides provider safety/capability limits.

## Relationship to Downloader

Native Usenet server settings, bandwidth, queue, verify/repair/extract and external-client adapter configuration remain in Downloader.

Acquisition Profile may only choose a supported preferred acquisition path/routing policy when such a choice is enabled by architecture.

## Relationship to Storage / Import

Storage owns:
- Mounts;
- LibraryRoots;
- Native Download Workspaces;
- Generic Downloads Roots;
- default LibraryRoot per content/media type;
- effective LibraryRoot import placement policy: HardlinkOrCopy / Hardlink / Copy / Move.

A Work may hold an explicit target-root override through the canonical library/monitoring contract.

Acquisition Profile does not store:
- arbitrary filesystem paths;
- LibraryRoot routing tables;
- Hardlink/Copy/Move policy;
- remote-path mappings.

External downloader path translation belongs to the specific Downloader external-client adapter.

Migration/coexistence path translation belongs to the relevant Migration/integration adapter.

There is no separate permanent `Import & Routing` Admin page. Physical placement is resolved from Storage + Work target-root state after Acquisition has already selected the release.

## Current backend migration path

Implementation should evolve incrementally.

### Keep

- generic `QualityProfileStore`
- `MediaAcquisitionRegistry`
- kind defaults
- Work assignments
- `ReleaseScorer`
- normalized `ReleaseInfo` parsing
- existing score fields
- current validation and regex timeout behavior

### Extend

- version the persisted profile state;
- introduce quality groups;
- introduce upgrade-until-score;
- introduce shared Release Rule definitions + profile-specific effects;
- add composite boolean conditions;
- add explicit Reject effect;
- add source/provider/release-type fields where normalized parser/provider data exists;
- add wait/source policy;
- add richer size policy.

### Migrate existing persisted profiles

Existing profile files must migrate forward automatically and deterministically.

Current simple `ReleaseScoreRule` entries can migrate to:
- one-condition shared rule definition;
- profile effect = scored preference/penalty.

Current:
- MustContain
- MustNotContain
- RequiredRegex
- RejectedRegex

can migrate into explicit Release Rules while preserving behavior.

Do not require admins to recreate existing profiles manually.

## Validation

Profile validation includes:

- unique ID/name policy where required;
- at least one usable quality/format;
- valid quality ordering/groups;
- no duplicate group membership;
- valid cutoff/target quality;
- valid score thresholds;
- min <= max sizes;
- valid regex with timeout;
- valid rule field/operator/value;
- referenced shared rule exists;
- referenced provider exists or is clearly unresolved;
- no impossible circular/shared-rule reference model.

Invalid profiles cannot become defaults.

## Unsaved changes

The editor uses explicit Save.

Changes across the current profile tabs are saved atomically as one profile transaction/version.

Score-Test may evaluate the **unsaved draft** and must label that clearly, so admins can test before committing.

Navigation away with unsaved changes uses standard unsaved-change protection.

## Detailed / Compact Admin mode

The global Admin density preference applies.

### Detailed

- explanatory help for thresholds;
- richer rule summaries;
- visible test breakdown;
- usage context.

### Compact

- dense profile list;
- table-oriented quality/rules;
- less repeated help text;
- same capabilities and validation.

No rule or score information disappears solely because Compact is selected.

## Clean design baseline

Planning mockups use Clean:

- neutral light background;
- purple Jularr accent;
- compact Admin shell;
- table/list-driven configuration;
- border/light-tint status chips;
- no decorative anime background artwork.

Media artwork may appear only as actual profile/example media content.

Dark mode remains required for implementation.

## Platforms

### Desktop

Primary full editor.

Left profile list + right editor is preferred.

### Tablet

Supported:
- collapsible profile list;
- full-width tab editor;
- rule editor as side sheet/full-height panel.

### Mobile

Supported for:
- profile selection;
- quality ordering/toggles;
- simple rule enable/effect/score editing;
- score test;
- usage inspection.

Complex composite rule creation may use a full-screen editor.

Do not squeeze desktop tables into phone width.

### TV

Unsupported.

## Required states

- no profiles
- profile loading
- profile ready
- unsaved draft
- saving
- save failed
- invalid profile
- invalid regex
- unresolved provider reference
- profile in use
- delete blocked
- migrated legacy profile
- imported profile preview
- import conflict
- test parsing
- test accepted
- test rejected by identity
- test rejected by quality
- test rejected by rule
- test below minimum score
- test delayed/waiting
- upgrade available
- upgrade cutoff reached
- permission denied

## Must not implement

- No separate Anime/Movie/Book scoring engines.
- No separate permanent Custom Formats page required for normal use.
- No duplicate Release Profile model just to mimic Sonarr.
- No duplicate Delay Profile model just to mimic Sonarr.
- No magic giant negative score as the only hard-reject mechanism.
- No hidden scoring rules.
- No UI-only scorer.
- No raw YAML/JSON as the primary editor.
- No regex requirement for normal language/codec/source rules.
- No provider credentials in Acquisition Profiles.
- No downloader server settings in Acquisition Profiles.
- No Storage paths in Acquisition Profiles.
- No Hardlink/Copy/Move setting in Acquisition Profiles.
- No final LibraryRoot routing table in Acquisition Profiles.
- No remote-path mapping in Acquisition Profiles.
- No separate permanent Import & Routing page.
- No target/identity mismatch overridden by score.
- No silent cross-profile edits to shared rule definitions.
- No silent Sonarr-import guess that converts ambiguous negative scores to Reject.
- No loss of current persisted profile behavior during migration.


## 2026-10-06 search/selection refinement

Binding generic behavior now lives in:

- `docs/ACQUISITION_SEARCH_PLANNER.md` / #864 — how candidates are searched;
- `docs/AUTOMATIC_RELEASE_SELECTION.md` / #865 — how normalized candidates are automatically accepted/ranked/delayed/upgraded.

This profile spec remains the canonical **Admin editor UX** for those policies. It must not create a second search or scoring engine.

### Search is not scoring

Acquisition Profile Release Rules normally do **not** rewrite indexer queries. Query construction is owned by the Search Planner from canonical target identity + indexer capabilities. Profile rules evaluate normalized returned candidates.

The profile may narrow eligible providers/indexers/media paths or express source preference, but it does not encode raw Newznab query templates.

### Required rule effects

Target profile effects are now explicitly:

- Require
- Prefer
- Avoid
- Reject
- Info

`Require` is a hard eligibility gate. Do not force common requirements into inverse regex or magic negative scores.

### Fallback tiers

The profile may define explicit timed fallback tiers that relax only rules/preferences the owner chose to relax.

Each tier must show:
- when it becomes active;
- which requirements/preferences change;
- whether a candidate accepted at this tier is Temporary or Final;
- which final target Jularr continues upgrading toward.

Canonical identity and hard safety never relax.

Fallback tiers integrate with Wartezeit & Quellen; do not create a separate scheduler.

### Upgrade benefit

In addition to target quality and Upgrade-until-score, profiles may set where meaningful:
- minimum quality-tier improvement;
- minimum score delta;
- optional maximum added size/storage cost;
- optional cooldown/minimum interval for equivalent upgrades.

This prevents repeated tiny upgrades and score oscillation.

### Pack / multi-unit policy

Pack preference is evaluated with actual canonical coverage utility. Do not model `Season Pack +N` as sufficient by itself.

Profile controls may express preference for packs, but automatic selection also considers:
- how many Wanted targets are covered;
- completeness;
- existing local coverage;
- duplicate/unwanted units;
- size/storage cost.

### Compatibility intent

Profiles may expose simple inspectable intents/presets such as Maximum compatibility / Balanced / Best quality only when they resolve to explicit normal rules. Never introduce an opaque AI compatibility score.

### Validation / simulation expansion

The Test surface must also support:
- rule-conflict detection;
- impossible AND/Require/Reject combinations where detectable;
- unreachable fallback/upgrade conditions;
- simulation against real normalized recent/historical/current candidates;
- predicted Auto winner, fallback tier and Temporary/Final state.

All simulation uses the same production evaluator.
