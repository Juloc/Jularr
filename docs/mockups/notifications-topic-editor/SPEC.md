# Notification Topic Editor — Target UX

Status: approved planning direction for the finished Jularr notification experience. The approved mockup uploaded to this folder is a visual reference; this text remains binding.

Global UX rules: `docs/UX.md`.
Notification Settings: `docs/mockups/notifications-settings/SPEC.md`.
Notification Center: `docs/mockups/notifications-center/SPEC.md`.
Bell / Quick View: `docs/mockups/notifications-bell/SPEC.md`.
Toast / Popup: `docs/mockups/notifications-toast-popup/SPEC.md`.
Persistent Banner: `docs/mockups/notifications-banner/SPEC.md`.

If an image and this specification conflict, this specification wins.

## Purpose

The Topic Editor is the focused editor opened from one notification topic group such as:

- Medien;
- Anfragen;
- Lernen;
- Community;
- Account;
- System/Admin.

It lets one profile decide, per event type:

1. whether the event is enabled;
2. which available channels receive it;
3. whether delivery is Immediate or Digest when supported.

It must stay simple enough to understand without exposing transport or event-pipeline internals.

## Navigation

Entry:

`Profile → Settings → Notifications → Topic group`

Example:

`Benachrichtigungen → Medien`

Desktop:

- open as a focused right-side sheet or large dialog attached to the Settings page;
- keep the parent Notification Settings page visible behind it.

Mobile:

- open as a dedicated full-height sheet/page;
- one column;
- normal back navigation returns to Notification Settings.

Do not create another permanent Settings destination.

## Header

Contains:

- contextual back/close action;
- topic name, for example `Medien`;
- concise description;
- optional overflow menu for bulk actions.

No tabs are required.

Recommended overflow actions:

- `Alle aktivieren`;
- `Alle deaktivieren` only when policy permits;
- `Auf Standard zurücksetzen` only when a real canonical defaults contract exists.

Do not show a permanent Save button.

## Event list

Each event type is shown as one self-contained setting group.

Example Media topic:

1. Neue Releases;
2. Anfrage-Status;
3. Download & Import.

Each event block contains:

- event icon;
- user-facing event name;
- concise description;
- enabled/disabled toggle;
- channel selection;
- delivery timing when supported.

No raw enum/category names are shown.

## Enable / disable behavior

The event-level toggle controls whether the profile receives this event at all.

### Enabled

- channel controls are active;
- timing controls are active when supported.

### Disabled

- existing selected channels/cadence remain stored unless product policy explicitly resets them;
- controls become visually disabled;
- turning the event back on restores the previous valid configuration.

This prevents accidental loss of detailed preferences.

Disabling an event affects only future deliveries.

Existing Notification Center items remain unchanged.

## Required / mandatory events

Some account, security or critical Admin events may not be fully disableable.

Canonical event policy owns whether:

- the event may be disabled;
- at least one delivery channel is mandatory;
- Digest is disallowed;
- Quiet Hours may be bypassed.

The UI reflects this truth.

Examples:

- toggle may be fixed on;
- explanatory text may say `Erforderliche Sicherheitsmeldung`;
- unavailable choices must explain why.

Do not hard-code mandatory rules only in the UI.

## Channel selection

Supported target channels include:

- In-App;
- Push;
- E-Mail.

Multiple channels may be selected simultaneously.

Example:

`Neue Releases → In-App + Push`

The control should use compact selectable chips/buttons as shown in the approved mockup.

### Availability

Only channels currently available to the profile may be selectable.

A channel can be unavailable because:

- Admin disabled/unconfigured the transport;
- profile lacks required account/device state;
- Push permission is missing/blocked;
- E-Mail is unavailable/unverified;
- policy forbids that channel for the event.

Unavailable channels may remain visible only when the user benefits from understanding why an existing/preferred option cannot currently be used.

They must appear clearly disabled with accessible explanation.

Do not make unavailable channels look selectable.

## Minimum channel rule

For an enabled event, the canonical subscription contract must define whether zero selected channels is allowed.

Preferred default:

- enabled event requires at least one valid delivery path.

If the user tries to deselect the final required channel:

- keep the last channel selected;
- show concise local explanation;
- do not silently switch the whole event off.

If product policy allows zero channels, then event enable/disable should still remain semantically clear and not create an enabled-but-undeliverable state by accident.

## Delivery timing

When supported, an event shows:

- `Sofort`;
- `Digest`.

Use radio/segmented semantics: one timing mode per event.

### Immediate

The event is delivered through selected channels according to normal delivery policy as soon as eligible.

### Digest

The event is queued for the profile's configured Digest schedule.

Digest is a cadence, not a transport.

An event configured for Digest must not also receive a duplicate immediate delivery unless canonical escalation policy explicitly requires it.

### Timing availability

Digest may be disabled when:

- the event is security/critical;
- the event requires immediate delivery;
- no Digest schedule/channel exists;
- the event category does not support batching.

