# Jularr Requests – approved HTML handoff (V2)

Start with **`preview-single-file.html`** in a normal browser; it works offline without a web server or dependencies. The split project is `index.html`, `styles.css`, `app.js`, and `assets/`.

- Top-right avatar → **Instance modules**: switch Arr-only (Discover; no User Library) and Full Jularr (Home + User Library). Preview Light/Dark in the same dialog.
- Requests: change tabs, search, filters, sorting and page size (20/50/100/200/Custom).
- Select rows with checkboxes / Ctrl/Cmd / Shift, or the tri-state checkbox in the table header. Selection displays the floating bulk toolbar. Use `…` to select all filtered results.
- Click a row to open details. Single and bulk actions update local demo data or request confirmation.
- Responsive: desktop tables, mobile cards. No third-party libraries or online requests.

The admin sidebar shows route previews for all other destinations. Only Requests is a working screen in this package. **No API connection, persistence, real file deletion, network calls or real module enablement** is implemented.

See `SPEC.md` for the design contract and `LUNA_HANDOFF.md` for the implementation task.

## V2 refinements

- Native OS dropdowns replaced by fully styled open menus with keyboard, selection and dark-mode visuals.
- Subtle outline type/status badges, smaller centered checkboxes, portrait covers.
- Real mobile main action, details and overflow menu; dark ghost actions with readable borders/text.
- Admin-only version visibility, no “Admin only” text.
- See the updated `SPEC.md` and `LUNA_HANDOFF.md` before implementing in Jularr.

Browser QA results and screenshot manifest: `QA_CHECKLIST.md`.
