# Copy/paste task for GPT-6 Luna (Codex)

You are working on **Jularr**, on the dedicated approved frontend/UI work branch. Work incrementally. Use the attached `jularr_requests_mockup.zip`.

## Binding design input

The attached package contains a working Requests mockup:
- `preview-single-file.html` (easiest to open and inspect in a browser);
- `index.html`, `styles.css`, `app.js`, `assets/` (inspectable structure);
- `SPEC.md` (behavior and contract);
- `QA_CHECKLIST.md` (verified preview behavior and implementation gaps);
- `reference-approved.png` (original AI visual inspiration);
- `reference-current-before.png` (previous unsuccessful production result).

**Do not implement based on the AI screenshot alone.** The HTML is the measurable layout reference. Read `SPEC.md` first, inspect how the actual mockup behaves, and then inspect Jularr's real Razor/WebKit/frontend Shell and Requests code.

## Critical new operating mode rule

- Arr-only = label the common landing **Discover**; hide User Library. Admin Library stays.
- Full Jularr = label the common landing **Home**; User Library shown when allowed.
- Use the single shared/capability-derived navigation owner. Hiding a disabled module must also prevent its backend tasks/API access; do not implement fake toggle-only isolation.

## V2 non-negotiable visual corrections

Prior Codex implementation failed at visual fidelity. **Do not use `reference-approved.png` as the exact layout; use `preview-single-file.html` and its V2 screenshots.**

- 15×15 centered, rounded checkboxes and proper check icon, including indeterminate header state.
- True 2:3 portrait artwork, never square thumbnails.
- Outline-first semantic status/type badges (tiny tint only, icon + text + thin frame).
- Styled popup dropdown menus **including options**, checkmarks, keyboard and dark-state. No default OS `<select>` menus visible.
- Every mobile card must expose main action + details + overflow. Overflow exposes all other eligible workflow actions; verify with an Open and Failed request.
- No visible “Admin only” string in version badge; hide chip entirely for non-admins.
- Ghost/outline buttons in Dark mode must remain clearly recognizable and readable.
- Do not blindly copy the prototype's custom dropdown code if WebKit has an accessible Select/Popover primitive; implement equivalent behavior with the existing shared component.

## Do exactly one vertical slice at a time

1. Shared Shell: breadcrumb, global Search, notifications and profile top right, theme in Settings only, admin-only version badge, Arr/full navigation.
2. Requests tabs and filters (All excludes Done), no unnecessary title.
3. Dense table, poster/metadata/availability badges and equal-width pill actions with fixed icon slots.
4. Tri-state select-all, Ctrl/Cmd/Shift row selection, floating bulk toolbar and valid-action permissions.
5. Page size 20/50/100/200/Custom, pagination and filter consistency.
6. Responsive mobile cards, Light/Dark, accessible focus/keyboard, reduced motion.
7. Screenshot/visual QA with the actual app.

For each slice: implement, compile, test, run the actual browser, take screenshots, compare against the attached HTML at 1715×960 / 1440×900 / 390×844 / 320×700, then fix meaningful deviations before continuing.

No broad redesign of unrelated screens; changes to shared tokens/components must be deliberate and regression-tested. No duplicate implementation/data owners or unvalidated in-memory business logic in production. Every business operation must call the canonical Jularr backend and reflect success only after confirmation.

At the end, provide: changed files, screenshots from the **actual** app, tests/results, remaining deviations, and required backend dependencies. If browser inspection is unavailable, say so rather than claiming visual validation.
