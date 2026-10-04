# Notification Center / Inbox — Target UX

Status: approved planning direction for the finished Jularr notification experience. The approved mockup uploaded to this folder is a visual reference; this text remains binding.

Global UX rules: `docs/UX.md`.
User notification preferences: `docs/mockups/notifications-settings/SPEC.md`.
Admin delivery infrastructure: `docs/mockups/admin-notifications/SPEC.md`.

Canonical notification architecture: `docs/NOTIFICATIONS.md`.

If an image and this specification conflict, this specification wins.

## Purpose

The Notification Center is the profile-scoped durable **In-App inbox** for meaningful Jularr events.

It must let a user answer, at a glance:

1. What happened?
2. Is it new or important?
3. What can I do next?
4. Where does this event belong in Jularr?

The Center is not:

- an Admin job table;
- Activity / To-Do;
- a system log;
- notification transport diagnostics;
- an e-mail/push delivery history;
- a second event store;
- a generic dashboard.

The experience should feel like a focused consumer inbox that connects events to the next useful action.

## Product target

This specification describes the finished Jularr target, not only the current `/Notifications` implementation.

The Center consumes the canonical notification/event pipeline and presents the profile's In-App deliveries.

It may include notifications originating from:

- Media;
- Requests;
- Downloads / Imports;
- Learning;
- Account / Security;
- Social features when those capabilities exist;
- Admin/System events for entitled profiles.

Only events actually delivered to the current profile may appear.

## Navigation

Primary entry points:

- global notification bell;
- direct Notification Center route;
- optional deep links from other Jularr surfaces.

The bell's unread count and the Center's unread state must use the same canonical inbox state.

Desktop may expose Notifications in the profile/account area or shell when the product navigation contract supports it.

Mobile keeps the normal Jularr navigation model and does not add a duplicate notification implementation.

## Page hierarchy

The page is intentionally simple.

Order:

1. page header;
2. compact filter/navigation controls;
3. chronological grouped inbox;
4. no dashboard widgets or secondary metrics rail.

### Header

Contains:

- title: `Benachrichtigungen`;
- one concise description if useful;
- link/action to notification settings;
- compact overflow menu for low-frequency global inbox actions.

Do not add large hero areas or summary cards.

## Primary filters

The default filter strip contains only:

- `Alle`;
- `Ungelesen`;
- `Wichtig`.

Counts may be shown where useful.

### Alle

Shows all visible, non-dismissed inbox items within the active retention window.

### Ungelesen

Shows items whose read state is unread.

### Wichtig

Shows notifications whose canonical severity/policy classifies them as Warning/Critical or otherwise explicitly important.

The UI must not invent importance independently from canonical event metadata.

### Additional category filter

A compact filter action may open a sheet/popover for domain filtering such as:

- Media;
- Requests;
- Learning;
- Account;
- Social;
- System/Admin.

This is secondary filtering and must not permanently clutter the page.

No full-text search is required for V1 target UX unless real usage proves a need.

## Chronological grouping

The feed is grouped by human time:

- Heute;
- Gestern;
- Diese Woche;
- Älter.

Grouping uses the profile's timezone.

The grouping boundary is presentation only; canonical timestamps remain timezone-aware backend values.

## Notification row

Rows are flat inbox rows separated by subtle dividers.

Do not wrap every item in an individual card.

A row contains:

1. unread/state indicator;
2. exactly one primary visual anchor;
3. title;
4. concise subject/context;
5. optional one-line explanation;
6. timestamp;
7. one contextual primary quick action when useful;
8. compact overflow action.

### One visual anchor rule

A row uses **either**:

- media artwork/cover/poster when the event is naturally tied to a media object;

or:

- a semantic Jularr icon for non-media events.

Do not stack artwork + category icon + extra decorative image in the same row.

Examples:

- new episode → series/episode artwork;
- new chapter → cover artwork;
- import failure without useful artwork → warning icon;
- review reminder → Learning icon;
- account/security event → shield/account icon;
- storage critical → system/storage icon.

