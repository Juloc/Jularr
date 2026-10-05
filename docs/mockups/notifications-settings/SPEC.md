# User Notification Settings — Target UX

Status: approved planning direction for the finished Jularr notification experience. The approved mockup uploaded to this folder is a visual reference; this text remains binding.

Global UX rules: `docs/UX.md`.
Shared Profile / Settings shell: `docs/mockups/user-settings/SPEC.md`.
Admin delivery infrastructure: `docs/mockups/admin-notifications/SPEC.md`.
Canonical notification architecture: `docs/NOTIFICATIONS.md`.

If an image and this specification conflict, this specification wins.

## Purpose

`Profile → Settings → Notifications` owns one profile's personal notification preferences.

The page answers three user questions:

1. Which delivery channels do I want to use?
2. Which Jularr events do I want to hear about?
3. When should non-critical notifications be delivered?

It does not own:

- SMTP, Web Push, webhook or other instance transport configuration;
- channel health/testing;
- the canonical event catalog itself;
- technical delivery diagnostics/history;
- Activity / To-Do;
- request/acquisition business rules;
- system logs.

Those remain with their existing canonical owners, especially Admin Notifications and the event/notification pipeline.

## Product target

This specification describes the **finished Jularr target**, not only the currently implemented In-App sink.

The target user-facing channels are:

- In-App;
- Push;
- E-Mail.

Additional future user-facing adapters may plug into the same model when the instance supports them.

Digest is not a transport. It is a delivery cadence applied to eligible events/channels.

Quiet Hours is not a channel. It is a scheduling rule for non-critical delivery.

The current `NotificationMode` single-choice model is insufficient for the finished UX because one event may target multiple channels at once. Implementation must evolve the canonical profile subscription contract rather than adding a second shadow preference store.

## Navigation and shell

This screen remains inside the existing Profile / Account experience.

Desktop:

`Profile → Settings → Notifications`

Mobile:

`Profile → Settings → Notifications`

Use the normal Jularr profile/settings chrome. Do not create a second settings shell or Admin-style dashboard.

The page header contains:

- contextual back navigation to Settings;
- title: `Benachrichtigungen`;
- one concise description;
- compact auto-save status when useful.

No permanent Save button for ordinary reversible preference changes.

## Information hierarchy

Desktop target order:

1. page header;
2. Delivery Channels;
3. Notification Topics;
4. Delivery;
5. optional compact preview/right rail when useful.

Mobile target order:

1. page header;
2. Delivery Channels;
3. Notification Topics;
4. Delivery.

Mobile must not reproduce a desktop two-column layout.

## Delivery Channels

The top group shows personal channels that the current instance makes available to this profile.

The channel toggle is a profile-wide optional-delivery gate. Turning a channel off preserves per-event channel selections for later restoration. Effective delivery is resolved centrally from event selection, profile channel enablement, instance availability and event policy; the page must not maintain a second derived preference model.

### In-App

Purpose:

- notifications inside Jularr;
- bell/unread badge;
- Notification Center.

State examples:

- Active;
- Off.

In-App uses the canonical Jularr notification inbox. Do not create a separate user-local feed.

### Push

Purpose:

- Web Push / installed PWA / supported app-device push delivery.

State may show:

- Active;
- Off;
- connected device count;
- permission required;
- unsupported on this device;
- blocked by browser/OS permission.

The preference toggle must not pretend Push is working when no registered push endpoint exists.

Enabling Push may trigger the platform permission flow only after an explicit user action.

Push endpoints belong to the authenticated profile/device and must be revocable.

### E-Mail

Purpose:

- notification delivery to the account/profile-supported verified e-mail destination.

State may show:

- Active;
- Off;
- masked or normal account e-mail where appropriate;
- verification required;
- unavailable because no eligible address exists.

The notification page does not become an account e-mail editor. Account identity/e-mail changes remain under Account & Security.

### Availability rules

A user channel appears as usable only when:

- the instance has the corresponding channel/sink configured and enabled;
- the profile is allowed to use it;
- any channel-specific requirement is satisfied.

Do not show instance-disabled channels as broken toggles.

A temporarily unavailable previously configured channel may remain visible with a truthful unavailable state if the user needs to understand why existing preferences cannot currently deliver.

## Notification Topics

Topics are grouped by product domain rather than shown as one long technical event table.

Target groups include:

### Medien

Examples:

- new releases;
- download/import completion or failure where user-visible.

### Anfragen

Examples:

- request approved/denied;
- request availability/status changes backed by canonical request events.

### Lernen

Examples:

- review reminders;
- learning reminders;
- meaningful learning milestones when the Learning product chooses to publish them.

Do not route every exercise or timer update through the global notification system.

### Konto

Examples:

- account/security events;
- sign-in/security-sensitive changes where supported.

### Soziales

Visible only when a real social/friends capability exists.

Examples:

- relevant friend/follower activity;
- mentions or social events backed by a real canonical event.

Do not reserve permanent UI for an unavailable module.

### System / Administration

Visible only to profiles entitled to receive Admin-audience notifications.

Examples:

- storage problems;
- infrastructure/system failures;
- other canonical Admin-audience events.

User Notification Settings controls the receiving profile's preference where the event policy permits it. It does not redefine event audience.

## Topic-group rows

The main settings page stays compact.

Each domain group row shows:

- icon;
- group name;
- short summary of contained notification types;
- number of enabled event types where useful;
- compact channel/cadence summary;
- chevron.

Example:

`Medien · 3 Ereignisse · In-App, Push`

Selecting a group opens a focused editor sheet/dialog for the events inside that group.

Do not place a dense channel matrix for every event directly on the landing screen.

## Topic editor

Desktop uses a focused dialog/sheet.

Mobile uses a bottom sheet or full-height sheet when content requires it.

Each event row contains:

- user-facing event label;
- optional concise explanation;
- enabled/disabled state where the event may be disabled;
- selected channels;
- cadence: Immediate or Digest where eligible.

Channel selection supports multiple simultaneous channels.

Example target state:

`Neue Releases → In-App + Push → Sofort`

or:

`Review-Erinnerungen → Push → Digest`

Changes persist immediately after successful validation.

## Required and critical notifications

Some security, account or critical infrastructure notifications may be mandatory.

The canonical event policy must determine:

- whether the event can be disabled;
- whether at least one delivery path is required;
- whether Quiet Hours may delay it;
- whether Digest is allowed.

The UI reflects that policy and explains it concisely.

Do not hard-code mandatory behavior only in the page.

Critical events that bypass Quiet Hours must remain visibly marked as such in settings.

## Delivery

The Delivery group owns profile-wide scheduling preferences.

### Quiet Hours

The summary row shows:

- Off; or
- configured time range;
- timezone.

Editor fields:

- enabled;
- start;
- end;
- timezone.

Default timezone follows the profile's Language & Region setting.

Quiet Hours delay/suppress only events whose canonical policy allows it.

They must not silently block mandatory critical account/security or Admin-critical events that explicitly bypass Quiet Hours.

Cross-midnight ranges such as `22:00–08:00` are valid.

Timezone/DST behavior must use real timezone semantics, not a fixed UTC offset.

### Digest

Digest combines eligible non-critical events into scheduled summaries.

The summary row shows:

- Off; or
- cadence/time.

Target choices may include:

- Daily;
- supported future cadence values backed by the canonical scheduler.

The editor must define at least:

- enabled;
- delivery time;
- eligible selected channels where more than one digest-capable channel exists.

An event configured for Immediate is not silently moved into Digest.

An event configured for Digest must not additionally send an immediate external duplicate unless its policy explicitly requires escalation.

In-App is the durable inbox route. When In-App is selected, its inbox row may still appear immediately even if the event timing is Digest; Digest controls scheduled external summary delivery and suppresses immediate transient Toast attention. The canonical semantics are defined in `docs/NOTIFICATIONS.md`.

## Preview

Desktop may use a compact secondary preview/right rail when it improves comprehension.

If present:

- it uses actual recent notification rendering or a clearly labeled local presentation preview;
- it is not a second Notification Center;
- it does not maintain independent read/unread state;
- it must not use misleading fake operational data.

Mobile omits the persistent preview rail.

## Auto-save and feedback

Simple reversible settings use immediate persistence.

On success:

- no modal;
- no full-page confirmation;
- an unobtrusive saved state may appear.

On failure:

- revert or retain the control state according to what truthfully reflects persisted state;
- show the error next to the affected control/group;
- do not claim the setting was saved.

Network loss must not silently discard changes.

Security-sensitive permission/identity actions may use explicit confirmation where needed.

## Loading state

Initial loading should preserve the settings-page skeleton/hierarchy.

Do not flash default preferences and then replace them with stored values.

Channel capability checks that finish later may update only the affected channel state.

## Empty and unavailable states

Possible states include:

- no optional channels enabled by the instance;
- only In-App available;
- no optional topic groups because modules are disabled;
- Push unsupported on current device;
- E-Mail unavailable/unverified;
- temporary delivery service failure.

The page must remain useful in all cases.

