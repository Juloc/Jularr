# Request Status / Details — Consumer Surface

Status: **approved UX direction; binding planning specification**.

This is the normal-user surface for understanding a previously submitted Request.

Binding playback-intent relationship: `docs/mockups/instant-play/SPEC.md`.

It is not an Admin Requests page, not Wanted, and not an acquisition diagnostics view.

The approved visual direction is a compact status dialog/sheet centered on the **current user-relevant state**, with Request details and history collapsed behind optional disclosure.

## 1. Purpose

The surface answers:

- What did I request?
- What is happening now?
- What scope/language/Edition did I request?
- Is Jularr waiting for approval, looking, downloading, preparing, monitoring or finished?
- What is the next useful information?
- Can I edit or cancel my Request?

The page must not feel like a mini Admin job tracker.

## 2. Entry points

Open from:

- the Request/status action on a media Detail page;
- Library/Calendar/other media cards when a Request state is shown;
- Request Success -> `View request status`;
- a notification linked to that Request;
- a future `My Requests` list if one is added;
- a deep link to the current profile's Request.

Do not add a permanent main-navigation destination solely for Requests.

## 3. Surface type

Desktop:
- compact medium modal/dialog.

Mobile:
- bottom/full-height sheet depending on available content.

Tablet:
- adaptive dialog/sheet.

TV:
- simplified read-only status surface where useful.

The default surface remains compact. Optional details expand in place.

## 4. Header

Show:

- small media artwork;
- canonical Work title;
- concise year/type only when useful;
- Close/Back.

Do not repeat a large media hero.

Do not show provider IDs, Wanted IDs, job IDs, downloader identifiers or indexer information.

## 5. Primary current-state block

The current state is the visual focus.

Show:

- one clear state icon;
- one state label;
- one concise explanation;
- optional progress only when meaningful and reliable;
- optional next-release information when monitoring future content.

### Waiting for approval

`Waiting for approval`

`Your request has been submitted and is waiting for approval.`

### Looking for media

`Looking for media`

`Jularr is looking for a suitable release.`

This projects internal Approved/Searching detail into one useful consumer state.

### Getting media

Prefer a consumer-safe media-specific label:
- `Getting episode · 32%`
- `Getting movie · 32%`
- `Getting media · 32%`

Optional secondary context:
`4 of 12 episodes available`

Only show percentage/count when trustworthy. `Downloading` remains an Admin/technical term and is not the preferred primary label on this consumer surface.

### Preparing

`Preparing`

`Processing downloaded media and adding it to Jularr.`

Do not expose extraction/import substeps.

### Monitoring future releases

`Monitoring future releases`

If known:
`Next release · 8 Oct 2026`

If unknown:
`No upcoming release announced`

### Available

`Available`

Primary action:
- Play;
- Read;
- Listen;
- Open details;

depending on media type/state.

### Rejected

`Request rejected`

Optional short user-visible reason only.

### Needs attention / Failed

`Could not complete request`

Show one concise user-readable reason category when available.

### Cancelled

`Request cancelled`

Never imply a shared technical acquisition was cancelled unless it actually was.

## 6. Consumer state model

The normal progression is intentionally simplified:

```text
Waiting approval
→ Looking for media
→ Getting media
→ Preparing
→ Available
```

`Monitoring future releases` is a separate ongoing state and can remain active after current requested content becomes available.

Exceptional states:
- Rejected;
- Needs attention;
- Cancelled.

Backend/Admin may retain more granular technical states, but normal UI projects them into these consumer states.

## 7. Request details

Below the current state, show a compact **Request details** disclosure/card.

It contains the exact saved Request intent:

- Scope;
- selected units when Custom;
- language;
- Edition intent;
- requested date/time;
- future-release intent where relevant.

Use the same labels as the Request flow.

Examples:
- `Whole series · Current + future`;
- `German`;
- `Official`.

Do not rederive saved intent from current profile defaults.

On Mobile this section may default collapsed when the current-state block already communicates enough.

## 8. Timeline

Timeline is secondary and **collapsed by default**.

Trigger examples:
- `Show timeline · 4 events`;
- `History`.

Expanded timeline contains only meaningful consumer milestones:

```text
Requested      2 Oct 18:42
Approved       2 Oct 18:43
Getting media  2 Oct 18:47
Available      2 Oct 19:12
```

Approved can appear as historical context even though it is not a primary current-state screen.

Do not show every search attempt, rejected release, scoring decision, downloader retry, extraction step, import sub-job or raw acquisition event.

Those belong to Admin.

## 9. Edit Request

Where editing is permitted, show `Edit request`.

This reopens the **same shared Request dialog** with saved values prefilled.

There is no separate Edit Request form implementation.

Editable fields follow the Request spec:
- Scope;
- Included content;
- Language/Edition;
- allowed privileged options.

If a change conflicts with acquisition already in progress, backend policy decides whether editing is allowed, requires cancellation/new Request, or can safely retarget.

Never silently mutate an incompatible active acquisition.

## 10. Cancel Request

Show `Cancel request` only when allowed.

Use a confirmation dialog.

Cancellation removes this profile's Request intent.

It does not automatically:

- stop acquisition required by another profile;
- stop owner monitoring;
- delete downloaded/imported media;
- delete canonical Work data;
- reset progress;
- cancel another user's Request.

