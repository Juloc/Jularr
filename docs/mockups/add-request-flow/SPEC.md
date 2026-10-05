# Request Flow — Clean Design

Status: **approved UX direction; binding planning specification**.

The approved visual direction is the one-page Request dialog/sheet with a single Scope selector, derived expandable Included content, language/edition preferences and a separate Success state.

The user will upload the approved visual reference to this mockup folder. The text specification remains authoritative if image and text differ.

## 1. Purpose

There is only one explicit consumer acquisition action: **Request**.

Binding playback-intent exception: `docs/mockups/instant-play/SPEC.md`.

`Start watching`, `Watch now`, `Play` and `Continue` are playback intents, not a second acquisition action. When the selected target is missing and effective policy permits instant acquisition, that playback intent may transparently create/reuse and auto-approve the same canonical Request for the smallest required playable target, then continue through Wanted/Search/Download/Import. It never creates a separate acquisition path.

There is no separate Add flow.

A Request may:
- wait for Admin approval; or
- be auto-approved immediately by instance policy/capability.

Both outcomes use the same Request UI.

The flow starts from media the user has already selected on Discover, Search, Calendar, Watchlist, Related Works or a media-detail page.

The dialog never contains media search/title selection.

## 2. Identity contract

The calling surface must provide a safely resolved canonical request target before Request opens.

Target kinds:
- watch/read/listen media -> canonical Work plus optional structure/Edition intent;
- Games -> canonical Game target owned by the Games module.

The shared Request UI does not make Game a Work.

If a provider result is not yet resolved, canonical identity resolution happens before the dialog opens.

Ambiguous provider identity must not create a Request or a new Work automatically.

The Request UI never exposes provider IDs.

If the requested content is already available, the normal action is Play / Read / Listen rather than Request, unless the user is requesting a materially different unavailable language/edition.

## 3. Exactly two UI states

The normal flow has exactly two states:

1. **Request settings**
2. **Request success**

There is:
- no search step;
- no title-selection step;
- no numbered stepper;
- no Next/Back wizard;
- no separate confirmation page before submit.

All relevant request settings are shown on one page.

## 4. Request settings layout

Order:

1. compact MediaIdentityHeader
2. Scope, only for structured media
3. Included content derived from Scope
4. Language & Edition, only when relevant
5. privileged Advanced override, only when permitted
6. footer with Cancel + Request

The page hides irrelevant groups.

A simple Movie request may therefore consist only of media identity, relevant language/edition choice and Request.

## 5. Media header

Show:
- small cover/poster;
- canonical title;
- concise secondary identity such as year/author;
- media type only when useful;
- Close.

Optional metadata must stay compact. Do not reproduce the whole Detail page inside the dialog.

## 6. Scope — one control only

For structured media there is exactly **one primary Scope control**.

### Anime / TV

Values:
- **All current + future**
- **Future only**
- **Custom**

Behavior:

#### All current + future
- all currently known applicable seasons/episodes are included;
- future episodes/releases are monitored automatically;
- Included content shows the resulting selection as a summary.

#### Future only
- already released/current content is not requested;
- future episodes/releases are monitored;
- Included content shows the future selection/state.

#### Custom
- the user explicitly selects seasons/episodes and future monitoring.

There must not be a second independent control that can contradict Scope.

### Manga

Use the same model:
- All current + future
- Future only
- Custom

Included content is Volume -> Chapter where canonical structure supports it.

### Light Novel

Use:
- All current + future
- Future only
- Custom

Included content is Volumes.

### Movie

No Scope selector. The target is the Movie.

### Book

No structural Scope selector by default. The target is the Work/desired Edition.

### Audiobook

No chapter-level acquisition Scope in the normal Request flow. The target is the Work/audiobook Edition.

### Games

Games uses the same one-page Request and approval semantics.

V1 Games Request is normally a simple target request:
- canonical Game identity;
- platform/region/language preference only when the Games acquisition contract genuinely requires it;
- no Season/Volume/Chapter Scope tree;
- no emulator/runtime/BIOS choices in Request.

