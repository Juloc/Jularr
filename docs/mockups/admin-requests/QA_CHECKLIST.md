# Browser QA — V2 (2026-10-08)

## Verified against self-contained HTML in headless Chromium

- 1715×960 and 1440×900 Desktop; 390×844 and 320×700 Mobile.
- No JS runtime errors in both light and dark screenshot flows.
- 15×15 rounded/cross-centered row checkboxes; 2:3 portrait cover (36×54 desktop / 43×64.5 mobile).
- Mobile main, Details and More buttons have nonzero visible width. Overflow includes contextual valid actions.
- Styled **opened** filter dropdown uses portaled menu, accessible option roles/selected check and value update; no native opened browser select.
- Page-size changes to 50 and Custom; custom size 35 affects pagination.
- Selecting one request shows floating bulk actions, header select-all enters indeterminate state, clearing selection hides toolbar.
- Change Profile editor also receives styled dropdown instead of native browser select.
- Role-based visibility: version chip hidden when demo isAdmin=false; visible label contains no “Admin only”.
- Arr mode displays Discover, hides User Library; Full mode displays Home and User Library.
- No horizontal document overflow at 320px width.

## Not verified / production requirements

- This remains a **static client-side prototype**. No real media APIs, permanent database writes, authorization validation, module worker isolation, real migrations or file deletion.
- Full screenreader and cross-browser testing remains for Luna/Jularr production integration.
- All prototypes use sample data and intentionally do not implement production-only workflows.

## Screenshots

- `preview-desktop-v2.png`, `preview-desktop-1440-v2.png`, `preview-mobile-v2.png` — initial view.
- `preview-desktop-v2-dark.png`, `preview-desktop-1440-v2-dark.png`, `preview-mobile-v2-dark.png` — dark view after filtering Anime.
- `preview-mobile-320-v2.png` — 320px layout.
- `preview-dropdown-v2.png`, `preview-mobile-dropdown-v2.png` — real **open** custom select menu.
