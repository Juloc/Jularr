# Notification Quiet Hours — Target UX

Status: approved planning direction for the finished Jularr notification experience. The approved mockup uploaded to this folder is a visual reference; this text remains binding.

Global UX rules: `docs/UX.md`.
Notification Settings: `docs/mockups/notifications-settings/SPEC.md`.
Notification Topic Editor: `docs/mockups/notifications-topic-editor/SPEC.md`.
Notification Center: `docs/mockups/notifications-center/SPEC.md`.
Toast / Popup: `docs/mockups/notifications-toast-popup/SPEC.md`.

If an image and this specification conflict, this specification wins.

## Purpose

Quiet Hours let one profile define a recurring daily period during which **delayable notification deliveries should wait**.

The feature must answer:

1. Are Quiet Hours active?
2. When do they start?
3. When do they end?
4. Which timezone applies?
5. Which important notifications may still break through?

Quiet Hours do not disable notifications and do not hide durable inbox history.

## Navigation

Entry:

`Profile → Settings → Notifications → Zustellung → Ruhezeiten`

The Notification Settings landing row shows a compact summary, for example:

`Ruhezeiten · 22:00–08:00`

When disabled:

`Ruhezeiten · Aus`

Desktop:

- open as a focused right-side sheet/dialog;
- keep Notification Settings visible behind it.

Mobile:

- open as a dedicated full-height settings screen/sheet;
- normal back navigation returns to Notification Settings.

Do not create a separate permanent settings destination.

## Header

Contains:

- contextual back/close;
- title `Ruhezeiten`;
- concise explanation:
  `Nicht dringende Benachrichtigungen werden in diesem Zeitraum zurückgehalten.`

No Save button.

## Main controls

Order:

1. `Ruhezeiten aktivieren` toggle;
2. `Von` time;
3. `Bis` time;
4. `Zeitzone`;
5. important-notification explanation;
6. unobtrusive auto-save status.

This order matches the user's mental model:

`Enable → define interval → confirm timezone → understand exceptions`

## Enable / disable behavior

### Enabled

The configured recurring interval becomes active immediately after successful persistence.

### Disabled

- no delivery is delayed by Quiet Hours;
- stored start/end/timezone values remain preserved;
- re-enabling restores the prior configuration.

Do not erase the user's schedule when the toggle is turned off.

## Time range

The schedule is a recurring daily local-time range.

Example:

`22:00 → 08:00`

must be interpreted as crossing midnight.

### Same-day range

Example:

`13:00 → 15:00`

covers that same local day period.

### Cross-midnight range

Example:

`22:00 → 08:00`

covers:

- 22:00 until midnight;
- midnight until 08:00.

The implementation must not require the user to manually specify `next day`.

## Invalid range

Start and end must not represent an ambiguous 24-hour mute.

If start equals end:

- do not interpret this silently as 24 hours;
- reject the invalid value or keep the previous valid value;
- show concise local validation.

If a future product requirement needs all-day muting, it should be an explicit separate setting rather than overloading equal times.

## Time picking

Desktop:

- selecting `Von` or `Bis` opens the shared time picker/control.

Mobile:

- use the platform/native-feeling time picker or shared Jularr sheet.

Do not expose raw UTC values.

Displayed time follows the profile's locale formatting while the stored schedule remains timezone-aware.

## Timezone

Default timezone comes from the profile's Language & Region settings.

The editor shows the active canonical timezone, for example:

`Europe/Berlin`

Use an IANA/real timezone identity or the platform's canonical equivalent, not a fixed UTC offset.

DST/summer-time transitions must follow timezone rules automatically.

### Timezone changes

When the profile timezone changes:

- the Quiet Hours clock values remain the user's local intended times;
- future scheduling is recalculated in the new timezone.

Example:

`22:00–08:00` remains `22:00–08:00` local time after moving timezone.

Do not preserve the old absolute UTC interval unless the user explicitly configured a separate timezone override.

### Explicit override

A separate Quiet Hours timezone override may exist only if there is a real product need.

The approved target uses the profile timezone directly.

Do not add a second timezone setting unnecessarily.

## What Quiet Hours delay

Quiet Hours apply to **delivery attention**, not durable event creation.

For an eligible event during Quiet Hours:

- Notification Center item may be created immediately;
- unread Bell count may increase immediately;
- transient In-App Toast is suppressed;
- Push is delayed when policy allows;
- Immediate E-Mail is delayed when policy allows;
- Digest behavior follows its own configured schedule.

This keeps the inbox truthful while reducing interruption.

## What happens after Quiet Hours

When the interval ends:

- eligible delayed Push/E-Mail deliveries may be released;
- do not replay every suppressed In-App Toast one after another;
- Notification Center already contains the durable history;
- burst/dedup policy may combine related delayed external deliveries.

The end of Quiet Hours must not create a notification storm.

## Critical / required exceptions

Canonical event policy owns whether an event:

- may be delayed;
- must bypass Quiet Hours;
- may use Digest;
- is mandatory.

Typical examples:

### Delayable

- new episode/release;
- Learning reminder;
- request status;
- routine import completion.

### Possible bypass

- critical account/security event;
- severe infrastructure problem for entitled Admin profiles;
- event whose product policy explicitly requires immediate attention.

The user-facing editor explains this simply:

`Wichtige Benachrichtigungen werden weiterhin zugestellt.`

Supporting text may clarify that security/system notifications can still arrive immediately.

Do not expose a complex per-event bypass matrix on this page.

## Relationship to Topic Editor

