# Jularr – Requests HTML design reference

Status: **Design prototype / implementation handoff**, not a Jularr production implementation. 2026-10-08.

## Priority of references

1. This document and explicitly approved UX decisions.
2. The functioning HTML/CSS/JS prototype (`index.html`, `styles.css`, `app.js`).
3. Browser screenshots in this directory.
4. The earlier AI-generated visual reference (`reference-approved.png`), which is **inspiration, not implementation code**.

The HTML is the concrete visual baseline: measure components and spacing in Chromium rather than infer them from the AI reference image. Preserve the meaning and behavior even if adapting markup to Jularr's real WebKit/shared components.

## Scope

The prototype covers the Requests page and the surrounding Shell; sidebar links to other destinations are illustrative placeholders. All displayed data is local sample content. Search, filter, navigation mode, theme, bulk selection, pagination, and actions are simulated client-side and **must be replaced by existing canonical Jularr application actions/APIs in production**. No prototype actions claim persistence, actual media deletion, working background workers or production API integration.

## Shell + operating mode – new decision

- **Arr-only mode**: single starting destination is named **Discover**. **No User Library item**. Admin Library remains available for managing media. Watchlist and Calendar depend on actual user permissions/module policies.
- **Full Jularr mode**: same canonical landing destination is named **Home**. User Library is visible if enabled and permitted.
- Do **not** create separate duplicated Home and Discover data/pages solely for this naming difference. The presentation label is derived from the mode and permissions.
- Canonical `UiNavigationCatalog` must own the navigation structure, with server-side capabilities and per-instance module enablement. No parallel hard-coded nav catalog.
- Disabling any optional module hides its entire menu, dashboard widgets, UI, and API affordances. In production its workers/schedulers/network calls must also be inactive; preserve stored data for re-enablement.
- Header: Breadcrumbs left; broad global search center; notifications, avatar + user/profile menu top right.
- Light/Dark/System settings belong to personal/instance Settings, not a theme toggle beside the notifications.
- Version badge in the sidebar is **admin-only**. The prototype always runs as `demo` administrator.

The two modes can be previewed via **avatar → Instance modules**. The same dialog previews Light/Dark.

## Requests page structure

**No large page title or subtitle.** Place status tabs, request-local search/filters and the responsive table immediately beneath the global header.

### Primary workflow tabs

`All`, `Open`, `Approved`, `In progress`, `Rejected`, `Done`, all with accurate counts.

`All` defaults to **all requests excluding Done**. Count semantics must be based on the canonical query/filter scope and independent of the current page size. Request workflow status and technical media availability are separate columns/models.

### Filters

- Local Requests search, independent of global Search.
- Media type; requested language/version; technical status/availability; requester.
- Sort by newest, oldest, modified, title, requester. Preserve filter state across page navigation and reload if supported.
- Server-side pagination/filtering in production, with suitable cache and non-jumping load states.

### Table

Selection checkbox; poster and title/unit/edition; type; requester; requested time; modified time/actor; profile/language; request workflow status; technical media status; seasons/unit coverage; actions.

- Desktop: compact rows and stable column widths, truncate variable-length descriptions. Do **not** let full technical errors increase row height; show full explanation in details.
- Responsive breakpoints: columns simplify on narrower desktop; mobile becomes stacked cards with title, requester, language/version, date, status and primary action.
- Media rows and type badges use consistent semantic colors, not accidental current accent colors.
- Table actions use a **shared equal-sized pill button** with fixed icon start slot and left-aligned content. Round icon-only buttons are dimensionally consistent.
- Main contextual action + details + overflow. No provider-named buttons such as `Remove from Sonarr`.

### Selection

- Checkbox on hover/selected and permanent header tri-state checkbox; layout must not shift on hover.
- Ctrl/Cmd-click toggles row, Shift-click selects a continuous visible range. Normal row click opens details.
- Select all visible is different from select all filtered across pages.
- Floating bottom toolbar appears when selection exists and respects mobile safe areas.
- Actions: Approve, Reject, Retry (overflow where appropriate), Mark as added, Change profile, Remove media, Delete request, and other capability-supported commands.
- Mixed selection: only enable actions genuinely supported for **all** selected items, or ask for explicit partial handling. Never silently succeed on only a subset.
- Delete request and Remove media are distinct domain operations. Destructive actions need explicit confirmation, permission checks, audit and partial-failure reporting.
- Prototype simulates state transitions in memory. Production is API-backed and must use Jularr's existing state machine.

### Pagination

