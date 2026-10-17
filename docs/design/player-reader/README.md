# Player and Reader design (not finished)

The workbench sources in this directory remain reference-only; they are not compiled or served.
The active Player/Reader code belongs to `src/Jularr.Frontend` and consumes canonical
Jularr Backend services, not illustrative sample data.

Source: the local UiDesigner workbench, project `Jularr`, branch `design/player-reader-preview`,
commit `138c7a98568ceebb4361f25db2d474f5a2ba432d` (2026-10-17). There it renders at
`/Preview/player` and `/Preview/reader`.

## Contents

- `Player/`: the player preview with its settings popover (compact and expanded), controls and
  styling (`PlayerDesign.*`, `PlayerSettings.*`).
- `Reader/`: the reader preview with unified presets, paging (`ReaderPaging.ts`) and its sample
  content (`ReaderPreviewData.cs`).
- `Framework/Popover.ts`: the workbench popover both previews use.
- `project.json`: the agreed design tokens and rules of the Jularr project in the workbench.
- `PLAYER_SPEC.md`: the player specification including the interaction quality bar, taken from
  `dev` (`f796acf4`), where it landed by mistake.

## Still open

- The C# files build against the workbench framework (`UiDesigner.Framework`: `Page`, `FrontendAssets`,
  `UiText`); to ship, port them onto Jularr's own components in `src/Jularr.Frontend` (Player module,
  Reading pages) and its texts, keeping Backend services as the only data source.
- The sample artwork (TMDB posters and a backdrop of "Your Name." and "Suzume") stays in the
  workbench: this repository is public and the rights remain with their owners. The Player
  preview references `Images/…` files that are therefore missing here.
- The earlier player prototype in Jularr itself is the branch `codex/player-design`
  ([#1165](https://github.com/Juloc/Jularr/pull/1165)).

## Integrated into the live Jularr components (first release slice)

- Player: one playback-speed slider (0.25×–4× in 0.05× steps) plus nine quick presets using the actual `<video>` playback rate. Existing session, stream, progress, audio/subtitle state and mini-player are unchanged.
- Reflowable Reader: Book/Light Novel typography presets, independently adjustable font, line spacing and reading width, and an explicit focus mode with accessible restore. Existing section locators, reading progress, highlights and translation remain canonical. EPUB/reflowable settings intentionally do not change the separate comic/PDF renderers.
- Locale keys use the existing English source and shipped language catalogs, not workbench `design.*` keys.

**Not yet integrated:** the complete Player settings popover/full-screen tabs, chapters/markers/sync/track appearance, the complete Reader visual layout and precise semantic spread/page navigation, platform parity and full playback/reader E2E. The initial paged layout now uses one or two viewport columns automatically and retains the existing section-level navigation. Never copy the workbench's sample data into production. Verification is required before calling this PR finished.
