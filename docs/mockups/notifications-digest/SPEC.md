# Notification Digest — Target UX

Status: approved planning direction for the finished Jularr notification experience. The approved mockup uploaded to this folder is a visual reference; this text remains binding.

Global UX rules: `docs/UX.md`.
Notification Settings: `docs/mockups/notifications-settings/SPEC.md`.
Notification Topic Editor: `docs/mockups/notifications-topic-editor/SPEC.md`.
Quiet Hours: `docs/mockups/notifications-quiet-hours/SPEC.md`.
Notification Center: `docs/mockups/notifications-center/SPEC.md`.

Canonical notification architecture: `docs/NOTIFICATIONS.md`.

If an image and this specification conflict, this specification wins.

## Purpose

Digest controls **when and through which supported channels batched notifications are delivered**.

It does not decide which events belong in Digest. That remains an event-level choice in the Notification Topic Editor.

The page answers:

1. Is Digest enabled?
2. How often should it run?
3. At what local time?
4. Through which Digest-capable channels?
5. Which timezone applies?
6. What happens when Quiet Hours overlap the scheduled delivery?
7. What happens to existing Digest event preferences if Digest is disabled?

## Navigation

Entry:

`Profile → Settings → Notifications → Zustellung → Digest`

Parent summary examples:

`Digest · Täglich, 19:00 · E-Mail`

or:

`Digest · Aus`

Desktop:

- open as a focused right-side sheet/dialog;
- keep Notification Settings visible behind it.

Mobile:

- use a full-height settings screen/sheet;
- normal back navigation returns to Notification Settings.

Do not create a separate permanent navigation destination.

## Header

Contains:

- contextual back/close;
- title `Digest`;
- concise description:
  `Fasse ausgewählte Benachrichtigungen zu einer regelmäßigen Zusammenfassung zusammen.`

No Save button.

## Main control order

The approved hierarchy is:

1. `Digest aktivieren`;
2. `Rhythmus`;
3. `Uhrzeit`;
4. `Zustellung`;
5. `Zeitzone`;
6. Quiet Hours consequence;
7. count/status of event types using Digest;
8. auto-save status.

This follows the user's mental model:

`Enable → how often → when → where → timezone → consequences`

## Digest enable state

### Enabled

Digest scheduling becomes active after valid configuration persists.

At least one Digest-capable active channel must be selected.

### Disabled

Digest scheduling stops.

The user must not be left with event types silently configured for a delivery mode that can no longer run.

If any event types currently use Digest, disabling requires an explicit resolution flow.

## Disable confirmation flow

When Digest is turned off while one or more event types use Digest, open a focused confirmation sheet/dialog.

Example:

`Digest deaktivieren?`

`6 Benachrichtigungstypen verwenden aktuell Digest. Was soll mit diesen Benachrichtigungen geschehen?`

Required choices:

### Recommended: Switch to Immediate

`Auf Sofort umstellen`

- all affected event preferences remain enabled;
- timing changes from Digest to Immediate;
- selected compatible channels remain where possible;
- canonical policy validates the resulting routes.

This is the recommended action.

### Disable those event types

`Diese Benachrichtigungen deaktivieren`

- affected event types become disabled;
- existing Notification Center history remains unchanged.

### Cancel

Leaves Digest enabled and makes no changes.

No silent fallback is allowed.

The user must understand the consequence before Digest is removed from active use.

## Rhythm

Target options shown in the approved mockup:

- Daily;
- Weekly;
- Specific weekdays.

The scheduler may initially implement only supported choices, but the UX contract permits these target modes.

### Daily

Runs once each local day.

### Weekly

Runs once on a selected weekday.

### Specific weekdays

Allows selecting one or more weekdays.

Do not support arbitrary cron-style schedules in the user UI.

If a cadence is unsupported by the current backend, do not render it as selectable.

## Time

The Digest delivery time is a profile-local clock time.

Example:

`19:00`

Use the shared Jularr time picker / platform-appropriate control.

Do not expose UTC.

Changing the delivery time recalculates the next scheduled run.

## Delivery channels

Only channels that explicitly support Digest may be selected.

Target examples:

- E-Mail;
- Push, if/when a real Push Digest delivery contract exists.

Digest is not itself a transport.

### Channel availability

A channel is selectable only when:

- Admin has enabled/configured the transport;
- the profile can use it;
- the transport supports Digest;
- required account/device state exists.

Unavailable channels may remain visible if useful to explain current configuration.

Example from approved mockup:

`Push-Digest ist aktuell nicht verfügbar.`

Do not make unavailable channels appear active or selectable.

## Minimum channel rule

When Digest is enabled, at least one valid Digest-capable channel is required.

If the user attempts to deselect the final valid channel:

- keep the final channel selected;
- show concise local explanation;
- do not leave Digest in an enabled-but-undeliverable state.