Runtime/BIOS/playability are Games/Admin concerns after acquisition, not consumer Request fields.

## 7. Included content tree

Included content is **derived from Scope**, not a second competing choice system.

Collapsed rows show useful summaries, for example:
- `Season 1 · 28/28`
- `Season 2 · future`
- `Specials · 0/4`
- `Volume 4 · 12/12 chapters`

Rows can be expanded.

For Anime/TV:
- Season
  - Episode

For Manga:
- Volume
  - Chapter

For Light Novel:
- Volume

### Editing behavior

When Scope is `All current + future` or `Future only`, Included content initially reflects that automatic selection.

If the user changes an individual season/episode/volume/chapter selection, Scope automatically becomes **Custom**.

Parent selection rules:
- selecting a parent selects all eligible children;
- deselecting a parent deselects its children;
- partial child selection gives the parent an indeterminate state.

In Custom mode, future releases appear as part of the same Included content model where meaningful, for example:
- `Future seasons / episodes`
- `Future volumes`

This replaces a separate duplicate Monitoring toggle.

Large trees may lazy-load/virtualize children, but remain the same interaction model.

## 8. Language & Edition

Only show choices relevant to the requested media.

### Language
- profile default preselected;
- human-readable language names;
- optional fallback/alternative language only where supported.

### Edition
For written media/audiobooks:
- known/official edition where meaningful;
- language-specific edition;
- relevant presentation/format where the product genuinely distinguishes it.

For video:
- edition/cut only when it is a real user-facing distinction.

Do not expose release groups, indexer candidates, technical versions or raw provider data.

## 9. Privileged Advanced

Normal users never need acquisition-profile knowledge.

For a permitted owner/media-manager, a collapsed Advanced section may expose:
- `Use default`;
- allowed acquisition-profile override.

This is optional and must not be required to submit Request.

Manual Search remains an Admin workflow.

## 10. Primary action

Primary button:
- **Request**

Secondary:
- Cancel

There is never:
- Add;
- Add & Monitor;
- Submit;
- Next.

Auto-approval is backend/policy behavior after pressing Request, not a different acquisition button. A permitted playback intent may also use the same auto-approval policy through the Instant Play contract without opening this explicit Request dialog.

## 11. Success state

After a successful Request, replace the settings content with a concise Success state.

Show:
- success indicator;
- `Request created` / equivalent concise heading;
- media identity;
- exact scope requested;
- selected language/edition;
- approval/result state.

Possible result states:
- **Waiting for approval**
- **Approved / auto-approved**
- **Looking for media**, only if acquisition search actually started
- **Monitoring**, only if future acquisition is actually active

Actions:
- Done
- View media/details
- View request status when approval/status tracking is relevant

Do not show technical acquisition logs.

## 12. Already-existing states

### Already requested
Do not create a duplicate Request. Show the existing Request state.

### Already monitored / acquired
Do not silently duplicate Wanted/acquisition state.

### Already available
Prefer Play / Read / Listen.

A new Request is only meaningful for a materially different unavailable language/edition/scope.

## 13. Request vs personal media state

These are separate domains.

**Request/Acquisition state**
- what Jularr should obtain;
- which scope/language/edition;
- whether approval/search/monitoring is active.

**Personal Jularr media state**
- Watchlist/Merkliste;
- Watching / Reading / Listening;
- progress;
- completed state;
- rating.

**External provider sync**
- optional adapter under Settings -> Connections;
- for example AniList/MAL synchronization.

The Request dialog never contains:
- Start watching / Watch now playback controls;
- Watching;
- Planning;
- Completed;
- Add to list;
- Watchlist/Merkliste;
- Start watching automatically;
- AniList/MAL sync controls.

AniList/MAL never owns the canonical Request or Jularr personal-state model.

## 14. Data contract

A Request stores user intent around canonical media:

- requester/profile;
- typed canonical target (`Work` for normal media, `Game` for Games);
- optional canonical structural/Edition target(s) for media where applicable;
- Scope;
- included units when Custom;
- requested language;
- edition intent;
- future-acquisition intent derived from Scope/Custom selection;
- timestamps;
- moderation state.

