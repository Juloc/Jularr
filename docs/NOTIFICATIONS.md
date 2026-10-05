# Notifications architecture

Canonical architecture and product contract for Jularr events, profile notification preferences, delivery scheduling, In-App inbox state, external channels, transient attention and persistent banners.

This document consolidates the target semantics defined by issue #429 and the binding Notification UX specs.

## Authority

For notification behavior and ownership:

1. `DOMAIN.md`, `ARCHITECTURE.md` and global security/authorization rules remain authoritative.
2. This document owns notification-specific architecture, data semantics and cross-surface behavior.
3. `docs/UX.md` owns shared UI behavior.
4. `docs/mockups/notifications-*/SPEC.md` and `docs/mockups/admin-notifications/SPEC.md` own screen/surface-specific presentation and interaction details.
5. Approved images are visual references; text is authoritative when an image conflicts.

A screen spec must not create a second notification state/configuration path.

## Status

The current implementation already provides the first #429 slice:

- one `IJularrEventPublisher`;
- durable `EventLogStore`;
- `JularrEventCategories` with audience/severity/message metadata;
- `NotificationDispatcher`;
- `NotificationSubscriptionStore`;
- `INotificationSink`;
- durable profile-scoped `NotificationStore`;
- In-App delivery;
- read/unread;
- occurrence-count deduplication;
- deep links;
- Bell unread count;
- Notification Center.

The finished product target adds:

- multiple simultaneous profile channels;
- E-Mail and Push transports;
- durable delivery intents/results and retry;
- profile channel preferences;
- per-event Immediate/Digest timing;
- Quiet Hours;
- restart-safe Digest scheduling;
- dismissal/retention;
- shared quick-action resolution;
- Bell Quick View;
- transient Toast attention;
- state-driven persistent Banners;
- live unread/recent updates.

These extend the existing canonical pipeline. They must not create a second event bus, subscription store or inbox.

## 1. Canonical responsibilities

The target flow is:

`Domain action -> JularrEvent -> EventLog -> recipient resolution -> event policy + profile preferences -> delivery planning -> channel sink / In-App inbox -> delivery result`

Separate presentation flow:

`Delivered/eligible event -> attention policy -> Toast or no transient attention`

Separate persistent-state flow:

`Canonical current condition -> BannerConditionProvider -> BannerPolicy -> BannerPresenter`

### Domain feature

Owns when a meaningful event occurred and supplies canonical subject/context.

It does not:

- call SMTP/Push/Webhook directly;
- decide page-specific Toast rendering;
- create inbox rows itself;
- implement Quiet Hours or Digest.

### Event log

Owns the durable record that the event occurred.

It is distinct from:

- per-profile inbox rows;
- per-channel delivery attempts;
- operational Activity/History;
- current Banner conditions.

### Event catalog

`JularrEventCategories` remains the single event-definition catalog and should evolve rather than being replaced by another registry.

Target event-definition metadata includes, where applicable:

- category;
- user-facing topic group;
- audience;
- severity;
- label/message localization keys;
- built-in default preference;
- whether the event may be disabled;
- whether at least one delivery route is required;
- supported profile channels;
- whether Digest is allowed;
- Quiet Hours policy;
- transient Toast eligibility/attention class.

Banner eligibility is **not** an event-catalog flag. Persistent Banners are current-state driven.

### Profile notification preferences

Own one profile's explicit personal choices.

No screen/page owns separate preference state.

### Delivery planner/scheduler

Owns:

- effective-route calculation;
- Immediate vs Digest routing;
- Quiet Hours deferral;
- restart-safe pending delivery;
- retry timing;
- idempotency/deduplication at delivery level.

The existing `NotificationDispatcher` should evolve into/through this canonical planning path rather than being duplicated by another dispatcher.

### Sinks

Own transport-specific delivery only.

Examples:

- In-App;
- Push;
- E-Mail;
- future integration sinks.

A sink never owns event subscription policy.

### In-App inbox

`NotificationStore` remains the canonical durable profile inbox owner.

The Bell, Quick View and Notification Center are projections/actions over the same store/query boundary.

### Admin Notifications

Owns instance-level transport configuration, channel health/testing and delivery diagnostics.

It does not own arbitrary profile preferences.

## 2. Audience is explicit and fail-closed

Current target audiences:

- Profile;
- Admin.

A `Profile` event requires a concrete target profile.

A missing ProfileId must **not** silently retarget the event to Admin profiles.

If a domain occurrence is really an Admin/system event, its canonical event definition must use Admin audience.