Unavailable timing choices must remain truthful and explained when necessary.

## Interaction order

The most intuitive order inside an event block is:

1. event title + enable toggle;
2. channel selection;
3. timing.

This matches the user's mental model:

`Do I want it? → Where should it arrive? → When should it arrive?`

Do not lead with transport details before the event itself.

## Auto-save

All reversible preference changes persist immediately.

Actions include:

- enable/disable event;
- add/remove channel;
- change Immediate/Digest.

Show a compact page/sheet status:

`Automatisch gespeichert`

This is reassurance, not a permanent success toast after every tap.

On failure:

- preserve or restore the control state to match persisted truth;
- show local error near the affected event;
- never display `saved` when persistence failed.

No full-page refresh should be necessary for ordinary changes.

## Dependency consequences

### Channel disabled globally while editor is open

If an Admin disables a channel:

- refresh its availability;
- preserve stored user intent where useful for future restoration;
- mark it unavailable;
- ensure effective delivery no longer uses it.

Do not silently remap it to another channel.

### E-Mail loses verification

- E-Mail becomes unavailable;
- other selected channels remain active;
- event itself stays enabled if another valid channel remains.

### Push permission revoked

- Push becomes unavailable;
- show truthful device/permission state;
- do not switch the event off if another selected channel works.

### Digest disabled globally/profile-wide

- affected events must not remain in a silently non-deliverable Digest state;
- canonical settings migration/policy must define fallback, preferably explicit user-visible transition to Immediate or a required resolution state.

## Bulk actions

Bulk actions are secondary.

They belong in the header overflow, not as prominent page buttons.

Possible actions:

- enable all optional events in this topic;
- disable all optional events;
- reset topic to canonical defaults.

Bulk changes must:

- show clear consequence before destructive broad changes where appropriate;
- preserve mandatory events;
- persist atomically where practical.

Do not create bulk actions before a real need/default contract exists.

## Desktop layout

Approved direction:

- parent Notification Settings remains visible on the left;
- Topic Editor opens as a right-side sheet;
- sheet has enough width for compact channel chips without wrapping excessively;
- event blocks are vertically stacked;
- auto-save status appears at the bottom/quiet footer area;
- no nested cards beyond the event setting groups;
- no Admin-style table.

The parent row that opened the editor may remain highlighted.

## Mobile layout

Approved direction:

- editor takes the full usable phone width;
- back action at top left;
- topic title centered/clear;
- event blocks stack vertically;
- channel chips wrap cleanly;
- toggles remain reachable;
- disabled event sections visibly mute dependent controls;
- auto-save status stays unobtrusive near the bottom;
- >=44 px touch targets.

No squeezed Desktop side sheet on phone.

## Visual states

The mockup must demonstrate at least:

### Enabled event

- toggle on;
- multiple selected channels;
- Immediate selected.

### Enabled event with different channel mix

- demonstrates that channel selection is per event.

### Disabled event

- toggle off;
- channel/timing controls retained but visually disabled.

This makes the consequences understandable without extra documentation.

## Accessibility

- event toggle has explicit accessible name;
- channel chips expose selected/unselected state;
- radio/segmented timing uses correct semantics;
- disabled controls expose why when necessary;
- focus order follows event block order;
- keyboard operation works on Desktop;
- sheet/dialog restores focus to the opening topic row;
- state is never communicated by color alone.

## Localization

All event labels, descriptions, channel names, timing labels and status/error messages use Jularr localization resources.

Stored preference identity uses canonical event/channel identifiers.

Do not persist rendered localized labels as configuration identity.

## Architecture ownership

Target flow:

`Canonical event catalog/policy → profile subscription editor → canonical profile notification preference store`

Ownership:

- event catalog owns event identity/audience/severity/policy capabilities;
- Admin Notifications owns transport availability;
- profile notification preference owner stores enabled/channels/timing;
- Topic Editor only presents and edits that canonical preference state;
- Digest scheduler owns batching time;
- Notification dispatcher/scheduler owns actual delivery.

Do not create topic-specific preference tables or page-local shadow settings.

## Target preference model

The finished UX requires one event preference to support:

- enabled state;
- multiple selected channels;
- timing mode;
- policy validation.

The current single-value `NotificationMode` model is not sufficient for this end state.

Implementation must evolve the canonical subscription model rather than add a parallel per-topic configuration path.

## Approved mockup composition

The approved reference shows:

- Desktop Light Mode Notification Settings page on the left;
- `Medien` Topic Editor opened as a right-side sheet;
- realistic Mobile version of the same `Medien` editor;
- three example events:
  - Neue Releases enabled with In-App + Push and Immediate;
  - Anfrage-Status enabled with a different channel combination;
  - Download & Import disabled with dependent controls muted;
- visible auto-save state;
- Jularr Clean purple selection language;
- no permanent Save button.

The image is a visual reference only. This specification remains the behavioral contract.

## Mockup file

Upload the approved visual reference as:

`docs/mockups/notifications-topic-editor/image.png`
