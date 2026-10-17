# Player and Reader design (not finished)

Work in progress, copied so it can be continued from any checkout. It is not part of the
Jularr build: nothing here is compiled, bundled or served.

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