If a future broadcast audience is needed, add an explicit audience contract. Do not infer it from missing data.

This prevents privacy leaks and accidental audience widening.

Admin recipient resolution remains authorization-based and must not rely only on UI role visibility.

## 3. Canonical event target identity

Rendered text and a raw URL are not sufficient canonical identity.

The target event contract should retain typed subject context sufficient to resolve presentation/actions, for example:

- Work;
- Episode;
- Chapter/Volume;
- Request;
- Operation;
- Learning review/session;
- Profile/social subject;
- Admin/system resource.

Existing `MediaType`, `SubjectId` and `DeepLink` may be migrated/evolved, but the long-term contract should avoid making raw presentation URLs the only subject identity.

Deep links remain useful as a validated/derived navigation target.

## 4. Canonical topic taxonomy

Topic grouping is presentation metadata on the canonical event catalog, not hard-coded independently in Settings.

Target groups:

- Media;
- Requests;
- Learning;
- Community/Social, only when a real social capability exists;
- Account/Security;
- System/Admin, only for entitled profiles.

Media owns release/library/download/import user-facing events.

Request approval/denial/status belongs to **Requests**, not Media.

This resolves the illustrative Topic Editor mockup mismatch where an `Anfrage-Status` example appears inside the `Medien` editor. The image demonstrates layout only; implementation follows the canonical topic grouping.

## 5. Profile channel model

Finished profile-facing channels:

- In-App;
- Push;
- E-Mail.

Push is the product channel. Platform endpoints may be Web Push, installed PWA or native/mobile endpoints; users do not need separate preference models unless a future explicit product need requires it.

Webhook/Home Assistant and similar automation adapters are **integration channels**, not normal personal profile channels by default. They require explicit integration routing/configuration and must not simply appear in personal Notification Settings.

### Global profile channel preference

The Settings channel toggle is a profile-wide gate for optional delivery through that channel.

Turning a channel off:

- stops effective optional delivery through that route;
- preserves per-event channel selections so they can be restored when re-enabled;
- does not alter other channel selections.

Mandatory/security policy may require a route even when an optional global preference is off; such exceptions must be explicit and explained.

### Per-event channel preference

Each event preference stores selected channels.

Effective channels are resolved as:

`event-selected channels ∩ profile-enabled channels ∩ instance-available channels ∩ event-supported channels`

No UI should independently calculate a competing result.

## 6. Event preference model

The current single-value `NotificationMode = Off | InApp | Push | Digest` is a V1 limitation and cannot represent the approved target.

The canonical target preference per profile/event needs, conceptually:

- Enabled;
- SelectedChannels;
- Timing = Immediate or Digest;
- UpdatedAt;
- explicit-vs-default provenance where useful.

Digest is timing, not a channel.

The existing `NotificationSubscriptionStore` should be migrated/evolved as the canonical owner. Do not create a second `NotificationPreferencesV2` runtime path.

### Defaults

Until configurable instance defaults exist, use built-in event defaults from the canonical event policy.

If Admin defaults are introduced later, resolution is:

`built-in event default -> instance default override -> explicit profile override`

Changing a default never overwrites an existing explicit profile choice.

## 7. In-App semantics

In-App means durable membership in the profile Notification Center.

If In-App is selected for an event:

- the inbox row is created/updated when the event is delivered to the profile;
- it may become unread immediately;
- the Bell count reflects that state immediately.

Quiet Hours do not delay creation of the durable inbox row.

Digest timing does not turn the In-App inbox into a delayed transport. If an event is configured for Digest and In-App is also selected:

- the durable inbox row may still appear immediately;
- no immediate transient Toast is shown because the event timing is Digest;
- external Digest-capable channels are batched for the scheduled Digest.

A user who wants only a scheduled external summary can deselect In-App for that event.

The Digest editor therefore does not treat In-App as a Digest transport.

## 8. Immediate delivery and Quiet Hours

For an event with Timing = Immediate:

- In-App durable row is created immediately when selected;
- Toast eligibility is evaluated separately;
- Push/E-Mail may deliver immediately if allowed;
- Quiet Hours may defer delayable Push/E-Mail delivery;
- Quiet Hours suppress delayable In-App Toast attention;
- Bell/Center remain current.

Quiet Hours do not mutate the saved event timing from Immediate to Digest.

### Quiet Hours policy

Each event definition owns one canonical delay policy:

- Delayable;
- BypassQuietHours.

Only explicitly critical/security/system policy may bypass.

