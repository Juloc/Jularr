# Automatic Release Selection — V1

Status: approved planning direction.

Owner issue: #865  
Parent: #396  
Depends on search candidates from: `docs/ACQUISITION_SEARCH_PLANNER.md`  
Primary profile UX: `docs/mockups/admin-acquisition-settings/SPEC.md`  
Manual Search UX: `docs/mockups/admin-manual-search/SPEC.md`  
Wanted UX: `docs/mockups/admin-wanted/SPEC.md`

This specification defines **how Jularr decides which normalized candidate may be automatically selected, delayed, treated as temporary, upgraded or sent to manual review**.

Search and selection are intentionally separate.

## Canonical hierarchy

Do not reduce the whole decision to one flat score.

```text
Canonical target / identity
-> hard safety + identity requirements
-> profile Require / Reject
-> quality eligibility + quality tier
-> explicit fallback tier
-> preference score
-> coverage utility
-> bounded transparent reliability tiebreak
-> profile source/indexer preference
-> deterministic final tiebreak
-> wait/delay policy
-> upgrade decision
```

A high preference score can never repair:

- wrong Work;
- wrong episode/volume/chapter/unit;
- explicit Reject;
- violated permanent Require rule;
- hard safety/integrity failure.

## Identity confidence

Identity evidence is separate from profile preference.

Normalized states:

- **Exact**
- **Strong**
- **Ambiguous**
- **Conflict**

Automatic selection may only use states allowed by policy.

Trustworthy provider/external IDs are stronger evidence than fuzzy title similarity, but trustworthy conflicts produce Conflict/manual review instead of silent acceptance.

Examples:

- canonical ID + matching unit = Exact/Strong;
- title similar but wrong episode = Conflict/Rejected;
- provider ID vs title/year materially inconsistent = Conflict;
- ambiguous anime absolute-vs-season mapping = Manual Review unless canonical mapping resolves it.

Identity mismatch is never fixed by scoring.

## Rule effects

Acquisition Profile rule effects:

- **Require** — candidate must satisfy it;
- **Prefer** — positive score;
- **Avoid** — negative score;
- **Reject** — hard rejection;
- **Info** — diagnostics only.

Do not emulate Require/Reject with arbitrary giant positive/negative score values.

Permanent Require and Reject live before preference scoring.

## Quality and preference remain separate

Quality tier determines eligibility/rank class.

Preference score differentiates otherwise eligible candidates.

A higher resolution is not automatically better if it violates language, compatibility, size or explicit profile rules.

Equal-quality groups/tiers remain supported; rules can distinguish members within an equal tier.

## Explicit fallback ladder

Profiles may define staged fallback tiers.

Example:

```text
Tier 0 now: DE+JA · WEB 1080p
Tier 1 after 2h: any DE 1080p
Tier 2 after 12h: JA + DE subtitles
Tier 3 after 24h: 720p
```

Rules:

- safety and canonical identity never relax;
- a permanent Require does not relax;
- a preference/requirement may relax only when explicitly modeled in a later fallback tier;
- every transition has reason + timestamp;
- fallback integrates with the existing wait/delay scheduler;
- ordinary score changes never silently redefine requirements.

## Temporary vs final acceptance

A fallback candidate may be **Temporary accepted**.

Meaning:

- download is allowed now under explicit profile policy;
- target can remain upgrade-wanted;
- history records active fallback tier and why the candidate was temporary;
- searching/upgrades stop only once the configured final goal is satisfied.

Do not equate successful import with final profile satisfaction.

## Coverage utility

Single, multi-unit and pack candidates are evaluated by canonical coverage.

Consider:

- Wanted targets covered;
- completeness;
- unwanted/duplicate units;
- existing local coverage;
- total size/storage cost;
- explicit profile pack preference;
- duplicate-download risk.

A complete season pack can win when most/all season episodes are missing, but should not automatically beat an ideal single episode when only one unit is Wanted.

Coverage is evaluated after identity/hard eligibility.

## Multi-target acquisition

When one candidate validly covers several Wanted targets:

- represent canonical coverage set;
- prevent parallel duplicate grabs for each covered target;
- relate one download/import to all confirmed covered targets;
- mark only confirmed targets as grabbed/fulfilled;
- keep unrelated/unconfirmed units unchanged.

