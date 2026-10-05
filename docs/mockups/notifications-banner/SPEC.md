# Persistent / Critical Notification Banner — Target UX

Status: approved planning direction for the finished Jularr notification experience. The approved mockup uploaded to this folder is a visual reference; this text remains binding.

Global UX rules: `docs/UX.md`.
Notification Center: `docs/mockups/notifications-center/SPEC.md`.
Bell / Quick View: `docs/mockups/notifications-bell/SPEC.md`.
Toast / Popup: `docs/mockups/notifications-toast-popup/SPEC.md`.
User notification preferences: `docs/mockups/notifications-settings/SPEC.md`.

Canonical notification architecture: `docs/NOTIFICATIONS.md`.

If an image and this specification conflict, this specification wins.

## Purpose

Persistent Banners represent a **current problem or state that still exists** and needs sustained visibility.

Core distinction:

- Toast: something happened.
- Banner: something is still wrong or still requires action.

Examples:

- storage is full and downloads/imports are paused;
- a critical integration is unavailable;
- account/security action is required;
- a server capability is currently unavailable;
- a system condition is actively blocking work.

A Banner is not:

- a generic success message;
- a replacement for the Notification Center;
- a one-off historical event;
- a job/activity list;
- a modal dialog;
- a place for routine informational notices.

## State-driven model

Banner visibility is driven by **current canonical state**, not by whether a notification row exists.

Target flow:

`Canonical system/account state → BannerConditionProvider → BannerPolicy → BannerPresenter`

A state transition may additionally emit a notification event:

`false → true = optional Toast + Notification Center event + Banner`

While the problem remains:

`true → true = Banner remains; no repeated event spam`

When resolved:

`true → false = Banner disappears automatically`

Reading, dismissing or deleting a Notification Center item must not resolve the underlying Banner condition.

## Severity classes

### Warning

A degraded condition exists, but the affected function can still work or has fallback behavior.

Examples:

- storage is running low;
- one indexer/integration is unavailable;
- a non-critical provider is degraded.

### Critical

A core capability is blocked or there is material risk to operation/data.

Examples:

- storage full and new downloads/imports are paused;
- required backend/service unavailable;
- backup/restore safety issue;
- critical infrastructure condition.

### Security

A profile/account security condition requires attention.

Examples:

- required account verification;
- compromised/revoked credential condition;
- mandatory session/security action.

Security may use the Critical visual level where appropriate but remains semantically distinct in policy.

## Placement

### Desktop

Banners appear directly below the global top bar and above page content.

They:

- span the usable main-content width;
- push content downward;
- never overlay or cover normal page content;
- preserve the current page/context;
- remain outside the left sidebar.

No page-specific banner placement is allowed for global conditions.

### Mobile

Banners appear directly below the mobile top bar.

They:

- use full available app width with normal horizontal inset;
- push page content down;
- use a stacked, readable layout;
- never cover bottom navigation or active content.

## Stack behavior

Do not show an unbounded vertical wall of warnings.

Runtime rules:

- maximum **two visible Banner rows** at once;
- highest severity first;
- newer/more actionable item wins when severity is equal;
- remaining active conditions collapse behind a compact `N weitere Probleme` affordance or equivalent system-status entry;
- duplicate conditions update the existing Banner instead of creating another one.

Escalation updates the existing Banner:

`Warning → Critical`

Do not create a second Banner for the same canonical condition.

## Banner anatomy

A Banner contains:

1. semantic status icon;
2. clear title;
3. concise consequence/context;
4. one primary corrective action;
5. optional secondary action only when genuinely useful;
6. optional dismiss/snooze control only when policy permits it.

No artwork/posters are used in Persistent Banners.

## Text hierarchy

Preferred pattern:

`Problem title`
`What is affected / consequence`

Examples:

`Speicherplatz voll`
`Neue Downloads und Imports sind pausiert, da kein Speicherplatz mehr verfügbar ist.`

`Indexer nicht erreichbar`
`Neue Suchen und automatische Updates können fehlschlagen.`

The user should understand both the state and the consequence without opening details.

Avoid raw exception text, stack traces or provider diagnostics in the Banner.

## Primary action