Topic Editor decides:

- whether an event is enabled;
- which channels are selected;
- Immediate vs Digest.

Quiet Hours then applies to eligible Immediate delivery.

Example:

`Neue Releases → Push → Sofort`

during Quiet Hours:

- event remains enabled;
- Push is delayed until Quiet Hours end;
- inbox item may appear immediately if In-App is also selected.

Quiet Hours must not silently change the event's saved timing mode from Immediate to Digest.

## Relationship to Digest

Quiet Hours and Digest are separate concepts.

Digest:

- batches events for a scheduled summary.

Quiet Hours:

- temporarily delays eligible immediate delivery.

If a Digest is scheduled inside Quiet Hours, the canonical scheduler must define one consistent rule.

Recommended target:

- delay the Digest until Quiet Hours end unless the user later receives an explicit option to allow Digests during Quiet Hours.

Do not deliver the same events twice because a delayed Immediate delivery and Digest overlap.

## Foreground behavior

If Jularr is open during Quiet Hours:

- delayable event Toasts are suppressed;
- Bell/unread state remains current;
- Notification Center remains current;
- current page may update normally if the event affects visible state.

Quiet Hours must not freeze application state updates.

## Immersive behavior

Player/Reader/Game suppression rules still apply independently.

If an event occurs during both:

- Quiet Hours;
- immersive mode;

it should still be delivered at most once according to the strongest applicable suppression/delay rules.

Do not queue duplicate transient presentation states.

## Auto-save

Changes persist immediately:

- enabled toggle;
- start time;
- end time;
- timezone when editable.

Show:

`Automatisch gespeichert`

as a quiet status.

On failure:

- keep/revert controls to persisted truth;
- show a local error;
- do not claim success.

No full-page Save button.

## Scheduler architecture

Quiet Hours require one canonical scheduler/policy boundary.

Target flow:

`Eligible delivery → profile Quiet Hours policy → deliver now OR defer until next allowed time`

The scheduler must:

- calculate next allowed delivery using the profile timezone;
- handle cross-midnight intervals;
- handle DST changes correctly;
- preserve deduplication;
- avoid replay storms;
- survive restart;
- not rely on an in-memory timer as the sole source of deferred delivery truth.

Deferred durable delivery must be restart-safe.

## Persistence ownership

The canonical profile notification settings owner stores:

- Quiet Hours enabled;
- local start time;
- local end time;
- timezone source/identity as required.

Do not create a page-specific Quiet Hours store.

The delivery scheduler consumes this canonical configuration.

## Queue / deferred delivery

For channels requiring later delivery, the system needs durable deferred-delivery state or an equivalent restart-safe scheduling contract.

Rules:

- deferred items remain attributable to the original canonical event;
- deduplication still applies;
- disabling a channel before release prevents that channel delivery;
- disabling the event before release follows canonical policy for pending delivery cancellation;
- profile deletion/logout/device revocation must not leak pending deliveries.

Do not copy full event truth into an unrelated scheduling database when canonical identifiers are sufficient.

## Settings changes during Quiet Hours

### User disables Quiet Hours

Pending delayable deliveries become eligible immediately, subject to rate/burst control.

Do not release hundreds of individual messages without aggregation safeguards.

### User changes end time

Recalculate pending next-delivery times.

### User changes timezone

Recalculate against the new local schedule.

### User disables an event/channel

Pending deliveries for that disabled route must not be sent afterward.

These consequences must be handled by the canonical scheduler, not UI code.

## Desktop layout

Approved direction:

- Notification Settings page remains visible on the left;
- Quiet Hours editor opens on the right;
- clean single-column editor;
- one setting card/group;
- `Von`, `Bis`, `Zeitzone` as simple rows;
- important-notification explanatory callout beneath;
- auto-save status near the bottom.

No timeline visualization or calendar complexity.

## Mobile layout

Approved direction:

- full-height single-column editor;
- title and description at top;
- enable toggle first;
- large touch-friendly time rows;
- timezone row;
- concise information callout;
- auto-save status near bottom;
- no squeezed Desktop sheet.

## Accessibility

- enable switch has explicit accessible label;
- time controls expose readable values;
- timezone is announced by its user-facing name;
- validation is associated with the relevant control;
- explanatory critical-bypass text is available to screen readers;
- state does not rely on purple/gray color alone;
- all controls meet touch target requirements.

## Localization

All visible labels and explanations use Jularr localization resources.

Time display follows profile locale.

Timezone identity may use canonical timezone naming where localization is not appropriate.

Stored schedule identity remains language-neutral.

## Visual contract

Use Jularr Clean Light Mode.

Approved direction:

- white/near-white editor surface;
- dark navy text;
- Jularr purple for active toggle/selection;
- simple row separators;
- restrained border/radius;
- one pale informational callout;
- no decorative graphics;
- no gradients/glow;
- no dashboard widgets;
- no unnecessary nested cards.

## Approved mockup composition

The approved reference shows:

- Desktop Notification Settings on the left;
- Quiet Hours editor opened as a right-side sheet;
- Mobile version of the same editor;
- Quiet Hours enabled;
- `Von 22:00`;
- `Bis 08:00`;
- `Europe/Berlin`;
- informational callout that important notifications may still be delivered;
- `Automatisch gespeichert`;
- Notification Settings parent row summarizing `22:00–08:00`.

The image is a visual reference only. This specification remains the behavioral contract.

## Mockup file

Upload the approved visual reference as:

`docs/mockups/notifications-quiet-hours/image.png`