If all channels become unavailable due to external/admin changes, show a clear blocked state and stop scheduled delivery until resolved.

## Topic relationship

The Digest page does **not** choose event categories.

Topic Editor owns event-level timing:

`Neue Releases → Digest`

The Digest page owns:

`Digest → täglich → 19:00 → E-Mail`

This separation is binding.

The approved page shows a status summary such as:

`6 Benachrichtigungstypen verwenden Digest`

and a link:

`Benachrichtigungsthemen bearbeiten`

That link opens the Notification Topic settings, not another duplicate event selector.

## No event types using Digest

Digest may be configured even when no event currently uses it.

Show a quiet informational state:

`Noch keine Benachrichtigung verwendet Digest.`

Provide:

`Benachrichtigungsthemen bearbeiten`

Do not create fake sample Digest content.

## Digest contents

A Digest should summarize and group events meaningfully rather than outputting a raw chronological dump.

Typical groups:

- New media;
- Requests;
- Learning;
- Social;
- other eligible product domains.

Example:

`Neue Medien`
- 3 neue Episoden;
- 2 neue Kapitel.

`Anfragen`
- 1 genehmigt.

`Lernen`
- 12 Reviews fällig.

Canonical event deduplication/grouping remains authoritative.

Do not duplicate the same underlying event multiple times inside one Digest.

## Empty Digest

If no eligible Digest events accumulated since the last successful Digest:

- do not send an empty Digest;
- do not generate a `Keine Neuigkeiten` message by default;
- advance scheduling normally.

## Critical / mandatory events

Canonical event policy decides whether an event may use Digest.

Critical/security events that require immediate delivery must not be selectable as Digest.

Do not allow Digest to delay mandatory immediate security/system notifications.

## Quiet Hours interaction

Quiet Hours and Digest are separate features.

If the scheduled Digest time falls inside Quiet Hours, the approved target behavior is:

- postpone Digest delivery until Quiet Hours end;
- keep the configured Digest clock time unchanged;
- show the effective consequence in the Digest editor.

Example from approved mockup:

`Dieser Digest wird nach Ende deiner Ruhezeiten um 08:00 zugestellt.`

Supporting text may show:

`Deine Ruhezeiten sind von 22:00–08:00.`

This is not an error.

Do not create a second hidden delivery-time setting.

## Timezone

Default timezone comes from the profile's Language & Region settings.

Example:

`Europe/Berlin (automatisch)`

Use a real timezone identity, not a fixed UTC offset.

DST must be handled automatically.

When profile timezone changes:

- the configured local clock time remains the same;
- next run is recalculated in the new timezone.

Example:

`19:00` remains local `19:00`.

## Auto-save

All ordinary reversible changes persist immediately:

- enable state when no destructive consequence exists;
- cadence;
- time;
- channel selection;
- weekday selection where relevant.

Show a quiet:

`Automatisch gespeichert`

status.

Do not show a success toast after every small change.

If persistence fails:

- controls reflect persisted truth;
- show a local error;
- do not show saved status.

## Delivery lifecycle

Target flow:

`Eligible event → profile event preference = Digest → pending Digest bucket → scheduled Digest run → channel delivery → mark included items processed`

Pending Digest state must be durable/restart-safe.

Do not rely on in-memory timers or queues as the only source of truth.

## Restart safety

The scheduler must preserve:

- next intended Digest execution;
- pending eligible event references;
- last successful Digest execution;
- delivery attempt state;
- profile/channel target.

After restart:

- no Digest is silently lost;
- the same Digest is not sent twice;
- overdue scheduled work is handled according to one canonical recovery policy.

## Delivery success

After a Digest is successfully delivered through the intended channels:

- included pending items are marked processed for that Digest route;
- they are not included again in the next Digest unless a new occurrence arrives;
- durable Notification Center items remain independent.

Digest processing must not delete Notification Center history.

## Delivery failure

If Digest delivery fails:

- pending events are not lost;
- retry follows the shared delivery retry policy;
- repeated retries must not duplicate already-successful channel deliveries;
- Admin delivery diagnostics may expose the failure;
- persistent user-facing channel failure may surface through the appropriate notification/banner policy.

Do not silently mark a failed Digest successful.

## Multiple channels

If a Digest is configured for multiple channels:

- each route has independent delivery result;
- success on one channel must not force duplicate resend on that already-successful route;
- retry only failed routes where possible;
- overall Digest occurrence keeps one canonical identity.

## Settings changes while events are pending

### Channel disabled

Pending delivery for that channel must not be sent afterward.

Other valid selected routes remain active.

### Event switched from Digest to Immediate

Canonical policy decides whether already-pending Digest occurrences remain in the upcoming Digest or are migrated to Immediate.

Recommended target:

- already-captured occurrences remain in the current pending Digest;
- future occurrences follow Immediate.

This prevents unexpected duplicate immediate replay.

### Event disabled

Future occurrences stop entering Digest.

