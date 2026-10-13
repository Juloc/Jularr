# Game Play Options Dialog

Status: UX and visual direction approved for Desktop, Tablet, Mobile and TV. The owner will upload the approved mockup image into this folder.

Shared contract: `docs/mockups/games/SPEC.md`.
Game Detail: `docs/mockups/game-detail/SPEC.md`.
Games architecture: #725.
UX planning: #729.

## Purpose

Small conditional chooser shown only when `Play` or `Continue` cannot resolve to one safe launch target automatically.

This is not a normal step before every game launch.

Normal path:

```text
Play / Continue
 -> one obvious valid LaunchPlan
 -> launch immediately
```

Ambiguous path:

```text
Play / Continue
 -> more than one meaningful valid choice
 -> Play Options
 -> ResolveLaunch
 -> launch
```

## When it appears

Show only for real ambiguity, for example:

- multiple materially different local GameReleases;
- multiple compatible runtimes with no preferred/default choice;
- region/revision choice that changes the actual local release;
- a launch option that cannot be derived safely from the current Game/Save context.

Do not show merely because multiple technical runtime cores exist internally.

Do not show if Jularr already has a valid preferred/default choice.

## Continue behavior

`Continue` should normally resolve from the save/recent-play state.

If the selected Save State or normal save is bound to one specific WorkVersion/GameRelease-extension/runtime-compatible path, use that automatically.

Do not ask the user to reselect a release that is already implied by the save.

Only show Play Options for Continue when more than one genuinely compatible continuation path remains.

## Dialog content

### Header

- title: `Spiel starten` / `Play Game`;
- compact Game cover/title;
- short explanatory line only when needed;
- Close/Cancel.

No hero artwork or large marketing content.

### Version / Release

Show this section only when multiple meaningful GameReleases are available.

Each option may show:
- region;
- revision/version;
- platform when relevant;
- language summary;
- local availability;
- preferred marker if one exists;
- concise compatibility state.

Do not show:
- file path;
- filename;
- hash;
- indexer/source;
- ROM/disc container format unless user choice genuinely depends on it.

Use clear labels such as:
- USA;
- Europe;
- Japan;
- Rev 1;
- English / Japanese.

### Runtime / Wiedergabe

Show only when more than one actual user-meaningful runtime choice remains.

Examples:
- `Im Browser`;
- `Extern öffnen`;
- another named approved runtime if it materially changes the experience.

Do not expose:
- EmulatorJS core IDs;
- internal adapter names;
- executable paths;
- BIOS paths;
- technical flags.

If one runtime is the configured/default valid choice, select it automatically and hide this section.

### Save / Continue context

When launched from Continue, show the chosen recent save compactly if useful:
- thumbnail;
- saved time;
- optional user-visible save name/location.

This is informational, not another required selection unless multiple Save States were explicitly chosen before opening the dialog.

## Primary action

Bottom/right primary button:
- `Spielen`;
- or `Fortsetzen` when the action originates from Continue.

Disabled only when the currently selected combination cannot produce a valid LaunchPlan.

If a choice is invalid, explain the exact concise reason next to that option.

Examples:
- BIOS fehlt;
- Runtime unterstützt diese Version nicht;
- auf diesem Gerät nicht verfügbar.

## Remembering choices

Keep this conservative.

Optional:
- `Für dieses Spiel merken`

Only show when the choice is stable and safe to persist.

Persistence scope:
- preferred GameRelease for this Game/profile;
- preferred runtime for this Game/profile when applicable.

Do not silently make a global platform/runtime Admin decision from this consumer dialog.

Admin runtime defaults remain Admin-owned.

The user can later clear/change the remembered preference through the same chooser or Game Detail where appropriate.

## Desktop

Use a centered modal.

Target:
- compact width;
- no full-page navigation;
- release options as concise rows/cards;
- runtime choices below only when needed;
- primary action in modal footer.

Do not create nested dialogs.

## Mobile

Use a bottom sheet or full-height sheet only when content requires it.

Order:
1. Game summary;
2. Version options if needed;
3. Runtime options if needed;
4. Remember choice if applicable;
5. sticky `Spielen/Fortsetzen` action.

Large touch targets.

No dense desktop table.

## Tablet

Use the Desktop modal structure at comfortable width.

If space is constrained, use the Mobile sheet behavior.

## TV

Use a centered focusable chooser.

Rules:
- very few large options;
- strong focus state;
- Gamepad/remote navigation;
- primary action clearly reachable;
- no tiny dropdowns;
- no technical metadata.

If only a release choice is ambiguous, show only releases.

If only runtime is ambiguous, show only runtimes.

## Selection logic

The UI consumes already-filtered valid choices from the Games application layer.

Conceptually:

```text
PlayOptions
- WorkId
- Origin: Play | Continue
- SaveContext?
- ReleaseChoices[]
- RuntimeChoices[]
- SelectedReleaseId?
- SelectedRuntimeKey?
- CanRemember
- Validation
```

The UI does not calculate emulator compatibility itself.

Selection changes may trigger a lightweight revalidation so impossible combinations become unavailable immediately.

## Launch contract

On confirm:

```text
PlayOptionsSelection
 -> ResolveLaunch(...)
 -> LaunchPlan
 -> Game Player
```

If the launch becomes invalid between opening and confirming:
- keep the dialog open;
- refresh choices/state;
- show concise reason;
- do not silently fall back to another release/runtime without user awareness.

## Empty/error behavior

If no valid launch target remains:
- do not render an empty chooser;
- show one concise unavailable state;
- offer Back/Close;
- authorized Admin may get a deep link to relevant remediation.

Normal users never receive BIOS/runtime configuration forms here.

## Approved visual composition

The approved mockup defines these platform layouts:

### Desktop
- centered modal over the existing Game Detail page;
- compact Game cover/title summary;
- release choices first;
- runtime choices only when genuinely ambiguous;
- optional `Für dieses Spiel merken`;
- `Abbrechen` + `Spielen/Fortsetzen` in the footer.

### Tablet
- same modal hierarchy at comfortable touch size;
- no extra wizard/navigation layer;
- same choice semantics as Desktop.

### Mobile
- bottom sheet over Game Detail;
- compact Game summary;
- stacked release/runtime options;
- large touch targets;
- primary `Spielen/Fortsetzen` action at the bottom.

### TV
- centered focusable overlay;
- only the ambiguous choices;
- large row targets and strong focus state;
- primary action reachable by gamepad/remote;
- no small dropdowns or dense metadata.

The visual example may contain illustrative runtime labels such as a concrete emulator/runtime name. Consumer implementation should prefer product-facing labels such as `Im Browser` unless the concrete runtime name is itself intentionally user-visible and useful. Internal adapter/core names remain hidden.

The mockup is the visual reference; this text spec defines behavior and wins on conflict.

## Visual direction

Use normal Jularr components.

Clean:
- light surfaces;
- navy text;
- restrained purple selection/focus;
- subtle border/shadow;
- same modal/sheet language as the rest of Jularr.

Original:
- warm light/cream surfaces;
- red accent;
- same information hierarchy.

No gaming-themed decorative shell.

## Must not implement

- no dialog before every launch;
- no multi-step wizard;
- no BIOS upload;
- no runtime configuration;
- no raw paths/files;
- no core IDs;
- no acquisition/download controls;
- no global runtime preference mutation;
- no duplicate Game Detail content;
- no player controls inside the dialog.