Each Banner exposes the most useful corrective action.

Examples:

| Condition | Primary action |
| --- | --- |
| Storage full | Speicher verwalten |
| Storage low | Speicher prüfen |
| Indexer unavailable | Integration prüfen |
| Required provider unavailable | Anbieter öffnen |
| Backup failure blocking safety | Backup öffnen |
| Account verification required | Konto prüfen |
| Server connection lost | Erneut verbinden |
| Security/session action required | Sicherheit prüfen |

Do not use generic `Details` when a direct corrective destination exists.

The action must route to the canonical owner of the problem.

## Secondary action

A secondary action is allowed only when it materially helps.

Examples:

- `Erneut versuchen`;
- `Später erinnern` for snoozable warnings.

Do not show two competing corrective actions by default.

## Dismiss / snooze policy

Every Banner condition has explicit policy.

### Persistent

Cannot be dismissed while the condition remains true.

Example:

- storage full and downloads are actively blocked.

No close control is rendered.

### Snoozable

May be hidden temporarily, but resurfaces after the snooze period or state escalation.

Example:

- storage low warning.

The snooze duration/state belongs to canonical Banner policy/state, not page-local JavaScript.

### Dismissible instance

The user may dismiss this specific occurrence until the underlying state changes again.

Suitable only for non-critical advisories.

### Resolved

Banner disappears automatically as soon as the canonical condition is false.

The approved mockup shows close affordances as component examples. Runtime must render them only when the active Banner policy allows dismissal/snooze.

## Resolution behavior

When a problem is corrected:

- Banner disappears automatically;
- the page layout closes the reserved space smoothly;
- Notification Center history remains unchanged;
- optional lightweight action feedback may say, for example, `Speicherproblem behoben`.

Do not emit repeated success notifications for routine recovery unless product policy requires one.

## Relationship to Notification Center

The Notification Center records events that occurred.

The Banner represents a condition that is currently true.

Example:

1. available storage crosses critical threshold;
2. one canonical event is emitted;
3. Notification Center receives the event;
4. Banner appears;
5. user reads/removes the Notification Center item;
6. Banner remains because storage is still full;
7. user frees space;
8. canonical condition resolves;
9. Banner disappears.

This separation is binding.

## Relationship to Toast / Popup

On first transition into a problem state, policy may show a Toast in addition to the Banner.

Do not repeat the Toast on every refresh/check.

Typical behavior:

- import failed once → Toast only;
- storage full and blocking future imports → Banner, optionally one initial Toast;
- indexer transient hiccup → Toast or no attention;
- indexer remains unavailable past the canonical threshold → Banner.

Attention escalation should be state/policy driven.

## Relationship to Bell

Bell unread count is based on durable inbox state.

An active Banner does not independently increment the Bell badge unless a canonical In-App notification event was delivered.

Removing/snoozing a Banner does not change unread count.

## Thresholds and stabilization

Conditions that can flap must use canonical stabilization/hysteresis.

Examples:

- storage warning threshold;
- repeated provider failures;
- transient network availability.

Do not show/hide Banners on every single short probe failure.

The owning domain/provider state should expose a stable condition such as:

- Healthy;
- Degraded;
- Unavailable;
- Critical.

Banner presentation consumes that stable state.

## Retry actions

`Erneut versuchen` is valid only when the canonical owner provides a safe retry/check operation.

Retry:

- triggers the owning health/reconnect operation;
- shows local progress;
- does not duplicate business operations;
- updates the Banner from canonical result.

Do not implement retry as page reload when a real domain operation exists.

## Global vs profile scope

Banner scope must be explicit.

### Instance/global condition

Examples:

- storage full;
- required backend unavailable.

Visible to profiles allowed to know/act on that condition.

Do not expose Admin infrastructure detail to ordinary profiles without policy.

### Profile condition

Examples:

- account verification;
- personal connection/authentication problem.

Visible only to that profile.

No cross-profile state leakage.

## Admin conditions

Admin/system Banners must respect backend authorization.

An ordinary user must not gain infrastructure information or Admin deep links merely because the global Banner presenter exists.

Where normal users are affected by an Admin condition, they may receive a user-safe derivative message such as:

