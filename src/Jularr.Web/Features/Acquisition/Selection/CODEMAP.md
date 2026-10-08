# Profiles and selection

Purpose: one profile model and one deterministic selection for every media type.

Canonical owners
- `QualityProfile` / `QualityProfileState` (`Quality/QualityModels.cs`), stored by `QualityProfileStore` (`quality-profiles.json`, version 3). Kind defaults and per-Work assignments resolve through `ResolveAsync`.
- `ReleaseSelectionEngine.Select(profile, candidates, reliability)` (`Selection/ReleaseSelection.cs`): the only ranking. `ReleaseScorer` applies the profile gates and rule weights to one release.
- `UpgradePolicy`: the one cutoff and upgrade rule (quality steps of the profile's order; no score or wait).
- Editor: `QualityProfileEditing` (form to profile and back) behind Admin `AcquisitionProfiles`.

Order inside the engine
safety -> identity -> Require / Reject / allowed quality / size gates -> quality order -> Prefer and Avoid rules (the row order is the priority;
saved as power-of-two weights) -> identity strength -> coverage -> bounded reliability -> indexer priority -> publish time -> id.
Allowed qualities are taken at once; a better quality upgrades later up to the cutoff. There is no wait, minimum score or score threshold.

Media adapters supply `SelectionCandidate` facts (identity evidence, coverage, safety, context points): `VideoReleaseJudge`, `MusicReleaseJudge`,
`ReadingReleaseJudge`, `BookReleaseSelector`, the anime judge in `AnimeAcquisitionPipeline`.

Migration: profile files of version 2 fold their fallback-tier qualities into the allowed qualities on first read.

Not done yet: a dedicated language priority layer; native import of Sonarr custom formats.

Tests: `ReleaseSelectionEngineTests`, `QualityProfileEditingTests`, `AcquisitionProfilePolicyTests`, `ProfileGrabAndSourcePolicyTests`.