## Row information hierarchy

Preferred text pattern:

`Title`
`Subject · contextual metadata`
`Optional concise explanation`

Examples:

`Neue Episode verfügbar`
`Solo Leveling · Staffel 2 · Episode 8`
`Jetzt in deiner Bibliothek verfügbar.`

`Import fehlgeschlagen`
`Mushoku Tensei · Staffel 2`
`Der Import konnte nicht abgeschlossen werden.`

The first line answers what happened.

The second identifies the object.

The third is optional and used only when it adds useful meaning.

Avoid verbose diagnostic text in the inbox.

## Primary quick action

A meaningful notification should expose the most likely next action directly.

Examples:

| Event | Primary action |
| --- | --- |
| New playable episode/movie | Play |
| New readable chapter/book content | Read |
| Audiobook available | Play |
| Request approved/denied | Open request |
| Import completed | Open media |
| Import failed | Details |
| Learning review due | Start |
| Social activity | Open profile/activity |
| Admin/System critical event | Details/System status |

The action must be based on the actual target/capability, not only the notification category.

### Action rules

- whole row remains a valid open target when a deep link exists;
- primary quick action performs the most useful direct next step;
- do not show both three different text buttons and a row deep link;
- one clear primary action maximum per row;
- if there is no meaningful direct action, omit it;
- disabled/dead actions are not shown.

On mobile the primary action should normally be an icon button or compact semantic affordance to preserve scanability.

## Row open behavior

Selecting the row:

1. marks the notification as read after the open action is accepted;
2. navigates to the canonical target when one exists.

Examples:

- release → media detail / episode;
- request status → request details;
- import failure → relevant failure/status surface;
- Learning reminder → review/session entry;
- system critical → appropriate Admin/System surface.

If no target exists, opening the item may mark it read without inventing a fake destination.

A failed navigation/action must not falsely mark the item handled if the user never reached the intended target.

## Overflow actions

The per-row overflow menu is for inbox management, not the primary product action.

Target actions:

- Mark as read / unread;
- Remove from inbox.

Optional future actions are allowed only when backed by a real product contract, for example:

- mute this event type;
- open notification settings for this topic.

Do not overload the menu with unrelated domain actions.

## Global inbox actions

Low-frequency global actions live in the page overflow menu.

Target actions:

- Mark all as read;
- Remove read notifications;
- Notification settings.

Do not keep large permanent buttons for these actions in the main content hierarchy.

## Read/unread semantics

Unread state is profile-scoped durable state.

Rules:

- newly delivered item → unread;
- opening/marking an item → read;
- mark unread restores unread state;
- mark all read affects only the current profile;
- read-state changes must not change chronological ordering;
- the bell badge counts visible unread items for the current profile.

### Visual treatment

Unread should be indicated by:

- small Jularr-purple dot;
- stronger title weight;
- optionally a very subtle surface tint.

Read items are quieter.

Do not use large colored cards or a thick accent stripe for every unread row.

State must not rely on color alone.

## Severity and importance

Canonical event severity drives importance presentation.

### Info

Neutral row treatment.

### Warning

Subtle warning icon/tone.

### Critical

Clear critical icon/tone and optional concise `Kritisch` label.

Do not turn the list into a wall of colored pills.

Criticality must remain visible in accessibility semantics and text/iconography.

## Deduplication and repeated events

Repeated identical underlying events must not spam the inbox.

When the canonical deduplication identity matches:

- reuse the existing inbox item;
- increment occurrence count;
- update its latest occurrence time;
- move it to the correct chronological position;
- make it unread again.

Example:

`Import fehlgeschlagen 3×`

Occurrence count is meaningful information and may be shown as a compact badge.

The row may summarize the latest relevant details.

## Required timestamp model

The finished data model must distinguish:

- `CreatedAt` — first occurrence;
- `LastOccurredAt` — latest occurrence for a deduplicated item;
- `ReadAt` — when the user marked/opened it;
- optional `DismissedAt` — inbox removal state.

