# #835 Notifications Target Model — implementation pack

Status: **ready for implementation**

Planning branch: `planning/notifications-ux-20261004`

Code baseline audited: `dev@3fd3ae708fa77b15d23d89d828ee51f8f48d6da2`

## Canonical sources

- Notification architecture: `docs/NOTIFICATIONS.md`
- User settings: `docs/mockups/notifications-settings/SPEC.md`
- Topic editor: `docs/mockups/notifications-topic-editor/SPEC.md`
- Admin notifications: `docs/mockups/admin-notifications/SPEC.md`
- Original event-system issue: #429
- Implementation issue: #835

This pack implements **only #835**.

It does not implement:
- Push transport;
- E-Mail transport;
- Quiet Hours scheduling;
- Digest scheduler/buckets;
- delivery retry/history;
- Bell Quick View;
- Toast attention;
- persistent Banners;
- inbox timestamp/dismissal redesign from #836.

Those follow in #836–#838.

## Goal

Replace the current mutually-exclusive notification mode model with one canonical profile preference model that can represent the approved finished product without creating a second runtime preference path.

The result of #835 must provide:

- one canonical event-policy catalog;
- explicit/fail-closed event audience semantics;
- one profile event preference aggregate:
  - Enabled;
  - multiple selected channels;
  - Immediate/Digest timing;
- profile-wide optional channel gates;
- one effective-route resolver;
- current In-App delivery preserved;
- no fake external delivery.

## Current implementation audit

### Event catalog

`JularrEventCategories.Meta` currently owns only:

- audience;
- severity;
- message key;
- label key.

Current categories:

- DownloadGrabbed;
- DownloadFailed;
- ImportCompleted;
- ImportFailed;
- ReleaseAvailable;
- RequestApproved;
- RequestDenied;
- StorageProblem.

This is already the correct single catalog owner and should be **extended**, not replaced.

### Subscription model

Current:

`NotificationMode = Off | InApp | Push | Digest`

with one row per:

`(ProfileId, Category) -> Mode`

This creates three target problems:

1. only one channel can be selected;
2. Digest is incorrectly represented beside transports;
3. profile-wide channel enablement cannot be represented.

### Sink contract

`INotificationSink.Mode` binds each sink to one old `NotificationMode`.

Target sinks are channels, so this property must become channel-based.

### Dispatcher

`NotificationDispatcher` currently:

1. resolves recipients;
2. reads one mode;
3. chooses sinks whose `Mode` matches.

This must become:

1. resolve recipient;
2. resolve canonical event preference;
3. resolve effective channels;
4. deliver only currently supported immediate routes.

No durable scheduling is added in #835.

### Audience bug

Current Profile audience behavior:

- ProfileId present -> that profile;
- ProfileId absent -> all Admin recipients.

Target behavior is explicit and fail-closed.

A missing ProfileId on a Profile event must never widen its audience.

### Emitters affected by audience change

Known emitters:

- `BookManualSearchService` — uses request profile;
- `AcquisitionRequestService` — uses request profile;
- `StorageAvailability` — Admin `StorageProblem`;
- `OperationStore` — may emit profile categories using nullable `snapshot.ProfileId`.

`OperationStore` needs explicit handling before publisher validation is tightened.

A system/background operation with no ProfileId must **not** generate a Profile-audience event and rely on Admin fallback.

Its operational record remains in Activity/History.

If a real Admin notification is required later, that must be a real Admin-audience event category.

## Target domain types

Keep these in the existing Notifications/Events feature family.

### NotificationChannel

Use a stable enum, not localized strings:

```text
InApp = 1
Push = 2
Email = 3
```

Do not model Digest as a channel.

Do not expose Webhook/Home Assistant as normal profile channels.

Those remain integration transports unless a later explicit product contract says otherwise.

### NotificationDeliveryTiming

```text
Immediate = 1
Digest = 2
```

### NotificationTopicGroup

Canonical presentation grouping:

```text
Media
Requests
Learning
Community
Account
SystemAdmin
```

Only groups with real events/capabilities need to render.

### NotificationQuietHoursPolicy

```text
Delayable
Bypass
```

#835 stores event policy only. #837 implements actual scheduling.

### Event policy metadata

