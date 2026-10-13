# Admin Game Import Resolution

Status: UX and visual direction approved for Desktop, Tablet and Mobile. The owner will upload the approved mockup image into this folder.

Shared Admin contract: `docs/mockups/admin-games/SPEC.md`.
Games architecture: #725.
UX planning: #729.
Acquisition/import foundations: #389, #697, #715.

## Purpose

Resolve the exceptional case where a completed Game import cannot safely determine its canonical Game Work/platform/WorkVersion target automatically.

This is not a normal import step.

Normal path:

```text
Known Wanted/Request target
 -> CompletedDownload(ContentKind.Game)
 -> Games importer
 -> match/validate
 -> import automatically
```

Exception path:

```text
Games importer cannot safely resolve one required identity field
 -> Activity / To-Do
 -> Game Import Resolution
 -> Admin supplies only missing/ambiguous decisions
 -> retry same staged import
```

## Entry point

Primary entry is from:
- Activity / To-Do;
- a needs-attention import event;
- correlated import failure requiring Admin resolution.

This is not a permanent Games navigation page.

The screen may offer `In Activity öffnen` or equivalent correlation link.

## Core UX rule

Ask only for what is genuinely unresolved.

Examples:

### Everything known
Do not open this surface.

### Game ambiguous, platform known
Ask only for the canonical Game.

### Region ambiguous
Show Game/platform as confirmed and ask only for Region.

### Platform ambiguous
Ask for Platform; then re-evaluate available Game/release candidates.

### Multiple release variants ambiguous
Ask only for the field(s) that materially distinguish the target release.

Do not render a generic form where every field is editable by default.

## Source summary

Show concise source/import evidence:

- original package/download display name;
- contained relevant file(s);
- file size;
- detected format/container;
- correlation/import identity where useful for diagnostics.

Do not show:
- full server filesystem paths;
- final destination path;
- storage internals;
- database IDs;
- raw downloader implementation data.

Example:

```text
Quelle
Pokemon.Emerald.USA.GBA.zip
512 KB · ZIP · 1 Datei

Enthaltene Datei
pokemon_emerald.gba
16 MB · GBA ROM
```

## Recognized information

Show deterministic evidence with clear state.

Example:

```text
Format                    GBA ROM                ✓
Plattform                 Game Boy Advance       ✓
Titel aus Dateiname       Pokémon Emerald        ?
Region aus Dateiname      USA                    ✓
```

State semantics:
- confirmed/strong deterministic evidence;
- unresolved/needs review;
- conflicting;
- unavailable.

Confirmed fields are read-only unless the ambiguity resolution specifically requires changing them.

Do not present every recognized value as an editable form field.

## Candidate Game matching

When canonical Game Work identity is unresolved, show a compact candidate section.

Candidate row may include:
- cover;
- title;
- platform;
- release year;
- match label.

Preferred wording:
- `Vorgeschlagen`;
- `Bester Treffer`;
- `Weitere Treffer`.

Do not show fabricated AI confidence percentages.

A numeric score may only be shown if Jularr has a real deterministic documented scoring model whose value is meaningful to the Admin.

## Candidate selection

Selecting `Spiel auswählen` opens a focused candidate chooser.

### Desktop/Tablet

Centered modal.

Contents:
- search field;
- matching Games list;
- cover;
- title;
- platform;
- year;
- suggested/best-match label where applicable;
- selected row;
- Abbrechen / Auswählen.

This is a canonical Game picker, not a duplicate Discover page.

### Mobile

Use a full-width sheet/page with:
- search;
- large candidate rows;
- radio/selection state;
- `Auswählen`.

Returning from the chooser updates the same Import Resolution surface.

No wizard step counter.

## Game / release section

Once a canonical Game is selected, show only the release attributes that need confirmation.

Possible fields:
- Platform;
- Region;
- Revision;
- Language.

Rules:
- strong confirmed values are read-only;
- ambiguous values use a select/choice control;
- fields irrelevant to the selected platform/release are omitted;
- no raw ROM filename/path fields here.

Example:

```text
Spiel
Pokémon Emerald

Plattform
Game Boy Advance      ✓ erkannt

Region
[ USA ▾ ]

Revision
[ Rev 0 (Standard) ▾ ]

Sprache
[ Englisch ▾ ]
```

## Additional information

A collapsed `Zusätzliche Informationen` section may show useful diagnostics such as:
- file size;
- detected file type;
- filename;
- source package name;
- import/correlation reference.

Do not show the absolute filesystem path in normal UI.

Deep diagnostics, if needed, belong in Activity/Operations.

## Unclear recognition state

When multiple Games remain plausible, show a concise warning:

```text
Mehrere mögliche Spiele

Dieses Spiel konnte nicht eindeutig erkannt werden.
Bitte wähle das richtige Spiel aus.
```

Then show the strongest candidates and `Spiel auswählen`.

No AI-themed wording.

No recommendation percentage unless backed by a real scoring contract.

## Actions

Primary:
- `Importieren`

Secondary:
- `Später`

Destructive:
- `Import verwerfen`

### Importieren

Validates the selected resolution and retries/continues the existing staged Games import.

It does not create a new download.

### Später

