# Admin Notifications — V1

Status: approved planning direction. Existing notification/event infrastructure on `dev` is the starting point. Approved mockups uploaded to this folder are visual references; this text remains binding.

Global UX rules: `docs/UX.md`.
User notification settings: `docs/mockups/notifications-settings/SPEC.md`.
Canonical notification architecture: `docs/NOTIFICATIONS.md`.

If an image and this specification conflict, this specification wins.

## Purpose

Admin → Benachrichtigungen owns the **technical notification delivery layer of the instance**:

- which delivery channels/sinks exist;
- whether a channel is enabled/configured;
- channel health and connection test;
- which canonical Jularr event categories exist and who they target;
- technical delivery result/history per channel;
- future instance-level notification defaults when backed by a real canonical contract.

It does not replace:

- the profile/user notification preference page;
- Activity / To-Do;
- operational History;
- generic system logs;
- provider settings;
- feature-specific business rules.

## Current implementation

Jularr already has a central event/notification pipeline that must be reused.

Existing contracts include:

- `JularrEvent`
- `JularrEventCategory`
- `JularrEventAudience`
- `JularrEventSeverity`
- `JularrEventCategories`
- `NotificationDispatcher`
- `INotificationSink`
- `NotificationStore`
- `NotificationSubscriptionStore`
- `NotificationMode`

### Current sinks

Only **In-App** is a real delivery sink today through `InAppNotificationSink`.

The model already contains future modes such as:
- Push
- Digest

but they are not evidence that a real Push/Digest transport exists.

Therefore the Admin UI must never present a channel as active/configurable/testable until a real sink + configuration contract exists.

### Current profile preferences

`/Profile/Notifications` already lets a profile configure event categories.

At the current implementation level:
- Off
- In-App

are the actual supported choices.

Admin Notifications must not create a second per-user subscription model.

### Current event categories

Current canonical event categories include:

- DownloadGrabbed
- DownloadFailed
- ImportCompleted
- ImportFailed
- ReleaseAvailable
- RequestApproved
- RequestDenied
- StorageProblem

The UI must read category metadata from the shared event catalog rather than hard-coding a divergent list.

### Current audience model

Events have explicit audience metadata:

- Profile
- Admin

Admin UI may explain this audience, but must not arbitrarily retarget a domain event to a different audience.

### Current delivery failure behavior

A notification sink failure is isolated by `NotificationDispatcher`.

A broken notification channel must not fail the media/import/request operation that emitted the underlying domain event.

This separation is binding.

## Target navigation

One Admin destination:

`Admin → Benachrichtigungen`

Tabs:

1. **Kanäle**
2. **Ereignisse**
3. **Zustellung**

Optional future tab:

4. **Defaults**

Only add Defaults when a real instance-default notification contract exists.

## 1. Kanäle

Purpose:
- configure and inspect available notification sinks.

### Channel list

Use one row per real sink.

Columns / row information:

- Kanal
- Status
- Beschreibung
- Letzter erfolgreicher Versand
- Letzter Fehler
- Aktionen

Possible status values:

- Aktiv
- Inaktiv
- Nicht konfiguriert
- Degraded
- Fehler
- Unavailable

### In-App

In-App is built-in.

It may show:

- Aktiv
- built-in / no external credentials
- health based on persistence/delivery availability

In-App does not need a fake credential form.

### Future channels

Potential sinks may include, only when implemented:

- E-Mail
- Web Push
- Webhook
- Home Assistant
- other adapters using `INotificationSink`

The mockup may visually demonstrate future channels, but implementation must hide or clearly mark them as unavailable until their real backend exists.

No half-working `Konfigurieren` or `Testen` buttons.

## Channel configuration

Each external channel uses one focused configuration sheet/dialog.

Common fields may include:

- enabled
- endpoint/host
- authentication
- sender/from identity
- timeout
- optional delivery-specific settings

Provider-specific fields are schema/adapter-driven where possible.

### Secrets

Secrets are:

- write-only;
- masked after save;
- never returned in clear text;
- never included in normal logs;
- redacted from diagnostics.

## Test action

`Testen` sends a channel-specific synthetic test notification.

Rules:

- test must not create a fake real domain event;
- result is clearly marked as a test;
- show success/failure and duration;
- failure gives a sanitized technical reason;
- no secret values in error details.

## 2. Ereignisse

Purpose:
- inspect the canonical event catalog and understand notification routing.

This tab is primarily read-only.

### Event table

Columns:

- Ereignis
- Beschreibung
- Zielgruppe
- Schweregrad
- Verfügbare Kanäle / Modi
- Status / Verwendung where useful