After cancellation, show the actual resulting Request state.

## 11. Retry / failure behavior

Normal users do not choose release candidates.

If Retry is allowed:
- retry the same Request intent through normal policy;
- use `Retry request`.

Do not open Manual Search.

If Admin intervention is required, show `Needs attention` and optionally a concise explanation.

## 12. Monitoring behavior

For Requests containing future content, monitoring is a user-relevant ongoing result, not a Wanted dashboard.

Show:
- `Monitoring future releases`;
- next known release/date when available;
- otherwise a quiet unknown/no-announcement state.

Do not show polling intervals, scheduled jobs or technical search history.

## 12a. Instant Play / Start Watching relationship

When this Request was created transparently by a permitted `Start watching` / `Watch now` playback intent, this status surface still shows the same canonical Request and acquisition state. It does not become a separate instant-play job view.

While the user remains on the originating media surface, the compact in-place playback-intent UI may show `Looking for media -> Getting media -> Preparing -> Starting playback` as defined by the Instant Play spec.

If the user leaves that surface, acquisition may continue, but later readiness must not unexpectedly open the Player. This Request Status surface may then show `Available` / `Ready to watch` normally.

`Stop waiting` belongs to the transient playback intent and does not cancel this Request. `Cancel request` remains the explicit Request action here and follows shared-acquisition ownership rules.

## 13. Available behavior

When immediately requested content is available:

Show:
- `Available`;
- primary media action;
- optional `View in Library` / `Open details`.

If future monitoring is also active, the surface can show both:

`Available now`

and

`Monitoring future releases`

These states are not contradictory.

## 14. Shared acquisition semantics

The Request belongs to the requesting profile.

Approved Requests can converge on shared canonical Wanted/acquisition state.

The consumer surface must not imply a shared download belongs exclusively to one user.

If this user cancels while acquisition continues because another Request/monitoring rule still needs it, show the user's Request as Cancelled without falsely claiming the shared acquisition stopped.

## 15. Notifications

Request transitions may produce normal notifications for:

- approved;
- rejected;
- available;
- failed/needs attention.

Notification delivery settings remain under User Settings.

## 16. Desktop layout

Approved structure:

1. compact media identity header;
2. prominent current-state block;
3. optional progress / next-release card;
4. Request details;
5. collapsed Timeline;
6. state-dependent bottom actions.

Actions can include:
- Edit request;
- Cancel request;
- Retry request;
- Play/Read/Listen;
- View media;
- Close.

Do not draw a permanent multi-step process pipeline across the main content.

## 17. Mobile layout

Approved structure mirrors Desktop vertically:

1. compact media header;
2. prominent current state;
3. optional progress / next release;
4. collapsible Request details;
5. collapsible Timeline;
6. touch-sized actions.

Keep the current state visible without expansion.

Do not squeeze horizontal desktop process diagrams onto Mobile.

## 18. Tablet / TV

Tablet uses the same adaptive dialog/sheet grammar.

TV is simplified/read-only where useful:
- current state;
- available-media action;
- no complex request editing when remote UX would become cumbersome.

## 19. Loading / partial / errors

Support:

- Request data loading;
- acquisition projection temporarily unavailable;
- partial Work metadata;
- stale local status;
- permission changed.

If acquisition status cannot be loaded, still show the saved Request intent plus:
`Status temporarily unavailable`.

## 20. Privacy / permissions

A normal profile sees only its own Requests unless explicitly granted broader capability.

Moderator/Admin notes are private by default.

A rejection reason must be explicitly user-visible before it appears here.

Server authorization is authoritative.

## 21. Accessibility

- current state has textual label;
- progress exposes semantic value;
- timeline is an ordered semantic list;
- full timestamps are accessible;
- disclosure controls expose expanded/collapsed state;
- Cancel is clearly destructive;
- focus returns to source after close;
- status is not color-only.

## 22. Shared components

Reuse:

- `MediaIdentityHeader`;
- `RequestStatus`;
- `RequestSummary`;
- `RequestTimeline`;
- `ProgressIndicator`;
- `NextReleaseCard`;
- ConfirmDialog;
- shared Request Dialog/Sheet controls.

Request creation, editing and status use one canonical Request DTO/state projection.

## 23. Must not implement

- no permanent technical process pipeline as main content;
- no Admin moderation controls;
- no Wanted/profile scoring;
- no release/indexer candidates;
- no downloader/client technical fields;
- no provider IDs/job IDs;
- no raw logs/errors;
- no manual release picker;
- no separate Edit Request form;
- no duplicate Request state machine;
- no cancellation of shared acquisition solely because one profile cancels;
- no permanent top-level Requests navigation.

## 24. Approved visual reference

The approved mockup direction is:

- compact Desktop modal and Mobile sheet;
- strong current-state block;
- Downloading progress only when useful;
- Monitoring state with next-release information;
- Request details below;
- Timeline collapsed by default;
- Edit/Cancel as secondary actions;
- Available state with direct media action;
- no Admin-like technical workflow presentation.

The owner will upload the approved image into this folder.

Implementation may not materially redesign this interaction without updating this SPEC and receiving UX approval.

Text specification wins over imagery on conflict.