The profile Quiet Hours editor does not expose a second per-event bypass matrix.

## 9. Digest semantics

Digest batches only events whose profile preference selects Timing = Digest and whose event policy allows Digest.

Digest configuration owns:

- enabled;
- cadence;
- local send time;
- selected Digest-capable external channels;
- timezone;
- scheduler metadata.

Topic Editor owns which events use Digest.

### Digest and Quiet Hours

If a scheduled Digest falls inside Quiet Hours:

- postpone that Digest until Quiet Hours end;
- do not change the saved Digest clock time;
- do not duplicate the same events into a second immediate route.

### Empty Digest

No eligible events means no empty Digest is sent.

### Disable Digest

If events currently use Digest, disabling Digest requires explicit resolution:

- switch affected event preferences to Immediate; or
- disable those event types; or
- cancel.

No silent non-deliverable state.

## 10. Durable delivery planning and results

External/scheduled channels require durable delivery state before production use.

Conceptually one delivery intent/result records:

- event or Digest batch identity;
- profile;
- channel;
- timing kind;
- scheduled/next-attempt time;
- status;
- attempt count;
- last sanitized error;
- success time;
- correlation/related operation where useful.

Target statuses may include:

- Pending;
- Deferred;
- Sending;
- Succeeded;
- Failed;
- RetryScheduled;
- Cancelled/Skipped with a modeled reason.

Only states actually implemented should be shown in Admin UI.

### Retry

Retry is delivery-only.

A failed notification route must never re-run the source media/request/import operation.

Retry must be idempotent and route-specific: success on E-Mail must not be resent merely because Push failed.

## 11. Deduplication

Deduplication has related but distinct layers.

### Event/inbox grouping

`DedupKey` groups repeated equivalent occurrences into one profile inbox item where policy allows.

A real recurrence:

- increments occurrence count;
- updates latest occurrence time;
- makes the item unread again;
- clears dismissal so the new occurrence resurfaces.

### Delivery idempotency

Restart/retry of the same planned delivery must not produce duplicate external sends.

This must not suppress a genuinely new recurrence merely because it shares a long-lived subject key.

Delivery planning therefore needs an idempotency identity tied to the actual delivery occurrence/batch, while event `DedupKey` remains the grouping identity.

Do not use one field for both responsibilities.

## 12. Inbox state model

The target inbox item distinguishes:

- CreatedAt: first occurrence;
- LastOccurredAt: latest grouped occurrence;
- ReadAt: current profile read state;
- DismissedAt: optional inbox removal state;
- OccurrenceCount.

Sorting uses `LastOccurredAt`.

Changing read/unread state must never alter chronological ordering.

The current use of `UpdatedAtUtc` for both recurrence and read-state mutation must be removed during migration.

### Dismissal

Remove/clear means inbox dismissal, not deletion of the canonical EventLog occurrence.

A new grouped recurrence after dismissal resurfaces the item.

Physical purge is retention work, not the user-facing remove action.

## 13. Read state vs delivery state

These are separate facts:

- Event occurred;
- In-App inbox item exists;
- inbox item is read/unread;
- inbox item is dismissed/visible;
- Push/E-Mail delivery succeeded/failed;
- Digest occurrence succeeded/failed.

External channel delivery success does not automatically mark an In-App item read.

Push/deep-link flows may explicitly mark a matching profile inbox item read when the user actually opens it and a secure canonical notification identity is available.

E-Mail fetches/link scanners must not accidentally mark inbox items read.

## 14. Shared action resolver

Notification Center, Bell Quick View and Toast must use one canonical action-resolution contract.

Given event/subject + current capabilities/authorization, resolve at most one primary action such as:

- Play;
- Read;
- Start;
- Open;
- Details.

The resolver also provides the canonical target/deep link.

Surfaces may render different control sizes, but they do not invent different actions.

If the target is unavailable or unauthorized, omit the action or resolve to the correct safe destination.

## 15. Attention policy and Toast

Transient attention is separate from durable delivery.

The canonical attention decision consumes:

- event policy/severity;
- profile popup preference;
- foreground/background state;
- current page/subject;
- immersive Player/Reader/Game state;
- Quiet Hours;
- recent duplicate exposure.

Result:

- no transient attention; or
- Toast.

Persistent Banner is not an output of this event-attention policy.

### Foreground

Eligible foreground event may show Jularr Toast.

Avoid a duplicate OS Push banner for the same foreground delivery when platform capabilities allow.

### Background/closed

Use configured Push; do not queue old foreground Toasts to replay later.