### Event metadata

Read from the real `JularrEventCategories` contract:

- category
- audience
- severity
- translation/message key metadata where useful internally

Do not build an independent editable event registry in the UI.

### Audience

Show:
- Benutzer / Profil
- Admin

This is descriptive.

The Admin must not change a Profile event into an Admin event or vice versa from this page.

That routing belongs to the domain event definition.

### Severity

Show normalized values:
- Info
- Warning
- Critical

Do not invent additional severity semantics in the UI.

### Available channels

The event itself does not directly own one hard-coded transport.

Available delivery depends on:
- registered sinks;
- sink mode;
- profile subscription/default;
- instance channel configuration.

Display this distinction accurately.

## 3. Zustellung

Purpose:
- technical delivery diagnostics for notification sinks.

This is **not** the same as Jularr Activity/History.

### Delivery history

A real delivery-history store is required before this tab can show durable history.

If no durable delivery records exist yet:
- do not fabricate history from unrelated logs;
- either keep the tab unavailable or show only current limited diagnostics with an explicit limitation.

### Target delivery record

A future durable delivery record may contain:

- timestamp
- event ID/category
- event audience
- recipient/profile ID reference
- sink/channel
- mode
- status
- duration
- retry count
- sanitized error code/message
- related operation ID
- correlation ID where supported

Do not store rendered localized notification content as the canonical delivery identity if event + params are sufficient.

### Table

Columns:

- Zeitpunkt
- Ereignis
- Empfänger
- Kanal
- Ergebnis
- Dauer
- Details

Filters:

- time range
- channel
- result
- event category
- audience
- recipient where permitted

### Delivery status

Examples:

- Erfolgreich
- Fehlgeschlagen
- Übersprungen
- Retry geplant
- Unavailable

Only show states actually modeled by the delivery subsystem.

## Retry semantics

Retry requires an explicit delivery retry contract.

Do not add a generic Retry button until:
- retryability is known;
- duplicate-delivery behavior is defined;
- idempotency/deduplication semantics are safe.

A channel failure must still never replay the original media/business operation.

## Relationship to profile notification settings

User/Profile Notification Settings owns:

- whether an event is enabled for this profile;
- selected available profile channels;
- Immediate vs Digest timing;
- personal channel gates;
- Quiet Hours and Digest schedule.

Admin Notifications owns:

- which transports exist;
- transport configuration/health;
- technical delivery diagnostics;
- future instance defaults.

Admin must not edit arbitrary user subscriptions from the channel page.

Per-user policy belongs in Users & Permissions or the user's own settings where explicitly authorized.

## Defaults — future only

A future `Defaults` tab may own defaults for newly created profiles.

Examples:

- default mode per event category;
- default enabled channels;
- default digest behavior.

Current behavior is:

absence of a profile subscription row → `NotificationSubscription.DefaultMode` → currently In-App.

Do not expose configurable instance defaults until this behavior is backed by a durable canonical settings contract.

When introduced, default resolution should be explicit:

`instance default → profile subscription override`

Existing explicit user choices must remain intact when defaults change.

## Quiet hours / digest

Quiet hours and digest are primarily profile delivery preferences unless a future instance policy provides hard bounds.

Do not add server-wide quiet hours that unexpectedly suppress all critical Admin notifications.

Critical infrastructure notifications may require explicit bypass semantics if quiet-hours support is introduced.

That behavior must be designed before implementation.

## Event localization

Canonical events store:

- message key
- message parameters

rather than one pre-rendered final text.

This allows notification content to be rendered in the recipient's language.

Do not replace this with one editable global text/HTML template system in V1.

## No template editor in V1

Do not add:
- raw HTML templates;
- arbitrary Liquid/Razor templates;
- scripting;
- custom per-event code.

Reasons:
- breaks centralized localization;
- adds security surface;
- duplicates event semantics;
- complicates upgrades.

A future template system would require a separate explicit design.

## Delivery isolation

Notification delivery is a side effect of the canonical event.

Flow:

`Domain action → JularrEvent → Event log → NotificationDispatcher → recipient preference → sink`

A sink error:
- is logged/recorded;
- may make delivery fail;
- must not roll back the source domain action.

## Event log relationship

The durable event log is the canonical record that an event occurred.

Notification delivery history, when implemented, records that a particular sink attempted/delivered that event.

Do not duplicate the entire event log into a second notification event database model.

## Activity / History boundary

Activity / To-Do:
- operational work needing progress/action.

History:
- completed application operations.

Notifications → Zustellung:
- whether an event notification reached a sink/recipient.

