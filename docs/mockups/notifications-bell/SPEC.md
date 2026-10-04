# Notification Bell / Quick View — Target UX

Status: approved planning direction for the finished Jularr notification experience. The approved mockup uploaded to this folder is a visual reference; this text remains binding.

Global UX rules: `docs/UX.md`.
Notification Center: `docs/mockups/notifications-center/SPEC.md`.
User notification preferences: `docs/mockups/notifications-settings/SPEC.md`.

If an image and this specification conflict, this specification wins.

## Purpose

The Notification Bell is the global, low-friction entry point into the profile's In-App notification inbox.

It has two responsibilities:

1. communicate unread state;
2. provide a quick view of the most recent notifications without forcing a full-page context switch.

It is not:

- a second Notification Center;
- a transport status widget;
- a permanent navigation destination;
- a dashboard metric;
- an independent unread counter.

The Bell and Quick View must consume the same canonical profile-scoped inbox state as the Notification Center.

## Product target

The current implementation links the bell directly to `/Notifications`.

The finished target keeps the bell globally available in the authenticated Jularr shell and adds a Quick View.

Desktop:

`Bell → anchored Quick View popover → optional direct action / Alle Benachrichtigungen`

Mobile:

`Bell → vollständiges Notification Center`

The Quick View is intentionally shallow and is a Desktop/large-screen convenience surface. On a phone, a nearly full-screen preview would duplicate the Notification Center without saving meaningful navigation or space.

## Bell placement

Use the existing Jularr shell placement.

Desktop:

- top/app shell area near account controls;
- visually secondary to global navigation/search;
- always reachable while authenticated.

Mobile:

- top bar;
- reachable without opening another navigation menu.

Do not duplicate a second bell in bottom navigation.

## Bell icon

Use the shared Jularr bell icon.

States:

### No unread notifications

- normal neutral bell;
- no badge.

### Unread notifications

- normal bell plus compact unread badge;
- badge uses Jularr accent;
- badge count reflects the current profile only.

### Large unread counts

Display:

- exact count while reasonably compact;
- cap visual text at `99+` when needed.

The accessible label should still expose a useful unread count where technically practical.

The badge must not change merely because the Quick View was opened.

Opening the Bell is not equivalent to marking all notifications read.

## Quick View trigger behavior

Desktop:

- selecting the Bell opens an anchored popover;
- pressing the Bell again closes it;
- clicking outside closes it;
- Escape closes it;
- focus returns to the Bell after dismissal.

Mobile:

- selecting the Bell navigates directly to the full Notification Center;
- normal back navigation returns to the previous screen;
- do not insert an intermediate bottom sheet that duplicates the same inbox rows and actions.

Desktop preserves context with the Quick View. Mobile prefers the full Center because the available viewport no longer gives a compact preview a real UX advantage.

## Quick View hierarchy

The Quick View contains:

1. compact header;
2. short recent notification list;
3. footer action to open the full Notification Center.

No tabs, filters, category sidebars or dashboard widgets.

### Header

Contains:

- title: `Benachrichtigungen`;
- optional unread count;
- compact overflow/settings affordance only if useful.

Do not add a duplicate page description.

## Notification selection

Show a small bounded set of recent visible inbox items.

Target:

- approximately 4–6 items on Desktop/large-screen Quick View.

The list remains chronological by `LastOccurredAt`.

Do not group into `Heute / Gestern` inside the small Desktop popover unless the list becomes long enough to genuinely benefit.

The full Notification Center owns richer chronological grouping.

## Read/unread presentation

Use the same visual semantics as the Notification Center:

- small purple unread dot;
- stronger title weight for unread;
- read items visually quieter.

The Quick View must not create its own read model.

Opening the Quick View alone does not mark anything read.

## Notification item layout

A Quick View item is a compact version of the Notification Center row.

It contains:

- exactly one visual anchor;
- title;
- concise subject/context;
- compact relative/short timestamp;
- optional one primary quick action.

### One visual anchor rule

Use either:

- media artwork/cover;

or:

- a semantic icon.

Never use artwork + category icon + decorative secondary image together.

## Text density

Desktop popover target:

`Title`
`Subject · context`
`5 Min`

Descriptions should normally be omitted in Quick View unless essential.

The Quick View should be rapidly scannable.

## Primary quick actions

Use the same action-resolution rules as the Notification Center.

Examples:

- playable release → Play;
- readable content → Read;
- review reminder → Start;
- request update → Open;
- import failure → Details;
- system critical → Details.

Rules:

- maximum one primary action per item;
- action must be valid for the actual target;
- no dead/disabled action;
- whole item may still open its canonical deep link;
- overflow actions are not required on every compact Quick View row.

On Desktop, icon-first compact actions are preferred to large text buttons.

On touch-capable large screens that still use the Quick View, actions remain touch-friendly.

## Opening an item

Selecting a notification item:

1. attempts the canonical target action/navigation;
2. marks the notification read when the open action is accepted/succeeds according to the Notification Center contract;
3. closes the Quick View when navigation leaves the current context.

If the item has no destination, selecting it may mark it read without inventing a fake destination.

## Footer

The footer always contains one clear action:

`Alle Benachrichtigungen`

This opens the full Notification Center.

Do not add several competing footer buttons.

Optional secondary settings access belongs in:

- header overflow;
- or the Notification Center itself.

## Mark-all-read

Do not make `Alle als gelesen` the dominant Quick View action.

It may exist in an overflow menu where appropriate.

Reason:

- inspecting notifications is the primary Bell task;
- bulk management belongs primarily in the Notification Center.

If used, it affects the same canonical inbox state and immediately updates the badge/list.

## Empty states

### No notifications exist

Show:

`Noch keine Benachrichtigungen.`

Optional concise supporting text only.

Footer may still link to the full Notification Center.

### Notifications exist but all are read

Still show the latest recent items.

Do not replace useful recent context with a large `Alles gelesen` empty state.

The absence of unread state is already communicated by the missing badge/dots.

## Important / critical notifications

Warning/Critical items remain identifiable with the same semantic icon/tone as the Center.

Do not turn the Quick View into a warning dashboard.

Critical items may remain near the top only because of recency; severity must not silently reorder the inbox unless canonical product policy explicitly defines priority ordering.

## Live updates

If a new In-App notification arrives while the Quick View is open:

- badge count updates;
- the item may appear at the top if the user is already near the top;
- avoid disruptive scroll jumps if the user is reading lower items.

If live transport is unavailable, the next normal refresh/request may update the list.

There must not be a second live-notification state store for the Quick View.

## Badge synchronization

The badge and Notification Center must remain consistent after:

- mark read;
- mark unread;
- mark all read;
- dismissal/removal;
- deduplicated recurrence;
- profile switch;
- new delivery.

Changing profiles must reset the displayed count/list to the newly active profile.

No cross-profile stale count may flash.

## Performance

The Bell appears globally, so its unread-count path must stay cheap.

Do not instantiate ad-hoc store logic directly in presentation code as the long-term target.

Prefer a canonical notification query/service boundary that supports:

- unread count;
- bounded recent list;
- profile scoping;
- cancellation;
- efficient indexed queries.

Avoid loading the full inbox merely to render the Bell.

The Quick View list should be fetched lazily on open unless a shared shell query already provides the same bounded data efficiently.

## Accessibility

Bell:

- is a real button/control when it opens a popover/sheet;
- has accessible label with unread state;
- badge is not the only unread signal for assistive technology;
- visible focus state.

Desktop popover:

- keyboard reachable;
- sensible focus order;
- Escape closes;
- focus returns to trigger;
- outside-click dismissal does not trap focus incorrectly.

Mobile Bell:

- is exposed as a normal accessible navigation control to the Notification Center;
- retains the unread count in its accessible label;
- uses normal platform/browser back navigation after entering the Center.

Unread/severity is never communicated by color alone.

## Responsive behavior

### Desktop

- anchored popover;
- restrained width;
- enough room for artwork/icon, title/context, timestamp and compact action;
- no two-column dashboard;
- no large cards;
- no page-level filters.

### Mobile

- Bell opens the full Notification Center directly;
- no Quick View overlay/sheet;
- no tiny Desktop popover squeezed onto phone;
- the full Center owns mobile scrolling, filtering and actions.

### Tablet

Use the Desktop Quick View when width and input model give it enough room. At phone-like widths, follow the Mobile rule and open the full Notification Center directly.

## Visual contract

Use Jularr Clean Light Mode as the reference.

Binding direction:

- white/near-white surface;
- thin neutral border;
- restrained shadow only to establish popover elevation;
- Jularr purple for unread/action emphasis;
- compact typography;
- subtle row dividers;
- no gradients;
- no glow;
- no oversized cards;
- no dashboard widgets;
- no duplicate icons/images.

The Quick View should look like a natural shell surface, not a separate mini-application.

## Architecture ownership

Target flow:

`NotificationStore / canonical inbox query → Bell unread count + Quick View recent list`

Ownership:

- NotificationStore/inbox owner owns durable state;
- shared notification query/service owns efficient unread/recent retrieval;
- Bell owns trigger/badge presentation;
- Quick View owns temporary shell presentation;
- Notification Center owns full inbox management;
- target feature owns destination action;
- Settings owns user delivery preferences.

Do not create Bell-specific notification persistence.

## Current implementation migration note

Current `_NotificationBell.cshtml`:

- calculates unread count directly;
- constructs `NotificationStore` inside the partial;
- renders an anchor to `/Notifications`.

The finished implementation should evolve this toward:

- shared injected canonical notification query/service;
- Bell as a button when Desktop/large-screen Quick View is supported;
- Bell as direct Center navigation on phone-sized layouts;
- lazy Quick View loading;
- full Center link from the Quick View.

Do not retain two competing Bell implementations during migration.

## Mockup contract

The approved reference should be uploaded as:

`docs/mockups/notifications-bell/image.png`

Expected composition:

- Light Mode;
- one Desktop Jularr screen with the Bell Quick View open and visibly anchored to the global Bell;
- unread badge;
- approximately 4–6 representative recent items;
- media artwork where appropriate;
- semantic icons for non-media events;
- at least one Play/Read/Start/Details quick action;
- clear `Alle Benachrichtigungen` footer;
- no filters/dashboard widgets inside the Quick View;
- optional small Mobile shell reference may show Bell + unread badge only, because tapping it opens the already-specified full Notification Center rather than a second Mobile Quick View.

The image is a visual reference only. This specification remains the binding behavioral contract.