### Quiet Hours / immersive mode

Suppress normal transient Toasts.

Do not replay a burst of stale Toasts when the suppression period ends.

## 16. Persistent Banner is state-driven

Banner truth comes from current canonical system/account/domain state.

Examples:

- storage full;
- required provider unavailable;
- security action required.

A Banner condition may emit an event when it transitions into a problem state, but reading/dismissing that event does not resolve the condition.

Banner lifecycle:

`condition false -> true = show Banner (+ optional one-time event/Toast)`
`true -> true = update/keep same Banner`
`true -> false = remove Banner`

Banner policy owns:

- severity;
- audience;
- corrective action;
- Persistent/Snoozable/Dismissible behavior.

No Banner state is stored in `NotificationStore`.

## 17. Bell / Quick View / Center ownership

All three consume the same canonical inbox query/state.

### Bell

- unread count only;
- no second state;
- phone-sized layout navigates directly to full Center.

### Desktop Quick View

- bounded recent list;
- lazily loaded;
- 4–6 recent items;
- shared actions;
- `Alle Benachrichtigungen` opens Center.

Opening Quick View alone marks nothing read.

### Notification Center

Owns:

- full inbox;
- filters;
- read/unread;
- dismissal;
- chronological grouping;
- incremental pagination/retention projection.

Do not instantiate ad-hoc stores directly in shell partials as the long-term target. Use one notification query/service boundary for count + bounded recent + inbox queries.

## 18. Live updates

Unread count, recent Quick View and open Center should converge on the same state after:

- new delivery;
- recurrence;
- mark read/unread;
- dismissal;
- mark all read;
- profile switch.

Use one live-update mechanism when implemented.

Do not maintain client-side notification truth that can diverge from the server.

## 19. Channel capability and Admin relationship

Admin channel configuration answers:

- does this channel exist?
- is it enabled/configured?
- is it healthy?
- can it send a test?
- what delivery attempts failed?

User preferences answer:

- do I want this event?
- through which available profile channels?
- Immediate or Digest?
- what are my Quiet Hours/Digest schedule?

Admin disabling a channel:

- immediately removes it from effective delivery;
- preserves profile selection intent where useful;
- cancels/skips future pending sends through that route;
- must not silently remap events to another route.

## 20. Pending delivery revalidation

Before a deferred/retried delivery is sent, re-evaluate current effective policy.

Examples:

- user disabled the event;
- user disabled the channel;
- Admin disabled the transport;
- device endpoint was revoked;
- E-Mail lost verification;
- profile was removed;
- mandatory policy changed.

A pending item must not send through a route that is no longer allowed.

Revalidation must not create a duplicate event.

## 21. Module gating

When a profile disables a module such as Learning:

- new optional profile-specific events owned by that module stop;
- module-specific Toast/Push/Digest work stops;
- existing Notification Center history remains;
- shared instance work required by other profiles continues.

This follows the global module-gating contract.

## 22. Profile isolation and security

Profile scope applies independently to:

- event preferences;
- global channel preferences;
- Push endpoints/devices;
- Quiet Hours;
- Digest schedule;
- pending deliveries;
- inbox rows;
- unread count;
- read/dismiss state.

Profile switching must not flash stale data from the previous profile.

Admin events are visible only to entitled recipients.

Deep links/actions re-check authorization server-side.

Webhook/E-Mail/Push secrets never appear in user settings or logs.

## 23. Localization

Canonical events store:

- category/message key;
- structured parameters;
- typed subject/target.

Do not persist rendered localized HTML/text as event identity.

Each surface renders in the recipient/profile locale.

External deliveries render at send time using the recipient locale appropriate to that profile/channel.

## 24. Retention

Retention policies are separate by responsibility.

### EventLog

Canonical audit/event history. Its retention follows EventLog/audit policy, not inbox cleanup.

### Inbox

May be bounded.

Rules:

- dismissal is not immediate physical deletion;
- unread/critical rows receive appropriate protection;
- retention never deletes EventLog merely because inbox data expires.

### Delivery diagnostics

Bounded technical history with secrets redacted.

### Pending delivery / Digest

Finalized/expired scheduler rows are cleaned after the operational retention window.

No queue grows forever.

## 25. Current implementation gaps found by the audit

The existing V1 is useful, but the approved target requires these explicit migrations:

