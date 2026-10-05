# Notification Toast / Popup — Target UX

Status: approved planning direction for the finished Jularr notification experience. The approved mockup uploaded to this folder is a visual reference; this text remains binding.

Global UX rules: `docs/UX.md`.
Notification Center: `docs/mockups/notifications-center/SPEC.md`.
Bell / Quick View: `docs/mockups/notifications-bell/SPEC.md`.
User notification preferences: `docs/mockups/notifications-settings/SPEC.md`.

Canonical notification architecture: `docs/NOTIFICATIONS.md`.

If an image and this specification conflict, this specification wins.

## Purpose

Toast / Popup Notifications are Jularr's short-lived **attention surface** for events that deserve immediate visibility while the application is open.

They answer:

1. What just happened?
2. Does it need my attention now?
3. Is there one obvious next action?

They are not:

- the durable notification inbox;
- a replacement for the Notification Center;
- a permanent critical-state banner;
- a job/activity feed;
- a generic place for every success/error message;
- a second notification persistence model.

## Two distinct toast families

Jularr must keep these concepts separate.

### Event notification toast

Represents a canonical Jularr event already routed through the notification system.

Examples:

- new episode available;
- request approved;
- review reminder;
- import failed;
- account/security event.

If the event is configured for durable In-App delivery, the Notification Center remains the canonical discoverable record.

Closing the toast does not remove or read the inbox item.

### Action feedback toast

Represents the immediate result of a local user action.

Examples:

- settings saved;
- item removed from Watchlist;
- notification removed;
- reversible action with Undo.

Action feedback:

- does not create a Notification Center item;
- does not use event subscription rules;
- should stay visually simpler;
- must not become a second event pipeline.

The current generic `TempData["Status"]` toast behavior is an action-feedback baseline, not the target implementation for canonical event popups.

## Delivery relationship

For a canonical event, the target flow is:

`Domain event → canonical event policy → profile subscription → durable In-App inbox when enabled → attention policy → Toast / Banner / None`

Toast appearance is a presentation decision.

The Toast must never be the only persisted evidence that an In-App notification occurred.

## Attention policy

Not every delivered notification should create a popup.

The canonical event/presentation policy should decide whether an event is eligible for:

- no transient attention;
- toast;
- persistent banner.

Factors may include:

- event category;
- severity;
- user notification preferences;
- active page/context;
- immersive mode;
- application foreground/background state;
- deduplication;
- Quiet Hours;
- recent duplicate exposure.

Do not let individual Razor Pages invent independent popup rules.

## Default attention guidance

Typical target behavior:

| Event | Toast guidance |
| --- | --- |
| New playable/readable release | Yes |
| Request approved/denied | Yes |
| Learning review reminder | Yes when reminder policy allows |
| User-visible import completed | Optional / policy-driven |
| Routine background completion | Usually no |
| Minor social activity | Usually no |
| Import/download failure | Yes |
| Account/security event | Yes |
| Critical persistent system condition | Banner, optionally initial toast |

These are product defaults, not hard-coded page rules.

## User preference

Notification settings should expose one simple profile-level preference for transient In-App attention:

`Popups anzeigen, während Jularr geöffnet ist`

Do not add a separate popup toggle to every individual event unless future evidence proves users need that complexity.

Topic subscriptions still control which events the user receives.

Critical mandatory policy may override popup suppression only where explicitly defined by canonical security/system policy.

## Foreground / background behavior

### Jularr in foreground

When an eligible event arrives:

- show Jularr In-App toast;
- avoid sending a duplicate foreground OS notification for the same delivery when the platform allows suppression.

### Jularr in background or closed

Use the configured system Push channel where available.

Do not queue old foreground-style toasts merely because the user later opens Jularr.

The durable Notification Center item remains the common history when In-App delivery is enabled.

## Context-aware suppression

A popup should not interrupt the user with a redundant action when the user is already looking at the exact target state.

Example:

- Request detail is open;
- that same request becomes approved.

Preferred behavior:

- update the current page/state;
- optionally show minimal action feedback;
- still persist the canonical inbox item when policy requires it;
- do not show a redundant `Open request` toast.

Suppression must compare canonical subject/target identity, not rendered text.

## Immersive mode

Normal informational toasts should be suppressed or deferred while the user is in an immersive surface such as:

- video playback;
- fullscreen reader;
- manga reading mode;
- game/player mode.

After leaving immersive mode:

- unread badge/Notification Center remains accurate;
- old informational popups do not all replay as a burst.

Critical/security alerts may still surface when policy requires immediate awareness, but must respect safe areas and avoid covering primary playback/reader controls where possible.

## Desktop placement

The approved target places transient notification toasts in the **upper-right application area**, visually below/near the global top bar and Bell.

Rules:

- do not center them as modal dialogs;
- do not obscure the full page;
- do not dim the application;
- preserve the current user context;
- respect shell safe spacing so Bell/account controls remain usable.