Pending occurrences may remain in the already-built bucket unless explicit product policy says otherwise.

### Digest time changed

Recalculate the next scheduled run.

### Digest disabled

Use the explicit disable-resolution flow above.

## Burst and grouping

Digest naturally absorbs bursts.

A large number of related events should be grouped using canonical product/domain grouping rather than emitted as hundreds of list entries.

Example:

12 episode releases from one series may become:

`12 neue Episoden von [Serie]`

Do not lose deep-link/action context when grouped.

## User action from Digest

A Digest item may link back into Jularr.

Examples:

- Play/Open media;
- Open request;
- Start Learning review.

Links must use canonical target identities/deep links and authorization.

Opening an external Digest item may mark the related durable In-App notification read only if the canonical cross-channel read policy explicitly supports it.

Do not assume E-Mail click == inbox read without a deliberate contract.

## Admin relationship

Admin Notifications owns:

- channel transport configuration;
- health;
- delivery diagnostics.

User Digest settings own:

- personal cadence;
- selected available Digest-capable channels.

The user page must never expose transport credentials, SMTP configuration, provider secrets or delivery logs.

## Desktop layout

Approved direction:

- Notification Settings remains visible on the left;
- Digest editor opens as a wide right-side sheet;
- main controls stack vertically;
- rhythm uses simple radio rows;
- delivery channel selectors are compact;
- timezone is one simple row;
- Quiet Hours consequence is a pale informational callout;
- Digest usage count and edit-topics link appear near the bottom;
- auto-save status is subtle.

No dense matrix or timetable grid.

## Mobile layout

Approved direction:

- full-height single-column editor;
- enable toggle at top;
- cadence choices as touch-friendly rows;
- time row;
- channel rows;
- timezone row;
- Quiet Hours callout;
- no squeezed Desktop layout.

The approved composite additionally shows a Mobile disable-confirmation sheet to document the consequence flow.

## Accessibility

- enable toggle has explicit label;
- cadence uses correct radio/select semantics;
- channel controls expose selected/disabled state;
- unavailable channel explanations are associated with the disabled option;
- time and timezone values are readable to assistive technology;
- confirmation sheet traps/restores focus appropriately;
- destructive disable option is clearly distinguished in text, not color alone;
- touch targets meet platform size requirements.

## Localization

All visible labels, descriptions, channel names, cadence names, confirmation copy and errors use Jularr localization resources.

Stored schedule/channel/event identities remain language-neutral.

Time display follows profile locale.

## Architecture ownership

Binding ownership:

- canonical event catalog/policy owns Digest eligibility;
- profile notification preference owner stores per-event timing choice;
- profile Digest settings owner stores cadence/time/channels;
- scheduler owns pending Digest buckets and next run;
- transport sinks own actual E-Mail/Push delivery;
- Admin Notifications owns transport availability/health;
- Topic Editor owns event-level Immediate/Digest selection;
- Digest Editor only configures the shared Digest schedule/delivery route.

Do not create a second event subscription model inside Digest.

## Target data model

The canonical profile Digest configuration needs, at minimum:

- enabled;
- cadence type;
- selected weekday(s) where applicable;
- local delivery time;
- timezone source/identity;
- selected Digest-capable channels;
- scheduling metadata required for restart-safe execution.

Pending Digest state must reference canonical events/deliveries without duplicating unnecessary event truth.

## Visual contract

Use Jularr Clean Light Mode.

Approved direction:

- white/near-white editor surface;
- dark navy text;
- Jularr purple for active toggle/radio/selection;
- simple row/control borders;
- restrained radius/shadow;
- pale blue/purple informational callout for Quiet Hours interaction;
- green saved status may be used subtly;
- no gradients/glow;
- no generic dashboard widgets;
- no excessive nested cards.

## Approved mockup composition

The approved reference shows a composite of:

### Desktop

- Jularr Notification Settings page;
- Digest row selected;
- large right-side Digest editor;
- Digest enabled;
- rhythm options:
  - Täglich selected;
  - Wöchentlich;
  - An bestimmten Wochentagen;
- time `19:00`;
- E-Mail selected;
- Push visible but unavailable with explanation;
- timezone `Europe/Berlin (automatisch)`;
- Quiet Hours consequence stating delivery will occur at `08:00`;
- summary `6 Benachrichtigungstypen verwenden Digest`;
- link to edit notification topics;
- auto-save confirmation.

### Mobile

Primary editor mirrors the same configuration in a single-column layout.

A second Mobile reference shows:

`Digest deaktivieren?`

with:

- `Auf Sofort umstellen` as the recommended safe option;
- `Diese Benachrichtigungen deaktivieren`;
- `Abbrechen`;
- final destructive confirmation.

The image is a visual reference only. This specification remains the behavioral contract.

## Mockup file

Upload the approved visual reference as:

`docs/mockups/notifications-digest/image.png`