1. `NotificationMode` single-choice preference cannot represent multiple channels + timing.
2. `INotificationSink.Mode` couples a sink to one mutually exclusive mode and must evolve to channel capability.
3. `NotificationDispatcher` directly chooses one sink mode and has no durable scheduling/delivery-intent layer.
4. Profile audience currently falls back to Admin when ProfileId is missing; target is fail-closed.
5. `NotificationStore.UpdatedAtUtc` mixes recurrence ordering with read/unread mutations.
6. `ClearReadAsync` physically deletes inbox rows; target user action is dismissal with retention-based purge.
7. Bell currently constructs `NotificationStore` directly in the partial; target uses a shared query/service.
8. No durable external delivery attempt/result owner exists yet.
9. No restart-safe Quiet Hours/Digest scheduler exists yet.
10. No shared NotificationActionResolver exists yet.
11. No canonical transient NotificationAttentionPolicy exists yet.
12. No state-driven BannerConditionProvider/BannerPolicy exists yet.
13. Current event catalog metadata is too small for topic/policy/capability rules.
14. Current raw `SubjectId`/DeepLink contract is weaker than the approved typed-target/action behavior.
15. Admin/User specs previously described channel/mode semantics at different maturity levels; this document is now the canonical target.

These are target-implementation gaps, not reasons to create parallel compatibility paths.

## 26. Migration principles

Implementation should proceed by evolving the canonical owners.

### Subscription migration

Migrate existing rows:

- Off -> Enabled=false;
- InApp -> Enabled=true, Channels={InApp}, Timing=Immediate;
- Push/Digest legacy values, if any exist, must be interpreted deliberately based on actual stored production usage and not guessed.

After migration, remove the obsolete runtime mode path within the supported upgrade window.

### Inbox migration

Existing:

- CreatedAtUtc -> CreatedAt;
- UpdatedAtUtc -> initial LastOccurredAt;
- ReadAtUtc -> ReadAt;
- DismissedAt initially null.

After migration:

- read-state changes never touch LastOccurredAt.

### Dispatcher migration

Evolve the existing event-publish/dispatcher pipeline into delivery planning.

Do not run old direct-sink delivery and new planner delivery in parallel for the same event.

### UI migration

Existing `/Profile/Notifications` and `/Notifications` should move to the approved canonical screens/surfaces without retaining a second legacy preferences model.

## 27. Implementation backlog and phases

Focused implementation issues:

- #835 — event policy, explicit audience and multi-channel profile preferences;
- #836 — inbox semantics, dismissal, shared query service and action resolver;
- #837 — durable delivery scheduler, Push/E-Mail, Quiet Hours and Digest;
- #838 — Bell/Toast/live attention surfaces and persistent Banners.

### Phase A — canonical policy + preference model (#835)

- extend event catalog metadata;
- fail-closed audience validation;
- migrate profile preferences to Enabled + Channels + Timing;
- add profile channel preference/capability resolution;
- keep In-App delivery working.

### Phase B — inbox correctness (#836)

- split LastOccurredAt from ReadAt;
- add DismissedAt;
- stable pagination;
- retention;
- shared notification query service;
- shared action resolver.

### Phase C — delivery infrastructure (#837)

- channel registry/config capability;
- durable delivery intents/results;
- E-Mail/Push sink contracts;
- retry/idempotency;
- Admin delivery diagnostics.

### Phase D — scheduling (#837)

- Quiet Hours;
- restart-safe deferral;
- Digest buckets/schedule;
- disable-resolution flow;
- burst/grouping.

### Phase E — attention surfaces (#838)

- Desktop Bell Quick View;
- Toast presenter + action feedback unification;
- foreground/background suppression;
- immersive suppression;
- live updates.

### Phase F — persistent conditions (#838)

- BannerConditionProvider;
- BannerPolicy;
- global shell Banner presenter;
- domain condition adapters such as Storage/Provider/Security.

Each phase uses the same canonical event/preferences/inbox/delivery owners.

## 28. Binding UX family

User/profile:

- `docs/mockups/notifications-settings/SPEC.md`;
- `docs/mockups/notifications-topic-editor/SPEC.md`;
- `docs/mockups/notifications-quiet-hours/SPEC.md`;
- `docs/mockups/notifications-digest/SPEC.md`;
- `docs/mockups/notifications-center/SPEC.md`;
- `docs/mockups/notifications-bell/SPEC.md`;
- `docs/mockups/notifications-toast-popup/SPEC.md`;
- `docs/mockups/notifications-banner/SPEC.md`.

Admin:

- `docs/mockups/admin-notifications/SPEC.md`.

This architecture contract is the common behavioral/ownership source for all of them.