Example:

An Import operation may:
- appear in Activity while running;
- appear in History when completed;
- emit `ImportCompleted`;
- create an In-App notification;
- later have an E-Mail delivery record.

These are related but not the same record.

## System logs boundary

System Logs contain technical application log entries.

Notification delivery detail may link to:
- correlation ID;
- related log filter.

Do not require admins to inspect raw logs for normal notification health.

## Provider boundary

Notification delivery channels are not Metadata/Indexer/Subtitle providers.

Do not place SMTP/Webhook configuration under Admin Providers unless the architecture explicitly adopts one generic adapter model for notification transports later.

For V1 planning, notification sinks belong to Admin Notifications.

## Clean design baseline

Planning mockups use Clean:

- neutral light background;
- purple Jularr accent;
- compact Admin navigation;
- table/list-driven operation;
- no decorative anime imagery;
- restrained status colors;
- no giant tiles.

Dark mode remains required for implementation.

## Detailed / Compact mode

The global Admin density setting applies.

### Detailed

Channels may show:
- descriptions;
- last success/error;
- health detail.

Events may show:
- full descriptive labels;
- routing context.

Delivery may show:
- more technical columns.

### Compact

Prefer:
- one row per channel/event/delivery;
- short status cells;
- inline actions;
- hidden secondary description only where redundant.

Same data/actions/permissions in both modes.

## Mobile

### Kanäle

Use stacked channel rows:
- icon
- channel
- status
- last result
- chevron/actions

### Ereignisse

One event card/row:
- event label
- audience
- severity
- available modes

### Zustellung

One delivery row/card:
- time
- event
- channel
- result
- recipient summary

No squeezed desktop table.

## Authorization

Admin Notifications requires explicit Admin/System or a dedicated notification-management capability.

Potential future capability split:

- view notification configuration
- manage channels
- test channels
- view delivery diagnostics

Do not use navigation visibility as authorization.

Profile notification preferences remain profile-scoped.

## Required states

- loading
- no external channels
- In-App only
- channel unconfigured
- channel healthy
- channel degraded
- channel unavailable
- channel test running
- channel test successful
- channel test failed
- event catalog loaded
- no delivery-history backend yet
- delivery history empty
- delivery history filtered empty
- delivery failure
- permission denied
- secret/config save validation failure

## Implementation target

Incremental implementation:

1. Reuse existing `JularrEvent` / event catalog.
2. Reuse `NotificationDispatcher`.
3. Reuse `INotificationSink`.
4. Reuse current In-App sink.
5. Build Admin channel registry from actually registered/configurable sinks.
6. Add external sink configuration contracts only when each sink exists.
7. Add a durable delivery-attempt/result model before implementing full Zustellung history.
8. Keep profile subscriptions in `NotificationSubscriptionStore`.
9. Introduce configurable instance defaults only with a dedicated canonical defaults contract.

## Must not implement

- No fake E-Mail/Web Push/Webhook support.
- No active Test/Configure action for a sink without a backend.
- No second profile subscription store.
- No arbitrary event audience editing.
- No duplicate event catalog.
- No notification failure that fails the originating domain operation.
- No raw secret readback.
- No unredacted secrets in logs/diagnostics.
- No fake delivery history derived from unrelated Activity records.
- No generic Retry until retry/idempotency semantics exist.
- No HTML/Razor/script template editor in V1.
- No server-wide quiet-hours behavior without explicit critical-event semantics.
- No duplicate Activity/History screen.


## Storage Lifecycle notification boundary (#414)

Storage Lifecycle uses the existing canonical event/notification pipeline. It must not create a Storage-specific delivery system.

The current implementation has `StorageProblem`; additional lifecycle categories are **target categories only until actually added to the shared event catalog**.

Potential #414 categories:
- StorageCapacityWarning / StorageCapacityCritical;
- StorageCleanupReviewAvailable;
- StorageLifecycleActionCompleted / Failed;
- StorageOptimizationCompleted / Failed;
- StorageMigrationCompleted / Failed;
- StorageIntegrityProblem;
- StorageTrashPurgeScheduled.

Rules:
- infrastructure/lifecycle events default to Admin audience unless a specific profile-facing use case exists;
- optional pre-delete/archive/downgrade notice to an affected profile is controlled by #414 policy, not by changing event audience ad hoc;
- profile notices must not reveal other profiles' Requirements/history;
- repeated capacity/forecast checks must deduplicate/group;
- notification delivery failure never fails the lifecycle operation;
- Activity/To-Do remains the operational detail owner; notifications deep-link there or to the relevant Storage review/policy item.
