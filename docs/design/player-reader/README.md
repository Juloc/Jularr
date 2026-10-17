# Player and Reader design (not finished)

The workbench sources in this directory remain reference-only; they are not compiled or served.
The active Player/Reader code belongs to `src/Jularr.Frontend` and consumes canonical
Jularr Backend services, not illustrative sample data.

Source: the local UiDesigner workbench, project `Jularr`, branch `design/player-reader-preview`,
Player snapshot `138c7a98568ceebb4361f25db2d474f5a2ba432d`; completed Reader snapshot
`6261334` (2026-10-17). There it renders at
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

The original Reader snapshot was unfinished: the newer model/paging helper had not
been connected to its older client and styles. The updated reference completes the
shared paged/vertical-scroll design, automatic/manual one/two-page layouts, native
colour input and Clean/paper surfaces without landscape decoration. UiDesigner
passed its build, four unit tests, 54 component checks and 176 browser clicks.
Those are design tests, not proof of real EPUB/PDF/comic or video playback.

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

- Player: one shared Speed/Audio/Subtitles/Delivery settings panel, playback speed 0.25×–4× in 0.05× steps, nine presets, Fit/Fill/Original and working track selection. Subtitle sync changes the actual WebVTT cues within ±10 seconds (0.05-second increments, reset, no accumulated drift). A delayed, layout-stable buffering indicator distinguishes persistent stalls from short buffering. If the browser rejects an initially accepted Direct Play source, the client attempts one HLS Remux/Transcode re-plan through the existing Backend (only when browser HLS is supported), otherwise it shows a real error. The underlying session, stream, progress and mini-player remain canonical.
- Reflowable Reader: Book/Light Novel typography presets, independent font/spacing/width settings, an explicit focus mode, RTL-aware spread navigation, keyboard/touch paging and visible Previous/Next page controls. Automatic page fitting also has explicit one/two-page overrides. Text clicks and selections never turn a page. Typography, viewport changes and translation reflow restore the canonical block locator. Existing section locators, reading progress, highlights and translation remain canonical. EPUB/reflowable settings intentionally do not change the current comic/PDF adapters yet.
- Locale keys use the existing English source and shipped language catalogs, not workbench `design.*` keys.
- The approved native `Form.Color` uses existing DataField binding, field labels,
  validation metadata, indexed names and read-only behavior; no new dependency or database schema.

## Reconciliation with clean-start

`codex/player-reader-finish` was created from current `origin/clean-start` and combines
#1213 without replacing newer target files. Its additional live fixes are an accent
played-range overlay driven by the real media element and a Reader page stride that
subtracts inline padding rather than drifting on each turn. The active working checkout remains
untouched. No active database changes or migrations. The Reader's single shared
frame for all document adapters remains an integration requirement, not a claim that
the current separate text/comic shells have already been unified.

Focused local verification: Frontend TypeScript/Vite build, UI resource coverage,
20 Player/Reader/translation client tests, 26 Playback/Reading/Form component tests,
and four real browser paths (film/episode, tracks, novel/manga/light novel and PDF).
All database tests used the dedicated isolated PostgreSQL fixture. Full release
validation, actual NAS reproduction and physical-device checks remain separate gates.

**Not yet finished:** audio/video lip-sync correction and per-file Admin/Personal timing overrides (unlike the working session-only WebVTT offset), chapter/intro/recap/outro markers, full device-specific Player settings, platform/fullscreen parity, the complete Reader visual layout, and real NAS/device/browser E2E (#1208). Automated browser tests cover the new controls and HLS fallback but must pass CI on the latest commit before claiming verification. This PR remains draft; never copy workbench sample data into production.
