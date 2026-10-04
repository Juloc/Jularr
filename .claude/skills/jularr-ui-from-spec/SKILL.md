---
name: jularr-ui-from-spec
description: Build or change a Jularr UI surface from its binding SPEC.md and approved mockup images - responsive, themed, all states, accessible. Use for any Razor page, shared component, or consumer/admin screen work.
---

# Jularr UI from spec

1. Find the surface under `docs/mockups/<name>/`. Read the complete `SPEC.md` plus cross-referenced specs (`docs/UX.md`, `docs/INFORMATION_ARCHITECTURE.md`, shared specs such as `request-status-details`, `error-permission-states`, `media-preview`).
2. List every image in that directory and in linked/shared mockup directories (`*.png *.jpg *.jpeg *.webp`) and actually view each with the Read tool. Filenames carry no meaning. If an older image conflicts with newer SPEC text, the text wins.
3. Reuse existing components, partials and CSS tokens (`src/Jularr.Web/Pages/Shared`, `wwwroot`, `Ui`). Razor Pages are page boundaries; shared markup lives in partials/view components. No page-local copies of existing components, no inline styles, no `!important`, no decorative gradients/glow, no left accent bars, no duplicated headings, eyebrows or explanatory text.
4. Cover desktop, mobile (tablet/TV where the spec lists them), Light/Dark/System, loading, empty, error, partial data, forbidden/unauthorized, keyboard operation, visible focus, labels, `prefers-reduced-motion`, and Back-navigation context.
5. Authorization is server-side; hiding a control is never the permission check. Do not expose secrets, provider tokens or host paths (Admin diagnostics only when the spec says so).
6. Verify visually: start the app (see the `run` skill), drive it in the browser pane, compare with the images in light and dark and at mobile width, and check the console. State honestly what could not be verified.
7. User-facing strings go through the localization resources (`Features/Localization`). The visible acquisition action is always **Request**; Instant is approval policy, never a different button.