Extend `JularrEventCategories.Meta` or rename it in-place to one richer canonical definition.

Do not create a parallel catalog.

Required metadata:

- Audience;
- Severity;
- MessageKey;
- LabelKey;
- TopicGroup;
- DefaultEnabled;
- DefaultChannels;
- DefaultTiming;
- CanDisable;
- RequiresAtLeastOneRoute;
- SupportedChannels;
- AllowsDigest;
- QuietHoursPolicy;
- AllowsTransientAttention.

The final implementation may use compact immutable sets/flags internally for catalog constants, but persisted user channel selections should remain relational and inspectable.

## Concrete policy for current categories

Preserve current effective behavior unless a target rule is already clear.

### DownloadGrabbed

- Topic: Media
- Audience: Profile
- Severity: Info
- Default: Enabled + InApp + Immediate
- CanDisable: yes
- Supported: InApp, Push, Email
- Digest: allowed
- Quiet Hours: Delayable
- Transient attention: false by default eligibility

### DownloadFailed

- Topic: Media
- Audience: Profile
- Severity: Warning
- Default: Enabled + InApp + Immediate
- CanDisable: yes
- Supported: InApp, Push, Email
- Digest: **not allowed**
- Quiet Hours: Delayable
- Transient attention: allowed

### ImportCompleted

- Topic: Media
- Audience: Profile
- Severity: Info
- Default: Enabled + InApp + Immediate
- CanDisable: yes
- Supported: InApp, Push, Email
- Digest: allowed
- Quiet Hours: Delayable
- Transient attention: allowed

### ImportFailed

- Topic: Media
- Audience: Profile
- Severity: Warning
- Default: Enabled + InApp + Immediate
- CanDisable: yes
- Supported: InApp, Push, Email
- Digest: **not allowed**
- Quiet Hours: Delayable
- Transient attention: allowed

### ReleaseAvailable

- Topic: Media
- Audience: Profile
- Severity: Info
- Default: Enabled + InApp + Immediate
- CanDisable: yes
- Supported: InApp, Push, Email
- Digest: allowed
- Quiet Hours: Delayable
- Transient attention: allowed

### RequestApproved / RequestDenied

- Topic: Requests
- Audience: Profile
- Severity: Info
- Default: Enabled + InApp + Immediate
- CanDisable: yes
- Supported: InApp, Push, Email
- Digest: allowed
- Quiet Hours: Delayable
- Transient attention: allowed

### StorageProblem

- Topic: SystemAdmin
- Audience: Admin
- Severity: Critical
- Default: Enabled + InApp + Immediate
- CanDisable: preserve current user choice in #835; do not silently make it mandatory yet
- Supported: InApp, Push, Email
- Digest: not allowed
- Quiet Hours: Bypass
- Transient attention: allowed

Future security/mandatory events may set:

- CanDisable=false;
- RequiresAtLeastOneRoute=true.

Do not pretend an existing category has mandatory semantics that the product has not explicitly assigned.

## Target preference aggregate

Replace the current mode-shaped record with a canonical aggregate conceptually equivalent to:

```text
NotificationEventPreference
- ProfileId
- Category
- Enabled
- Channels[]
- Timing
- IsExplicit
- UpdatedAtUtc
```

`IsExplicit` does not need a database column if row existence already represents an explicit profile override.

No-row resolution:

`event built-in default -> returned resolved preference`

Future instance defaults can insert between those layers without changing this aggregate.

## Profile-wide channel preferences

Add one canonical profile-wide gate per channel.

Conceptual record:

```text
NotificationProfileChannelPreference
- ProfileId
- Channel
- Enabled
- UpdatedAtUtc
```

Built-in defaults:

- InApp = enabled;
- Push = disabled;
- Email = disabled.

Reason:

- existing behavior remains In-App;
- no profile starts receiving a future external channel merely because an Admin later configures it;
- Push/E-Mail require explicit profile/user setup.

Per-event channel selections are **not erased** when the profile-wide channel is disabled.

## Effective-route resolver

Add one service/contract, for example:

`NotificationPreferenceResolver`

It owns the effective route calculation.

Inputs:

- canonical event definition;
- resolved event preference;
- profile-wide channel gates;
- currently available sink channels.

Output:

- Enabled/disabled;
- Timing;
- selected channels;
- effective channels.