`Downloads sind vorübergehend nicht verfügbar`

while Admin profiles receive the actionable technical Banner.

Do not use the presentation layer to invent this translation; the canonical condition/policy owns the audience/message level.

## Immersive surfaces

Normal Warning Banners must not overlay immersive experiences such as:

- video fullscreen;
- reader fullscreen;
- manga immersive mode;
- game/player mode.

Preferred behavior:

- defer non-critical Banner presentation until shell chrome is visible again;
- keep the underlying condition active;
- Bell/Center state stays correct.

Critical/Security conditions that must interrupt may use a reduced safe-area alert appropriate to the immersive surface.

Never permanently cover subtitles, playback controls or game controls.

## Mobile layout

Mobile Banner layout is intentionally different from Desktop.

Preferred pattern:

`[Icon] Title`
`Consequence text`
`Primary action`

Rules:

- one column;
- action may span full/large width;
- close/snooze control stays easy to reach when allowed;
- no tiny right-aligned desktop action squeezed into the row;
- >=44 px targets;
- maximum two visible Banners still applies.

## Accessibility

Banners must:

- use semantic alert/status roles appropriate to severity;
- communicate Warning/Critical through icon + text, not color alone;
- keep actionable controls keyboard accessible;
- not steal focus automatically;
- announce newly appearing Critical states appropriately;
- avoid repeatedly re-announcing an unchanged persistent condition;
- expose the corrective action clearly;
- preserve sufficient contrast in Light/Dark/Original themes.

## Localization

Banner titles, consequence text and actions use Jularr localization resources.

Stored condition identity is canonical and language-neutral.

Technical diagnostics may remain available in the destination Admin/System screen, not embedded into localized Banner text.

## Architecture ownership

Binding ownership:

- Storage/provider/account/system domain owns the actual current state;
- BannerConditionProvider normalizes eligible current conditions for presentation;
- BannerPolicy owns severity, audience, dismiss/snooze rules and preferred action;
- BannerPresenter owns shell rendering/stack order;
- Notification event pipeline owns historical event delivery;
- Notification Center owns durable inbox presentation;
- ToastPresenter owns transient attention;
- target Admin/Profile feature owns corrective action.

Do not store Banner truth in NotificationStore.

Do not let individual pages manually persist global Banner state.

## Visual contract

Use Jularr Clean Light Mode for the approved reference.

Binding direction:

- banners immediately below top bar;
- full main-content width;
- pale severity-tinted surface, not saturated blocks;
- thin severity-colored border;
- semantic icon inside a restrained tinted icon area;
- dark navy text;
- clear bold title;
- consequence text directly below/alongside title;
- action aligned right on Desktop;
- optional close/snooze at far right only when policy permits;
- no gradients;
- no glow;
- no artwork;
- no card-grid/dashboard treatment.

### Severity visuals

Critical:

- pale red/pink background;
- red semantic icon/accent;
- strong corrective primary action when appropriate.

Warning:

- pale amber/yellow background;
- amber warning icon/accent;
- neutral or restrained corrective button.

Exact colors come from shared theme tokens, not page-local literals.

## Approved mockup composition

The approved reference is a single Desktop 16:9 Light Mode Jularr consumer screen.

The normal Jularr Home/Dashboard remains visible.

Directly under the global top bar are two full-width Banner examples:

### Banner 1 — Critical

`Speicherplatz voll`

Supporting text explains that new downloads/imports are paused because no storage remains.

Primary action:

`Speicher verwalten`

Visual treatment:

- pale red surface;
- red critical/storage icon;
- stronger primary action.

### Banner 2 — Warning

`Indexer nicht erreichbar`

Supporting text explains that new searches and automatic updates may fail.

Primary action:

`Integration prüfen`

Visual treatment:

- pale amber surface;
- amber warning icon;
- restrained action.

The mockup may show close icons to demonstrate the component affordance; actual rendering is governed by the Banner's dismiss/snooze policy.

The media page underneath remains fully usable and demonstrates that Banners push content down rather than covering it.

## Mockup file

Upload the approved reference as:

`docs/mockups/notifications-banner/image.png`

The image is a visual reference only. This specification remains the binding behavioral contract.