Bottom-left: Page size dropdown `20 / 50 / 100 / 200 / Custom…`, arbitrary **validated integer** (prototype caps at 500), visible range, and go-to-page. Bottom-right: numbered pages and prev/next. On page-size changes, reset to a valid page. Selection across pages must have well-defined scope.

## Approved V2 corrections (binding)

- Checkbox must be 15×15 px, vertically centered, mildly rounded, with centered check/indeterminate glyph. Selection column stays stable when checkbox appears on hover.
- Poster is always **portrait, 2:3**, cropped by `object-fit:cover`, not square. The desktop example uses 36×54 px, mobile 43×64.5 px.
- Request/media status chips and media-type tags are **subtle outlined badges**: semantic icon, label, and fine border do the communication. Only a very faint semantic tint is permitted; avoid large solid colored chips. This applies to light and dark.
- All visible dropdown triggers **and the opened popover menus/options** must use Jularr styling. Browser-native dropdown UI is not an acceptable end result. Menu has selected check, hover/focus/keyboard and a search field for long lists. Native `<select>` is retained only as a non-visible input/value source in the prototype. Portal dropdown panels to body so the mobile filter scroller cannot clip them.
- On **mobile**, every request row exposes its valid main action, details and overflow; overflow offers the remaining eligible operations, including Approve/Reject/Retry, mark as added, profile, remove and delete. Never hide actions just to fit a narrow viewport; use a mobile action sheet if the production stack prefers it.
- The sidebar version chip displays only product/version (`Jularr · v0.1.0`) and **never contains visible “Admin only” text**. Role-based conditional rendering decides whether it exists.
- Secondary/ghost/outline controls in dark mode must have sufficient text, icon and border contrast. A faint button that disappears on the dark background is a regression. Disabled state remains distinguishable.
- Actions keep same-height pill control grammar, fixed left icon slot and left-aligned text slot. The device-specific layout may reflow, but not change semantics.

## Design tokens used by the example

Styles in `styles.css` are canonical for **this prototype**, not a mandate to duplicate standalone CSS into production:

- Accent: `--accent: #7539f9` (Jularr default, must be user-configurable in the real app).
- Background: `--bg: #f7f8fd`, white panels, restrained borders `--line: #e9ebf4`.
- Main text: `--text: #191d39`; secondary `--text-soft: #4e5879`.
- Panel rounding: 15–16 px; buttons and selects capsule geometry (border radius 20 px/50%).
- Table row min-height 67 px; portrait poster 36 × 54 px (2:3); mobile 43 × 64.5 px; button height 35–36 px.
- Shared font: Inter fallback to Segoe UI/system UI; no serif substitution.
- Semantic badges: green success, blue acquisition progress, violet preparing, orange partial, red failure, gray neutral. Status semantics cannot be determined from color alone.
- Dark theme overrides all surface, text, border and semantic tokens consistently.

In production, map these tokens to Jularr/WebKit design tokens. Do not introduce a second CSS framework or duplicate core components.

## Directories

- `index.html` — split-file entry point.
- `styles.css` — visual contract and responsive styles.
- `app.js` — deterministic demo state and interactions.
- `assets/*.jpg` — temporary prototype poster crops from the approved AI mockup, **not production media assets**.
- `preview-single-file.html` — self-contained working HTML preview (all styles/scripts/images embedded).
- `preview-desktop-v2.png`, `preview-desktop-1440-v2.png`, `preview-mobile-v2.png`, and corresponding `-dark.png` — refreshed browser screenshots of the V2 implementation.
- `reference-approved.png` — earlier AI image.
- `reference-current-before.png` — screenshot of the unsuccessful prior Requests implementation.

## Completion contract for Luna

- Start by reading this file, `docs/UX.md`, `docs/INFORMATION_ARCHITECTURE.md`, the real `docs/mockups/admin-requests/SPEC.md`, related specs and existing shared components.
- Inventory the real Requests fields/actions and routes. Prefer canonical API data and existing UI primitives.
- Reconcile any legacy conflict, especially the **Arr-only vs Full** shell decision and `All` excluding Done. Update binding specs with explicit product approval instead of silently diverging.
- Implement one slice at a time: Shell; tabs/filters; table; selection; bulk actions; pagination; responsive/QA.
- After each slice run the real application and take a screenshot at 1715×960, 1440×900 and 390×844; compare against the working HTML reference. Dark mode also required.
- Verify keyboard selection, pagination, no horizontal overflow, stable row heights, access control, form safety and precise destructive-action semantics.
- No fake backend actions, mock-only success claims, security bypass, duplicate media/request model, separate Arr-only page implementation or unneeded dependencies.
- Implemented only when **rendered** screenshots and interaction tests succeed; build/test green alone is insufficient.