Formula:

`event-selected ∩ profile-enabled ∩ event-supported ∩ currently-available`

Rules:

- if event preference Enabled=false -> no effective channels;
- invalid/unsupported persisted channels are ignored and should be normalized on write;
- policy validation happens before persistence;
- instance availability does not erase stored user intent.

Do not duplicate this calculation in Razor Pages or sinks.

## Persistence design

Keep `NotificationSubscriptionStore` as the **single canonical owner**.

It may own multiple tables as one aggregate boundary.

### NotificationSubscriptions

Evolve the existing table to:

- ProfileId;
- Category;
- Enabled;
- Timing;
- UpdatedAtUtc.

Primary key remains:

`(ProfileId, Category)`

Remove `Mode` after deterministic migration.

### NotificationSubscriptionChannels

Create normalized child rows:

- ProfileId;
- Category;
- Channel.

Primary key:

`(ProfileId, Category, Channel)`

Do not persist selected channels as localized strings or JSON.

### NotificationProfileChannels

Create profile-wide gate rows:

- ProfileId;
- Channel;
- Enabled;
- UpdatedAtUtc.

Primary key:

`(ProfileId, Channel)`

Absence resolves through the built-in channel default.

All three tables are still owned through `NotificationSubscriptionStore` / one notification-preference service boundary.

Do not create a second `NotificationPreferencesStore`.

## Migration mapping

Migration must be deterministic and tested.

### Legacy Off

```text
Enabled = false
Timing = Immediate
Channels = { InApp }
```

Keeping InApp selected means re-enabling restores the only previously supported route.

### Legacy InApp

```text
Enabled = true
Timing = Immediate
Channels = { InApp }
```

### Legacy Push

Although unsupported by the current UI/sink, its semantic intent is unambiguous:

```text
Enabled = true
Timing = Immediate
Channels = { Push }
Profile Push gate = enabled
```

This preserves an explicitly stored Push choice.

### Legacy Digest

The legacy value does not encode a transport and no real Digest sink exists.

Do **not** invent external delivery.

Conservative migration:

```text
Enabled = false
Timing = Digest
Channels = { InApp }
```

This preserves the fact that the row represented Digest intent without unexpectedly starting delivery under new semantics.

The user must explicitly re-enable/reconfigure it through the future target UI.

Add a migration test for this case.

### No existing row

Do not materialize rows for every category.

Resolve from event built-in defaults.

This preserves the distinction between default and explicit user choice.

## Store API target

Replace old mode-oriented methods with aggregate methods.

Suggested surface:

- `GetEventPreferenceAsync(profileId, category)`;
- `GetAllEventPreferencesAsync(profileId)`;
- `SetEventPreferenceAsync(profileId, category, update)`;
- `ResetEventPreferenceAsync(profileId, category)`;
- `GetProfileChannelPreferencesAsync(profileId)`;
- `SetProfileChannelEnabledAsync(profileId, channel, enabled)`.

A write must be atomic across subscription row + selected-channel rows.

For raw-SQL persistence, use one DB transaction for that aggregate update.

Do not expose low-level channel-table mutation to pages.

## Validation rules

One canonical validator/resolver enforces:

- category exists in the canonical catalog;
- disabled is rejected when `CanDisable=false`;
- selected channels are a subset of `SupportedChannels`;
- Digest is rejected when `AllowsDigest=false`;
- enabled mandatory event has at least one selected supported channel when `RequiresAtLeastOneRoute=true`;
- no unknown enum values;
- profile/channel identifiers are non-empty/defined.

Do **not** validate selected channels against current Admin availability when saving.

Unavailable channels may be retained as user intent.

Effective routing handles current availability separately.

## Audience validation

Add catalog-level event validation.

### JularrEvent.Create

When the category audience is Profile:

- ProfileId is required;
- empty/whitespace throws.

When the category audience is Admin:

- ProfileId must be null;
- a supplied profile ID is rejected as inconsistent.

### Publisher

`JularrEventPublisher.PublishAsync` validates the event against canonical category metadata **before** writing EventLog.

This protects callers that construct a `JularrEvent` manually in the future.

Validation includes:

- event Audience equals catalog Audience;
- event Severity equals catalog Severity;
- ProfileId semantics above.