Request is distinct from technical `WantedItem`.

```text
Request
→ approval / auto-approval
→ canonical Wanted
→ Search
→ Download
→ Import
→ Available
```

An auto-approved privileged Request can create/update Wanted immediately, but it is still a Request from the consumer UI.

Never create duplicate canonical Works or Games.

## 15. Multi-user deduplication

Requests remain attributable per profile.

Compatible approved Requests converge on shared canonical Wanted/acquisition state.

Do not download the same target twice only because multiple profiles requested it.

Materially different language/edition requirements may create distinct acquisition targets.

Canceling one user's Request must not cancel shared acquisition still required by another Request or monitoring rule.

## 16. Permissions

Capabilities control:
- create Request;
- auto-approval;
- edit Scope;
- override language/edition;
- use privileged acquisition-profile override;
- cancel own pending Request;
- manage other users' Requests in Admin.

Unavailable privileged controls disappear.

Server authorization is authoritative.

## 17. Desktop

Use one medium centered modal.

Structure:
- compact media header;
- Scope;
- expandable Included content;
- Language & Edition;
- optional Advanced;
- sticky Cancel + Request footer.

No wizard rail and no separate page inside the modal.

## 18. Mobile

Use one full-height sheet/page.

Structure mirrors Desktop:
- compact media header;
- Scope;
- expandable Included content;
- Language & Edition;
- optional Advanced;
- sticky Cancel + Request footer.

No numbered steps and no Next button.

Large child lists can expand inline or use a focused temporary selector if required for performance/usability. Returning restores the same Request page and selection.

## 19. Tablet / TV

Tablet:
- wide: modal;
- narrow: sheet;
- same data/interaction contract.

TV:
- simplified Request;
- simple Scope/language choices only;
- complex granular selection may hand off to Web/Mobile.

## 20. Loading and errors

Support locally:
- loading Included content;
- loading languages/editions;
- provider temporarily unavailable;
- duplicate Request discovered;
- permission changed;
- target changed after metadata refresh;
- submit failure.

Known media identity should render immediately.

Preserve valid selections on retry.

## 21. Navigation

Cancel/Close returns to the exact source context.

Preserve where practical:
- Discover/Search query and filters;
- Calendar period/view;
- Detail scroll/selection state.

Success stays in context unless the user explicitly chooses View media or View request status.

## 22. Accessibility

- initial focus on dialog heading;
- focus trap for Desktop modal;
- keyboard-operable Scope/tree controls;
- indeterminate parent states exposed semantically;
- state not conveyed by color only;
- touch-sized Mobile targets;
- screen reader Success summary includes title, Scope and approval state.

## 23. Shared components

Use shared:
- `MediaIdentityHeader`
- `ScopeSelector`
- `IncludedContentTree`
- `LanguageSelector`
- `EditionSelector`
- `RequestStatus`
- `RequestSummary`
- `Dialog/Sheet`

Do not create separate Anime/Movie/Manga Request dialogs.

## 24. Must not implement

- no separate Add flow;
- no Search/title selection inside Request;
- no wizard/stepper;
- no duplicate Scope + independent season selector semantics;
- no separate Monitoring toggle that contradicts Scope;
- no personal Watching/Reading/Watchlist controls;
- no AniList/MAL controls;
- no per-media Request engine;
- no indexer/release table;
- no downloader selector;
- no filesystem/root controls;
- no release scoring;
- no manual import;
- no ambiguous identity creation;
- no duplicate equivalent Request/acquisition;
- no technical Operations log.

## 25. Approved visual reference

The approved visual direction is:

- Desktop one-page Anime/Series Request modal;
- one Scope selector at the top;
- Included content directly below and expandable;
- changing individual content makes Scope Custom;
- Language & Edition below;
- Request as the sole primary action;
- separate compact Success state;
- equivalent Mobile sheet behavior.

The owner will upload the approved image into this folder. No redesign of this interaction is allowed during implementation without updating this SPEC and receiving UX approval.

Text specification wins over imagery on conflict.