The stack aligns consistently to the right edge of the main application content.

## Approved visual stack

The approved reference may show multiple representative toast states at once to document the component family.

This is a design reference, not a requirement that four independent toasts normally remain on-screen simultaneously.

Runtime target:

- show at most **two full toast cards** simultaneously;
- additional eligible items wait in the queue;
- a compact `+N weitere Benachrichtigungen` affordance may summarize queued items;
- selecting that affordance may reveal/advance queued toasts or open the Notification Center according to the final implementation pattern.

Avoid tall stacks that cover substantial application content.

## Event toast anatomy

A standard event toast contains:

1. exactly one visual anchor;
2. title;
3. concise subject/context;
4. optional compact time;
5. maximum one primary quick action;
6. close control.

### One visual anchor rule

Use either:

- media artwork/cover when naturally tied to media;

or:

- semantic icon for Learning, Request, Warning, Security, System, etc.

Do not show artwork plus a second category icon plus decorative imagery.

## Text hierarchy

Preferred density:

`Title`
`Subject · context`
`Optional short time/status`

Examples:

`Neue Episode verfügbar`
`Solo Leveling · Staffel 2 · Episode 8`
`vor wenigen Sekunden`

`Import fehlgeschlagen`
`Mushoku Tensei · Staffel 2`
`3×`

Avoid long diagnostic copy.

Detailed diagnostics belong in the target details screen.

## Primary quick action

Use the same canonical action-resolution rules as Notification Center and Bell.

Examples:

| Event | Quick action |
| --- | --- |
| Playable episode/movie | Play |
| Readable chapter/book | Read |
| Audiobook | Play |
| Learning review | Start |
| Request update | Open |
| Import failure | Details |
| System problem | Details |

Rules:

- maximum one primary action;
- action must be valid for the actual subject;
- no dead/disabled button;
- no secondary button row;
- no per-toast overflow menu.

The approved Desktop reference uses compact icon-first/circular actions where possible.

## Toast body click

The non-control body may open the canonical notification target when one exists.

Example:

- clicking episode artwork/text → media/episode detail;
- clicking Play → direct playback.

These are distinct actions.

If no canonical target exists, the body should not invent one.

## Read-state consequence

Closing or timing out a toast:

- does not mark the durable Notification Center item read.

Opening the canonical target through the toast:

- follows the same read semantics as opening the item from Bell/Center.

A failed target action must not falsely claim the notification was handled.

## Close behavior

Each event toast has an explicit close affordance.

Closing means:

- dismiss the transient popup;
- preserve durable inbox state;
- continue processing queued transient items.

Close must not alter the user's subscription.

Do not use close as a hidden `disable this notification type` action.

## Auto-dismiss timing

Default guidance:

- Info: approximately 6 seconds;
- Warning: approximately 8–10 seconds;
- Critical: does not rely on a short auto-dismiss if immediate sustained action is required.

Exact durations may be tuned globally through the shared component, not per page.

Pause the dismiss timer while:

- pointer is over the toast;
- keyboard focus is inside the toast;
- accessibility interaction requires additional time.

Respect reduced-motion and accessibility needs.

## Warning / Critical behavior

### Warning

May use:

- warning semantic icon;
- restrained warning accent;
- longer display duration.

### Critical

If the condition is transient but urgent, a persistent/long-lived toast may be acceptable.

If the condition remains true and requires ongoing corrective action, use the persistent Banner surface instead.

Example:

- `Import fehlgeschlagen` → Toast;
- `Speicher voll – neue Imports blockiert` → Banner + inbox event.

Banner rules are specified separately in:

`docs/mockups/notifications-banner/SPEC.md`

## Deduplication and burst control

The transient layer must reuse canonical event deduplication identity where available.

Repeated events with the same underlying identity must not produce popup spam.

Possible outcomes:

- update an existing visible toast;
- increment occurrence count such as `3×`;
- queue only one consolidated item.

Example:

Five repeated import failures for the same underlying operation should not produce five independent popup cards.

## Multiple related media events

When several related media items arrive as a burst, the notification policy may aggregate them.

Example:

`5 neue Episoden verfügbar`

Aggregation must be based on canonical subject/group identity and product policy.

Do not aggregate unrelated events merely to reduce UI load.

## Queue behavior

The toast presenter owns an in-memory transient queue.

Rules:

- queue is presentation-only;
- durable notification state remains in NotificationStore;
- only events eligible for transient attention enter the queue;
- queue ordering follows event arrival / LastOccurredAt;
- deduplication may update queued/visible entries;
- dismissed/timed-out items leave the transient queue only.

Refreshing/restarting the app does not need to replay old transient toasts because the Notification Center remains durable.

## Action feedback toast

Action feedback uses a simpler component.

Examples:

`✓ Einstellungen gespeichert`

`Aus Bibliothek entfernt    Rückgängig`

Rules:

- no artwork;
- no notification unread dot;
- no Notification Center record;
- concise;
- generally shorter duration;
- optional one Undo action.

Errors tied to a specific form field remain inline next to that field rather than being moved into a global toast.

Global operation failures may use action feedback toast when no better local error surface exists.

## Undo

Undo is allowed only for an action whose reversal is safe and already has a canonical backend operation.

Examples:

- notification removed from inbox;
- removable user-list state.

Do not fake Undo visually if the backend cannot actually reverse the operation.

Undo timing should match the feedback toast lifetime.

## Mobile behavior

When the Jularr web/PWA/app is in the foreground on a phone:

- use a compact top in-app banner/toast;
- one column;
- artwork/icon on the left;
- title/context in the middle;
- optional compact quick action;
- swipe/dismiss affordance where platform conventions support it;
- >=44 px interactive targets.

Do not shrink the Desktop toast stack onto mobile.

Only one foreground in-app toast should normally be shown at a time on phone.

When the app is not active, rely on system Push rather than a hidden Jularr toast queue.

A dedicated Mobile mockup is optional; the approved primary reference is Desktop.

## Accessibility

Transient notifications must:

- use appropriate live-region semantics;
- not repeatedly interrupt screen readers for low-importance bulk events;
- expose title/context/action clearly;
- preserve keyboard focus unless the user explicitly interacts with the toast;
- never auto-focus a newly arriving toast;
- pause timeout when keyboard focus enters;
- provide visible focus for close/action controls;
- communicate severity through icon/text, not color alone.

Action feedback success should generally use polite announcement.

Critical alerts may use more assertive announcement only when genuinely necessary.

## Localization

All displayed text uses Jularr localization resources.

Stored event identity remains canonical event key + parameters.

Time text follows profile locale/timezone.

Do not persist rendered localized toast content as the source of truth.

## Architecture ownership

Target flow:

`JularrEvent → event policy → profile subscription → durable delivery → NotificationAttentionPolicy → ToastPresenter / BannerPresenter / None`

Owners:

- domain feature owns event emission;
- event catalog/policy owns severity/audience/attention capability;
- NotificationStore owns durable inbox state;
- NotificationAttentionPolicy owns whether transient attention is appropriate;
- ToastPresenter owns transient queue and rendering state;
- Notification Center owns durable inbox management;
- Banner presenter owns persistent critical state surface;
- Settings owns user popup preference;
- target feature owns deep link / quick action destination.

Do not create page-specific toast queues or a second notification database.

## Current implementation migration note

Jularr currently has a shared layout toast for `TempData["Status"]`.

That existing mechanism should evolve as **action feedback**, not be overloaded into the canonical event notification system.

The finished implementation should introduce/reuse one shared toast component/presenter that can render:

- simple action feedback;
- canonical event notification toast variants;

while keeping their persistence semantics separate.

Do not leave multiple competing toast implementations after migration.

## Visual contract

Use Jularr Clean Light Mode.

Approved direction:

- normal Jularr consumer page remains fully visible;
- transient stack sits in the upper-right;
- white/near-white cards;
- subtle neutral border;
- restrained shadow;
- medium Jularr radius;
- dark navy text;
- Jularr purple for primary interaction/unread emphasis;
- media artwork only when meaningful;
- semantic warning colors only for warning/error state;
- no gradients/glow in the toast component;
- no giant text;
- no dashboard chrome;
- no per-toast menus.

## Approved mockup composition

The approved reference is a single Desktop 16:9 Light Mode Jularr consumer screen.

Background:

- normal Jularr Home/Dashboard-style media surface;
- sidebar, top search, Bell and profile controls visible;
- hero/media shelves may remain visible to show that toasts preserve current context.

Upper-right toast presentation:

1. **New episode**
   - media artwork;
   - `Neue Episode verfügbar`;
   - `Solo Leveling · Staffel 2 · Episode 8`;
   - recent time;
   - circular Play action;
   - close control.

2. **Import completed**
   - media artwork;
   - title/context;
   - Open quick action;
   - close control.

3. **Review reminder**
   - Learning semantic icon;
   - `Review-Erinnerung`;
   - concise due-item text;
   - Start quick action;
   - close control.

4. **Import failed**
   - Warning semantic icon;
   - `Import fehlgeschlagen`;
   - media/context;
   - occurrence count such as `3×`;
   - Details action;
   - close control.

5. Compact queue summary:
   - `+2 weitere Benachrichtigungen`.

The reference intentionally displays several component variants together for design documentation. Runtime still follows the bounded visible-stack rules above.

## Mockup file

Upload the approved reference as:

`docs/mockups/notifications-toast-popup/image.png`

The image is a visual reference only. This specification remains the binding behavioral contract.