### Dispatcher

Remove the Profile -> Admin fallback entirely.

Recipient rules:

- Profile -> exactly the named profile;
- Admin -> entitled Admin recipients.

Unknown/future audience -> fail closed.

## OperationStore change required by fail-closed audience

Before enabling publisher validation, update the operation event emitter.

Current operation completion/failure may map to Profile categories while `snapshot.ProfileId` is null.

Target rule:

- if the selected event category is Profile-audience and `snapshot.ProfileId` is missing, do not publish that Profile event;
- keep the canonical Operation/History record;
- optionally log at debug/information level that no profile notification was emitted;
- do not retarget to Admin.

If a future system-triggered operation genuinely needs Admin attention, define/publish a real Admin category instead.

This must land in the same #835 change so fail-closed validation does not break background operations.

## Sink contract migration

Change:

`INotificationSink.Mode`

to:

`INotificationSink.Channel`

Current:

`InAppNotificationSink.Channel = NotificationChannel.InApp`

No Push/E-Mail sink is added in #835.

A sink failure remains isolated.

## Dispatcher behavior in #835

The dispatcher uses:

- recipient resolution;
- event preference;
- profile channel gates;
- registered sink channels;
- event policy.

Then:

### In-App

If effective In-App is selected:

- deliver to `InAppNotificationSink`;
- this remains immediate and preserves current behavior.

### External channels

No real external sink is introduced in #835.

The resolver may model Push/E-Mail preference correctly, but runtime has no registered sink for them.

Therefore no fake delivery occurs.

### Digest timing

If an event is Timing=Digest and In-App is selected:

- In-App durable delivery may still occur immediately, matching `docs/NOTIFICATIONS.md`;
- no external Digest delivery exists until #837.

Do not create an in-memory Digest queue in #835.

## Transitional current UI

#835 does **not** implement the approved full Notification Settings/Topic Editor redesign.

The existing `/Profile/Notifications` page must nevertheless stop using the removed legacy mode model.

Transitional behavior:

- it continues to expose only the currently real user choice:
  - Off;
  - In-App.
- Off writes:
  - Enabled=false;
  - Channels keeps/sets InApp;
  - Timing=Immediate.
- In-App writes:
  - Enabled=true;
  - Channels={InApp};
  - Timing=Immediate.

This keeps the live page functional while #837/#838 and the approved screen implementation land later.

Do not render Push/E-Mail/Digest as working controls in this transitional page.

The page must read labels/audience from the extended canonical event catalog.

## Expected implementation files

Primary:

- `src/Jularr.Web/Features/Events/JularrEventModels.cs`
- `src/Jularr.Web/Features/Events/JularrEventPublisher.cs`
- `src/Jularr.Web/Features/Notifications/NotificationModels.cs`
- `src/Jularr.Web/Features/Notifications/NotificationSubscriptionStore.cs`
- `src/Jularr.Web/Features/Notifications/NotificationDispatcher.cs`
- `src/Jularr.Web/Features/Notifications/INotificationSink.cs`
- `src/Jularr.Web/Features/Operations/OperationStore.cs`
- `src/Jularr.Web/Pages/Profile/Notifications.cshtml.cs`
- `src/Jularr.Web/Pages/Profile/Notifications.cshtml`
- one new EF migration under `src/Jularr.Web/Data/Migrations/`
- migration snapshot only if EF tooling changes it for modeled entities; raw notification tables remain intentionally outside the EF entity model.

Likely new focused types may live under:

- `src/Jularr.Web/Features/Notifications/NotificationPreferenceResolver.cs`

Do not spread policy into page folders.

## Tests

Extend/split `EventNotificationPipelineTests` and add focused preference/migration tests where clearer.

Required coverage:

### Catalog

- every enum category has exactly one policy entry;
- no duplicate/missing category;
- default channels are supported;
- Digest default is not used when Digest is disallowed;
- current topic mapping is correct:
  - RequestApproved/Denied -> Requests;
  - StorageProblem -> SystemAdmin.

### Audience

- Profile event requires ProfileId;
- Profile event never reaches Admin when ProfileId is absent;
- Admin event reaches only entitled Admin recipients;
- Admin event with a ProfileId is rejected;
- manually constructed event with mismatched audience/severity is rejected before EventLog append.