## Upgrade benefit

Upgrade policy may include:

- minimum quality-tier improvement;
- minimum score delta;
- Upgrade until quality/tier;
- Upgrade until score;
- optional maximum additional size/storage cost;
- optional cooldown/minimum interval between equivalent upgrade attempts.

Rules:

- never downgrade;
- avoid endless tiny score oscillation;
- equivalent candidate requires explicit material benefit;
- Proper/Repack special handling only when profile says so;
- current file and candidate use the same evaluator/profile semantics.

## Compatibility-aware profiles

Profiles may expose simple intent/presets such as:

- Maximum compatibility;
- Balanced;
- Best quality.

They resolve to explicit inspectable rules/constraints. They are never an opaque AI score.

Compatibility may consider codec/HDR/audio/direct-play goals using canonical device/playback policy, but must not depend on one transient playback session.

## Hard safety/integrity

First-class hard rejection may include normalized evidence for:

- unsupported password/encryption;
- corrupt/incomplete/invalid package;
- blocklisted failed release;
- wrong protocol/media type;
- impossible hard size bounds;
- unsafe/unexpected executable/package content where relevant.

These are not negative preference points.

## Reliability tiebreak

Historical success may be used only as a bounded late tiebreak.

Possible evidence:

- release-group import success/failure history;
- indexer grab/import success;
- repeated wrong-result rate.

Constraints:

- cannot override identity/Require/Reject/quality eligibility/explicit profile preference;
- bounded contribution;
- minimum sample/confidence;
- contribution visible in diagnostics;
- do not punish new groups/providers permanently from tiny samples.

No opaque ML ranking is required.

## Deterministic tie-break

Same target + profile + candidates + health snapshot should select the same winner.

Network response order is never a tiebreak.

Documented stable order should include:

1. hard eligibility / fallback tier;
2. quality tier;
3. preference score;
4. coverage utility;
5. bounded reliability;
6. profile source/indexer preference;
7. indexer priority;
8. stable candidate/source identifier.

## Manual Search explanation

Every candidate can answer:

- why is this Eligible / Warning / Rejected?
- why would/wouldn't Auto choose it?
- what identity evidence was used?
- which Require/Reject gate fired?
- which score rules contributed?
- which fallback tier is active?
- Temporary or Final?
- what canonical units does it cover?
- why did candidate A beat B?

Optional diagnostic **What would need to change?** may explain, without mutating configuration:

- `Minimum score would need to be <= 80`
- `German audio is a permanent requirement`
- `Identity is ambiguous; scoring cannot fix this`

## Profile conflict detector

Detect where practical before save/test:

- Require X + Reject X;
- impossible AND groups;
- unreachable minimum score;
- fallback tier that can never activate/usefully differ;
- upgrade stop conditions that make upgrades impossible/oscillatory;
- duplicate/redundant rules.

Warnings must identify exact conflicting rules.

## Profile simulation

Test tab may evaluate recent/historical/current normalized candidates using the exact production evaluator.

Show:

- eligible/rejected/manual-review counts;
- candidate(s) Auto would select;
- fallback tier;
- temporary/final result;
- predicted upgrades;
- score/rule distribution.

Simulation is not a second scorer.

## Search/selection separation

Search Planner determines **how candidates are found**.

This engine determines **what candidates mean for the current target/profile**.

Normal Release Rules/Custom Formats must not rewrite Search Planner queries.

Search narrowing belongs to target/indexer/media policy. Preference belongs here.

## Must not implement

- no flat score overriding wrong identity;
- no giant magic score to simulate Reject/Require;
- no pack fixed bonus without actual coverage context;
- no reliability/AI score overriding explicit rules;
- no response-order winner;
- no implicit fallback relaxation;
- no "downloaded = final target reached" assumption;
- no separate Manual/Automatic/Profile-Test scorers.

## Acceptance

- identity/safety/Require/Reject precede score;
- identity confidence is explicit;
- fallback tiers are explicit and timed;
- temporary acceptance remains upgrade-aware;
- packs use coverage utility;
- one candidate can satisfy several Wanted targets without duplicate grabs;
- upgrade requires meaningful configurable benefit;
- reliability is only transparent late tiebreak;
- selection is deterministic;
- Manual Search explains every decision;
- conflict detection + simulation reuse the same evaluator.