Chronological sorting uses `LastOccurredAt`.

Changing read/unread state must **never** update the chronological sort timestamp.

This corrects the current implementation behavior where `UpdatedAtUtc` can mix event recurrence and read-state updates.

## Removal / dismissal

User removal should be represented as inbox dismissal, not as deletion of the underlying domain event.

Target behavior:

- remove immediately hides the row;
- show a short undo toast;
- undo restores the item and its prior read state;
- the event/audit source remains intact.

Implementation may physically purge dismissed/read inbox rows later through retention policy, but the Notification Center must not pretend that inbox removal deletes the canonical event.

## Retention

The durable event log and the profile inbox have different purposes.

The inbox may use a bounded retention policy so it does not grow forever.

Retention must:

- be explicit;
- preserve unread/critical items for an appropriate period;
- never delete canonical domain/audit events merely because an inbox row expires;
- be owned by the notification subsystem, not by Razor UI.

Exact retention values belong to the implementation/product policy and should be configurable only if there is a real operator/user need.

## Live updates

The Center may receive new notifications while open.

Do not unexpectedly shift the user's scroll position.

Preferred behavior when the user is not at the top:

`3 neue Benachrichtigungen`

Selecting the indicator loads/reveals them at the top.

When already at the top, new items may appear smoothly without disruptive layout jumps.

The bell badge updates from the same canonical unread state.

## Bell relationship

The bell is a compact entry surface.

The full Center owns the durable inbox.

The bell must not maintain a second unread model.

Bell quick-view behavior is specified separately in:

`docs/mockups/notifications-bell/SPEC.md`

The Center may be reached through `Alle anzeigen` from that surface.

## Toast / popup relationship

Toasts are transient attention surfaces.

They do not replace durable inbox delivery.

A notification that should remain discoverable must still have an inbox item when its policy includes In-App durable delivery.

Toast behavior is specified separately in:

`docs/mockups/notifications-toast-popup/SPEC.md`

## Banner relationship

Persistent blocking/critical banners are reserved for states that need sustained visibility or immediate corrective action.

They are not a substitute for the inbox.

Banner behavior is specified separately in:

`docs/mockups/notifications-banner/SPEC.md`

## Empty states

### No notifications yet

Show a quiet empty state:

`Noch keine Benachrichtigungen.`

Optional supporting text may explain that releases, requests, reminders and important Jularr updates will appear here.

Do not display fake sample notifications.

### No unread

Show:

`Alles gelesen.`

Offer `Alle anzeigen` when currently filtered to unread.

### Filter has no matches

Show:

`Keine passenden Benachrichtigungen.`

Offer a direct filter reset.

## Loading

Use skeleton rows matching the final row geometry.

Do not flash an empty state before data finishes loading.

Loading additional older items should preserve current scroll position.

## Pagination / older history

Do not load an unbounded inbox in one request.

Use cursor/incremental pagination or equivalent bounded loading.

Desktop and mobile should both expose older items without a classic dense admin pagination control.

Preferred UX:

- infinite/incremental loading with stable scroll;
- or a restrained `Ältere laden` action.

Backend queries must remain profile-scoped and indexed for chronological retrieval.

## Error states

A load failure should:

- keep existing loaded notifications visible where possible;
- show a compact retry state;
- not replace the entire page with a generic fatal screen unless no inbox data can be shown.

Per-row action failure:

- leaves the persisted/read state truthful;
- shows concise local/toast feedback;
- allows retry where appropriate.

Never silently discard mark-read/remove actions.

## Mobile UX

Mobile is a native-feeling inbox, not a squeezed desktop table.

Rules:

- one column;
- realistic phone proportions;
- compact header;
- `Alle / Ungelesen / Wichtig` remain directly reachable;
- rows use >=44 px touch targets;
- artwork/icon stays compact on the left;
- title/context may clamp to preserve scanability;
- time is compact;
- primary action uses a clear icon/short action;
- overflow opens a bottom sheet;
- no hover-only behavior;
- no horizontal scrolling.