### Preference defaults

- missing row resolves to built-in category default;
- missing profile channel row resolves:
  - InApp true;
  - Push false;
  - Email false;
- default resolution does not materialize an explicit row.

### Preference writes

- one event can persist multiple channels;
- disabling an event preserves channel selections;
- re-enabling restores them;
- unsupported channel is rejected;
- Digest on non-Digest event is rejected;
- aggregate write is atomic.

### Effective resolver

- selected channel + enabled profile gate + registered sink + event support -> effective;
- profile channel gate off -> not effective but selection preserved;
- unavailable sink -> not effective but selection preserved;
- disabled event -> none;
- multiple channels resolve independently.

### Migration

Seed legacy schema values and prove:

- Off -> disabled + InApp + Immediate;
- InApp -> enabled + InApp + Immediate;
- Push -> enabled + Push + Immediate + Push gate enabled;
- Digest -> disabled + InApp + Digest;
- no-row behavior still uses catalog default.

### Existing behavior

- default profile event still creates exactly one unread In-App item;
- Off suppresses In-App but EventLog remains;
- sink failure still cannot fail originating publish;
- request approve/deny remains profile-scoped;
- StorageProblem remains Admin-scoped;
- dedup behavior remains unchanged in #835.

### OperationStore

- profile-scoped operation with ProfileId publishes expected event;
- system/background operation without ProfileId does not publish a Profile event;
- operation itself still completes/records normally.

## Implementation order

### Phase 1 — types and event policy

1. Add channel/timing/topic/quiet-hours policy enums.
2. Extend the canonical event catalog.
3. Add catalog validation.
4. Add catalog tests.

No DB change yet.

### Phase 2 — persistence migration

1. Add new preference/channel tables/columns.
2. Backfill legacy values using the mapping above.
3. Remove `Mode`.
4. Replace store API with canonical aggregate methods.
5. Add migration/store tests.

### Phase 3 — effective routing

1. Add `NotificationPreferenceResolver`.
2. Convert `INotificationSink.Mode` -> `Channel`.
3. Convert In-App sink.
4. Update dispatcher to effective-channel resolution.
5. Preserve sink-failure isolation.

### Phase 4 — fail-closed audience

1. Fix `OperationStore` null-profile emission.
2. Tighten `JularrEvent.Create`.
3. Validate at publisher boundary.
4. Remove dispatcher fallback.
5. Add audience/emitter regression tests.

Do not tighten publisher validation before affected emitters are corrected in the same branch.

### Phase 5 — transitional page

1. Update `/Profile/Notifications` model to the new aggregate.
2. Keep only Off/In-App in the current live UI.
3. No fake target controls.
4. Keep localization/current route stable.

### Phase 6 — validation

Run at minimum:

- focused notification tests;
- migration tests on the repository-supported test DB path;
- `dotnet test` for affected test project;
- `npm run check` if repository rules require global validation even though no frontend JS changed;
- full repository-required checks from `AGENTS.md`.

## Must not implement in #835

- no `NotificationPreferencesV2Store`;
- no second event catalog;
- no JSON channel array;
- no Digest sink;
- no in-memory scheduler;
- no Push/E-Mail transport;
- no Webhook/Home Assistant in user profile settings;
- no delivery-history table yet;
- no inbox dismissal/timestamp migration from #836;
- no Toast/Banner logic;
- no full settings visual redesign;
- no profile-to-Admin fallback;
- no broad mandatory-notification policy invented for existing events.

## Completion criteria

#835 is complete when:

1. `NotificationMode` is no longer the runtime preference/sink contract.
2. Existing explicit legacy preferences migrate deterministically.
3. One event supports multiple selected channels.
4. Digest is represented only as timing.
5. Profile-wide channel gates exist with safe defaults.
6. One resolver owns effective-channel calculation.
7. In-App default delivery still works.
8. No fake external delivery exists.
9. Profile events require an explicit profile and never widen to Admin.
10. Current event emitters are compatible with fail-closed audience rules.
11. Current `/Profile/Notifications` remains functional on the new model.
12. Tests prove defaults, migration, multi-channel persistence, route resolution and audience behavior.

After this, #836 can safely correct inbox semantics without also changing the subscription/event-policy model.