If only In-App exists, show the In-App channel and valid topic controls without advertising unavailable transports as functioning.

## Permissions and profile isolation

All preferences are profile-scoped.

A profile may only read/change its own notification preferences unless a separate explicit Admin capability is designed.

Admin entitlement controls visibility of Admin-audience topics.

UI visibility is not authorization; backend policy must enforce profile scope and event permissions.

Changing profile must never leak another profile's subscriptions, push endpoints, unread counts or e-mail delivery preferences.

## Canonical ownership

The finished flow remains:

`Domain action → JularrEvent → canonical event policy/catalog → recipient profile subscription → delivery scheduling → sink → delivery result`

Binding ownership:

- domain feature owns when a meaningful event occurs;
- canonical event catalog owns event identity, audience, severity and delivery-policy metadata;
- profile notification subscription owner stores user choices;
- Admin Notifications owns instance channel configuration/health;
- Notification Center owns durable In-App inbox presentation/read state;
- delivery scheduler owns Quiet Hours/Digest timing;
- sink owns transport-specific delivery.

Do not duplicate any of these responsibilities in Razor/page state.

## Relationship to Admin Notifications

Admin Notifications configures whether a transport exists and is healthy.

User Notification Settings configures whether this profile wants to use available transports for eligible events.

Example:

- Admin enables/configures E-Mail globally;
- user enables E-Mail personally;
- user selects E-Mail for Request Status;
- event policy allows E-Mail;
- dispatcher/scheduler delivers through the canonical E-Mail sink.

The user page must never expose SMTP credentials, webhook URLs, provider secrets or transport test controls.

## Relationship to Notification Center

The settings page controls delivery preferences.

Notification Center owns:

- inbox list;
- unread/read state;
- filters;
- mark read;
- clear/archive behavior if supported;
- notification deep links.

The settings page may link to Notification Center but must not duplicate its management controls.

## Responsive behavior

### Desktop

- use the standard persistent Jularr navigation;
- main settings content remains readable rather than stretched edge-to-edge;
- Delivery Channels may use a compact horizontal set of channel controls;
- topic groups remain list-like rows;
- optional preview may occupy a secondary rail;
- dialogs/sheets stay focused and do not become tiny modals.

### Mobile

- one column;
- standard profile/settings navigation;
- >=44 px touch targets;
- channel controls become stacked rows;
- topic groups remain tappable rows;
- editors use bottom/full-height sheets;
- no hover-only behavior;
- no horizontally compressed desktop matrix.

### Tablet

Use the desktop information hierarchy with adaptive width. A second rail is optional and should disappear before the primary content becomes cramped.

## Accessibility

- all toggles and selectors have explicit accessible labels;
- channel state is not communicated by color alone;
- status text such as Active, Verification required or Permission blocked remains readable;
- focus order follows visual order;
- keyboard operation is complete on desktop;
- sheets/dialogs trap and restore focus correctly;
- success/failure feedback is announced appropriately without becoming disruptive.

## Localization

All labels, descriptions, status text, channel names, cadence text and error messages use Jularr localization resources.

Stored subscription identity is based on event/channel identifiers, never rendered localized strings.

Time formatting follows profile locale while scheduling uses timezone-aware canonical values.

## Visual contract

Use the normal Clean Jularr settings language:

- restrained light surfaces;
- purple accent for interactive/selected state;
- shared icons;
- shared toggles, rows, sheets and status indicators;
- clear spacing;
- no generic SaaS metric cards;
- no decorative gradients/glows;
- no dense Admin table.

Dark and Original Jularr themes use the same hierarchy and interaction model through shared design tokens.

## Planned notification UX family

This screen is one part of the complete notification experience. Separate screen/surface specs are reserved for:

- `docs/mockups/notifications-topic-editor/`;
- `docs/mockups/notifications-quiet-hours/`;
- `docs/mockups/notifications-digest/`;
- `docs/mockups/notifications-center/`;
- `docs/mockups/notifications-bell/`;
- `docs/mockups/notifications-toast-popup/`;
- `docs/mockups/notifications-banner/`.

Admin transport configuration continues to use the existing:

- `docs/mockups/admin-notifications/`.

## Mockup

The approved Desktop/Mobile reference should be uploaded to this folder as `image.png`.

The expected reference should show:

- Desktop in a wide 16:9-style application composition;
- Mobile in a realistic phone aspect ratio;
- Delivery Channels;
- compact Notification Topic groups;
- Delivery / Quiet Hours / Digest;
- Jularr Clean visual language.

The image is a visual reference only. This specification remains the behavioral contract.