Tapping the row remains the primary navigation action.

## Desktop UX

Desktop uses:

- standard Jularr shell/sidebar;
- wide but bounded inbox content;
- generous whitespace;
- subtle dividers;
- no dashboard rail;
- no secondary widget stack;
- no table columns that make the page feel like Admin Activity.

Primary action and timestamp may align to the right for scanability.

Per-row overflow can become more visible on hover/focus but must remain keyboard accessible.

## Accessibility

- full row open target must have clear semantics;
- nested primary/overflow actions must not create invalid nested interactive controls;
- unread state is announced to assistive technology;
- severity has text/icon semantics, not color alone;
- keyboard users can reach row, quick action and overflow predictably;
- focus remains visible;
- timestamps have understandable accessible labels;
- destructive/remove actions have appropriate confirmation/undo semantics;
- dynamic new-notification indicators are announced without interrupting the user.

## Localization

Notification message identity remains canonical event key + parameters.

Rendered text uses the recipient profile's language.

Do not persist rendered localized notification text as the canonical event identity.

Date/time formatting follows profile locale/timezone.

Relative timestamps may be used for recent items, with accessible absolute time available where practical.

## Profile isolation and authorization

All inbox reads and mutations are scoped to the current profile.

A profile must never:

- read another profile's notifications;
- mutate another profile's read state;
- infer another profile's unread count;
- access an Admin deep link without the required backend authorization.

Admin-only events are delivered only to entitled profiles according to canonical event audience/policy.

UI visibility is not authorization.

## Architecture ownership

Target flow:

`Domain action → JularrEvent → canonical event policy → profile subscription → In-App delivery → NotificationStore → Notification Center`

Ownership:

- domain feature owns event emission;
- event catalog owns identity/audience/severity/policy;
- dispatcher/scheduler owns delivery routing;
- NotificationStore owns durable inbox state;
- Notification Center owns presentation and inbox interactions;
- target feature owns the destination action/deep link;
- Event Log owns canonical event occurrence/history;
- Admin Notifications owns transport health/configuration.

Do not duplicate event or domain state in the Center.

## Backend target changes implied by this UX

The current implementation already provides a useful baseline:

- profile-scoped inbox rows;
- read/unread state;
- deduplication;
- occurrence count;
- deep links;
- severity;
- mark-all-read;
- clear-read.

The finished UX requires the canonical inbox model/API to support, as needed:

- separate `LastOccurredAt` from read-state timestamps;
- dismissal/undo semantics;
- stable incremental pagination;
- importance filtering by canonical metadata;
- primary action metadata/resolution;
- live/unread update mechanism where implemented;
- retention policy.

Do not solve these by creating a second parallel notification table/store.

## Visual contract

Use Jularr Clean visual language.

Binding direction:

- Light Mode reference first;
- white/near-white main surfaces;
- dark navy text;
- Jularr purple only for selected/unread/interactive emphasis;
- subtle gray dividers;
- restrained border radius;
- minimal shadows;
- no decorative gradients;
- no glow;
- no card around every row;
- no generic SaaS widgets;
- no duplicated image/icon decorations;
- no button-heavy row design.

The content hierarchy itself should provide the visual structure.

Dark and Original Jularr themes keep the same information hierarchy and behavior through shared design tokens.

## Mockup contract

The approved reference should be uploaded as:

`docs/mockups/notifications-center/image.png`

Expected composition:

- Desktop in a wide 16:9-style Jularr application view;
- Mobile in a realistic phone aspect ratio;
- Light Mode;
- flat grouped inbox;
- `Alle / Ungelesen / Wichtig` filters;
- media artwork where appropriate;
- semantic icon where artwork is not appropriate;
- one contextual quick action per actionable row;
- row overflow for inbox management;
- examples of unread/read, warning/critical and deduplicated occurrence count.

The mockup is a visual reference only. This specification remains the binding behavioral contract.
