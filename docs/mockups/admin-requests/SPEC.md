# Admin Requests — V1

Status: approved UX direction from planning mockups.

Global UX rules: `docs/UX.md`

## Purpose

Requests is the Admin moderation/workflow page for user content requests.

It is separate from Wanted:

- Requests = who asked for what, and whether the request is approved/rejected.
- Wanted = the technical acquisition need after approval.

Approving a request can create/update the corresponding canonical Wanted/acquisition state.

## Tabs

Section navigation uses `Requests | Rules | Users`, in that order, through the shared navigation catalog and section component. The queue is the first tab. Rules retains the existing settings route and forms. Users initially lists real accounts read-only under the existing user-directory authorization; account management remains separate. The shared header breadcrumbs identify the current view without duplicate page titles or top-right view-switching buttons.

Primary tabs:
- All
- Open
- Approved
- In Progress
- Done
- Rejected

Counts may be shown as small neutral pills.

All includes completed requests. Status tabs are the visible lifecycle filter; do not add a redundant Status dropdown or Season filter.

## Request information

Each request shows:
- requested Work/unit
- media type
- requester
- requested language
- requested edition/version/profile intent where applicable
- request time
- current request state
- linked acquisition state where available

Examples:
- Anime work/season
- Movie version/language
- Manga volume
- Light Novel volume
- Book edition

## Actions

For Open requests:
- Approve
- Reject
- View details

For approved/in-progress requests:
- View linked Wanted/acquisition item
- View media
- optionally change request-specific acquisition preference if permission allows

For completed/rejected:
- View details/history
- reopen only if supported and explicit

Approval must never create duplicate canonical Works.

## Desktop layout

Use:
- Admin sidebar
- state tabs
- search
- filters for media type, language and requester
- request table
- pagination

Recommended columns:
- Title/unit
- Requester
- Language/version
- Requested at
- Status
- Actions

## Mobile layout

Use compact request rows with:
- artwork
- title/unit
- media type
- requester
- requested language/version
- age/date
- state
- primary action(s)

Approve/Reject must be large touch-friendly controls where relevant.

## Visual language

- Light Admin design.
- No full saturated tags.
- Use outlined/subtly tinted tags with small status icons.
- Primary approval action may use the accent color.
- Rejection/destructive treatment stays restrained but clear.

## Acceptance criteria

- Request moderation is distinct from Wanted acquisition.
- Open requests can be approved/rejected.
- Approved request transitions can link to Wanted/acquisition.
- Requester and requested language/version remain visible.
- Desktop and Mobile are both supported.