Leaves the import unresolved/staged and keeps or returns it to Activity / To-Do.

No data is discarded.

### Import verwerfen

Requires normal destructive confirmation.

Discarding the import:
- marks this import as intentionally abandoned/cancelled;
- follows configured staging/retention cleanup policy;
- does not blindly delete unrelated source/library files.

The confirmation must state the actual effect.

## Import execution

On confirmation:

```text
Resolution
 -> validate against staged import
 -> Games importer
 -> final Games LibraryRoot placement
 -> Work/WorkVersion create or link, with GameRelease extension where required
 -> Operation/Activity completed
```

Final path/naming remains owned by Games importer.

The Admin never chooses a final filesystem destination here.

## Retry / failure behavior

If import fails after resolution:
- preserve the staged source when safe;
- preserve the Admin's selected resolution;
- return an actionable failure reason;
- allow retry;
- correlate with the same Activity/Operation.

Do not force the Admin to repeat successful selections after an unrelated transient failure.

## Desktop layout

Approved direction is an Admin detail surface rather than a tiny modal.

Use the normal light Jularr Admin shell.

Main content uses two columns when space allows:

### Left — Evidence
- Source;
- contained files;
- recognized information;
- metadata/candidate evidence.

### Right — Resolution
- selected Game;
- only unresolved release fields;
- optional additional information;
- actions at bottom.

Do not show absolute paths.

Do not turn the page into a permanent Import dashboard.

## Tablet layout

Use the same Evidence + Resolution structure when width permits.

Reduce density:
- fewer inline metadata columns;
- candidate picker remains modal;
- actions remain obvious.

No horizontal-scroll-heavy tables.

## Mobile layout

Mobile is one continuous Import Review surface, not a wizard.

The approved image may visually show overview/detail examples separately; implementation must not require a sequential `Übersicht -> Weiter -> Details` flow for information already available.

Recommended single-page order:

1. source summary;
2. recognized information;
3. unresolved warning/candidate summary when needed;
4. selected Game;
5. unresolved release fields;
6. actions.

Candidate selection opens a separate chooser and returns to the same review page.

If screen length requires progressive disclosure, use collapsible sections, not numbered wizard steps.

## Mobile candidate chooser

Full-width chooser:
- search field;
- candidate rows;
- selected state;
- Auswählen.

This is the only separate selection surface required for normal ambiguity.

## Mobile unclear-recognition state

Compact warning card plus likely candidates.

Primary action:
- `Spiel auswählen`

Do not require a fake `Weiter` step before showing the unresolved decision.

## Validation

Before enabling `Importieren`, ensure all required unresolved fields have valid values.

The UI consumes validation from the Games import application layer.

Conceptually:

```text
GameImportResolution
- ImportId
- SourceSummary
- DetectedFiles[]
- Evidence[]
- CandidateGameWorks[]
- SelectedWorkId?
- Platform
- Region?
- Revision?
- Languages[]
- ValidationIssues[]
- CanImport
```

The UI does not implement media matching itself.

## Matching/scoring boundary

No general AI classifier.

Matching may use deterministic evidence such as:
- acquisition/Wanted target;
- filename parsing;
- platform/file-format mapping;
- provider metadata IDs;
- checksums where a legitimate metadata source supports them;
- deterministic candidate scoring.

If ambiguity remains, ask the Admin.

Do not call an opaque AI model merely to avoid showing this exception flow.

## Security and filesystem boundary

Hard rules:
- no arbitrary destination-path entry;
- no unrestricted server file browser;
- no raw absolute path in standard UI;
- no executable launch;
- no runtime/BIOS configuration;
- no downloader controls;
- source remains inside permitted staging/import roots.

## Required states

- one unresolved Game candidate;
- multiple Game candidates;
- unresolved Platform;
- unresolved Region/Revision;
- ready to import;
- validation failed;
- retrying import;
- import succeeded;
- transient import failure;
- source missing;
- permission denied;
- backend unavailable.

## Success

On successful import:
- mark correlated To-Do/Activity item resolved;
- return to Activity or relevant Game detail/context;
- optional concise success toast.

No dedicated success page required.

## Visual direction

Use standard Jularr Admin UI.

Clean:
- light neutral surfaces;
- navy text;
- restrained purple accent;
- semantic success/warning/error colors;
- same spacing/cards/forms as other Admin surfaces.

No gaming-themed decorative background.

## Explicitly not in this surface

- no general import queue;
- no automatic AI classifier;
- no Discover clone;
- no arbitrary filesystem destination;
- no full path display in standard UI;
- no downloader/indexer controls;
- no runtime configuration;
- no BIOS/Firmware management;
- no metadata-provider settings;
- no ROM execution;
- no multi-step wizard for fields already known.

## Visual baseline

The approved mockup covers:
- Desktop Import Review;
- Desktop Game candidate chooser;
- Tablet Import Review;
- Mobile Import Review;
- Mobile candidate chooser;
- Mobile unclear-recognition state.

The visual image is illustrative where it shows separate Mobile states. The implementation follows the single-review-page rule above.

The owner will upload the approved image into this folder.

The mockup defines visual direction; this text spec defines behavior/data/boundaries and wins on conflict.
