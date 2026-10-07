---
name: jularr-ui
description: Jularr UI work (Razor, CSS, shared components, consumer/admin surfaces, responsive fixes) from the surface's binding SPEC.md and approved mockup images, with a visual completion gate. Extends jularr-task.
---

# Jularr UI

Extends `jularr-task` (owner first, test policy, coordination, finish/stop all apply). UI is not done because Razor compiles or tests pass.

## Workflow

1. Identify the exact surface; its binding is `docs/mockups/<surface>/SPEC.md`. Read that SPEC completely.
2. List and actually view every approved image in that surface's folder only (`*.png *.jpg *.jpeg *.webp`; filenames carry no meaning). If an older image conflicts with newer SPEC text, the text wins.
3. Read another shared SPEC (e.g. `request-status-details`, `error-permission-states`, `media-preview`) only when the surface SPEC explicitly depends on it or the changed shared component is owned there. Do not read `docs/UX.md`, `docs/INFORMATION_ARCHITECTURE.md` or other mockup folders by default, do not audit other pages, and do not reinterpret or "improve" a settled design unless redesign was requested.
4. Inspect only the directly relevant shared shell/component code and reuse it (`src/Jularr.Web/Pages/Shared`, `wwwroot`, `Ui`). Razor Pages are page boundaries; shared markup lives in partials/view components. No page-local copies of existing components, no inline styles, no `!important`, no decorative gradients/glow, no left accent bars, no duplicated headings, eyebrows or explanatory text.
5. Implement the approved design. Cover the states the SPEC lists: desktop/mobile (tablet/TV when listed), Light/Dark/System, loading, empty, error, partial data, forbidden, keyboard operation, visible focus, labels, `prefers-reduced-motion`, Back-navigation context.
6. Authorization is server-side; hiding a control is never the permission check. No secrets, provider tokens or host paths (Admin diagnostics only when the SPEC says so). User-facing strings go through the localization resources (`Features/Localization`).
7. Start or reuse the existing local app (`run` skill / `jularr-dev-env`; loopback only, no firewall prompts) and inspect the real result in the browser pane.
8. Fix discrepancies, run focused tests/build per `jularr-task`, then stop.

## Visual completion gate

For meaningful visual changes verify, as applicable: Desktop Light, Desktop Dark, a narrow/tablet sanity check, and Mobile when shared responsive code changed. Compare SPEC, approved mockup and the rendered app: spacing, hierarchy, typography, controls, theme contrast, hover/focus, empty/loading/error states, no regressions elsewhere on the page; check the browser console. No screenshot framework for one change. If something could not be verified, say exactly what.

## Consumer action semantics

Consumer actions express user intent, not backend infrastructure; derive them from canonical state and policy, never from a hard-coded verb.

| Media | Actions |
| --- | --- |
| Video | Play, Continue, Start watching; Request only when approval is actually required |
| Reading | Read, Continue reading; Request only when approval is actually required |
| Audio | Listen, Continue listening; Request only when approval is actually required |

When auto-approval or immediate acquisition allows the intended action, the primary action may be Play/Read/Listen while Request/Search/Download runs behind the canonical acquisition state machine. The UI never creates a second acquisition path. Admin/manual infrastructure controls may use infrastructure terms (Search, Download, Import) where the SPEC says so.
