# Collections — Landing / Detail / Edit / Smart Rules / Linked Sync

Status: **approved UX direction; binding planning specification**.

Collections live inside `Library -> Collections`. They are not a new top-level consumer destination.

This specification defines:
- Collections landing;
- Manual Collection detail/edit;
- Smart Collection detail/rule builder;
- Linked Collection detail/sync behavior;
- Desktop/Mobile/Tablet/TV behavior.

The normal Library MediaCard/ListRow remains the canonical media presentation inside Collections.

## 1. Product model

A Collection is a **profile-scoped set/view of canonical Work IDs**.

A Collection may contain any supported media types together:
- Anime;
- Series;
- Movies;
- Manga;
- Light Novels;
- Books;
- Audiobooks.

Cross-media is therefore a normal capability, **not a Collection kind**.

Games uses the same canonical `WorkId` as other media. A Game Work may therefore participate in Work Collections without a second cross-domain identity contract. Collection membership remains title-level; platform/release/runtime details stay in Games/WorkVersion extensions and are not duplicated into `CollectionEntry`.

V1 has exactly three persisted Collection modes:

### Manual
Membership is explicitly selected by the profile owner.

### Smart
Membership is computed from a persisted rule expression over local/cached canonical media facts and profile state.

### Linked
Membership is synchronized from an external provider/list such as AniList/MAL through a profile Connection.

Do not create additional parallel Collection models per media type.

## 2. What is not a Collection mode

### Franchise / adaptation

Franchise/adaptation grouping is not a fourth persistence model.

Use canonical Work relations and represent it as:
- a Smart/derived Collection rule/view where appropriate; or
- a normal franchise presentation surface.

Do not duplicate Work identity to implement a franchise Collection.

### Built-in personal views

Watchlist, Favorites, Reading List and other existing system views should not be duplicated into normal editable Collections merely to fill the Collections page.

If a system view is intentionally exposed beside Collections later, keep its own semantics and label it clearly as a system view rather than pretending it is a Manual/Smart/Linked Collection.

## 3. Ownership

V1 Collections are profile-scoped.

Consequences:
- a Profile sees/edits only its own private Collections unless future sharing is explicitly added;
- Smart rules using watched/read/rating/progress/activity evaluate against the owning Profile;
- switching Profile immediately changes the visible Collections;
- no personal Collection data leaks across Profiles.

Shared/family/public Collections are deferred until explicit ownership/permission semantics exist.

## 4. Entry / navigation

Primary entry:

`Library -> Collections`

At Library level use the existing Library/Collections subview switch.

Do **not** add Collections as a permanent top-level sidebar destination.

Other valid entry points:
- Collection card/shelf;
- direct deep link;
- media context action `Add to collection`.

Back returns to the prior Collections/Library context while preserving scroll/focus/filter state where practical.

## 5. Collections landing

The landing page is a simple browser, not a dashboard.

Header:
- `Collections`;
- Search;
- Filter;
- Sort;
- `New Collection`.

Do not show:
- stats sidebar;
- analytics;
- permanent Manual/Smart/Linked chip wall;
- built-in system views duplicated as fake Collections.

### Filter

Filter may include:
- Mode: Manual / Smart / Linked;
- provider/source for Linked;
- media composition where useful;
- updated/date filters when justified.

The default page shows all Collections together.

### Sort

Useful options:
- Recently updated;
- Title;
- Recently created;
- Item count.

### Collection card

Each card uses:
- automatic 2x2 artwork mosaic by default;
- optional representative/custom artwork;
- title;
- item count;
- compact mode/source marker only when useful, e.g. `Smart` or `Linked · AniList`;
- overflow for secondary management actions.

Do not expose Smart rule syntax or provider IDs on the card.

## 6. New Collection flow

`New Collection` opens a compact chooser:

- Manual Collection;
- Smart Collection;
- Linked Collection.

Do not create separate top-level pages before the user has chosen a mode.

### Manual creation

Ask only for:
- title;
- optional description;
- optional artwork choice.

After creation, open the Manual detail where media can be added.

### Smart creation

Ask for:
- title;
- optional description.

Then open the dedicated Rule Builder.

### Linked creation

Choose:
- configured profile Connection/provider;
- external list;
- optional local title override only where useful.

Then resolve/sync into a local Linked Collection.

Do not ask users to paste provider IDs when a connected provider can list available lists.

## 7. Shared Collection detail shell

All three modes use the same normal detail shell.

### Compact header

Show:
- mosaic/artwork;
- title;
- optional short description;
- item count;
- compact mode/source metadata;
- mode-appropriate primary management action;
- overflow only for secondary actions.

Do not use a huge media-detail hero.

The Collection header should remain visually subordinate to the media grid.

### Toolbar

Below the header:
- Search within Collection;
- Filter;
- Sort;
- Grid/List toggle where useful;
- mode-specific action on the right.

Do not add permanent media-type chip rows by default.

Media type belongs in Filter unless a particular platform width makes a compact type scope materially useful.

### Content

Reuse the normal Library MediaCard/ListRow.

A Collection does not own alternate media cards or duplicated media metadata.

## 8. Manual Collection detail

Primary management action:
- `Add media`.

Secondary:
- Edit details;
- Reorder;
- Duplicate where useful;
- Delete.

Manual detail may use manual order as its default sort.

### Membership

Membership may include any canonical/resolvable Work, including a Work that:
- is only known from Discover/provider metadata;
- is not downloaded locally;
- is not currently requested.

The card then exposes the normal availability/Request state.

A Collection is not restricted to locally stored media.

## 9. Add media

`Add media` opens a searchable selector/sheet.

It:
- searches canonical/resolvable Works;
- supports normal media-type/filter scoping;
- marks items already present;
- supports multi-select;
- stores only canonical Work references.

Do not rebuild the entire Discover page inside this selector.

If a selected provider result is not yet a canonical Work, normal identity resolution applies before durable membership is created.

## 10. Manual editing

Separate simple metadata editing from membership editing.

### Edit details

Small dialog/sheet:
- title;
- description;
- artwork mode/custom artwork where supported.

### Reorder

Manual Collections may enter an explicit Reorder mode.

Desktop:
- drag handle/list or supported grid reorder.

Mobile:
- touch-safe reorder list.

Reorder changes only Collection order.

### Remove

Removing a Work from a Manual Collection never:
- deletes Work;
- deletes files;
- resets progress;
- changes rating;
- cancels Request/Wanted;
- removes provider metadata.

## 11. Smart Collection detail

A Smart detail page should look like a normal Collection.

Header adds:
- compact `Smart` marker;
- result count;
- `Edit rules`.

Do not expose the rule tree permanently above the media grid.

There is no `Add media` action because membership comes from rules + explicit overrides.

Per-item context may include:
- `Why is this here?`

This opens a compact explanation, never raw JSON/YAML.

Example:

```text
Why this matches
✓ Media type: Anime
✓ Local: Yes
✓ Resolution: 2160p
✓ Watched: No
```

## 12. Smart Rule Builder

Smart rules require a dedicated full content page.

Do not place the builder inside a small modal.

Desktop wide composition:
- rule editor left/center;
- live preview right;
- sticky/fixed final Save/Cancel region where appropriate.

Mobile:
- full-screen page;
- vertical rule groups;
- live preview below/collapsible;
- sticky Save.

Tablet landscape may use the Desktop split composition.

## 13. Rule grammar

Support nested groups.

```text
ALL
├─ Media type is Anime
├─ Resolution >= 2160p
└─ ANY
   ├─ Watched is No
   └─ Progress < 10%
```

Group controls:
- ALL / ANY;
- Add rule;
- Add group;
- remove group where valid.

Rule row:
- field;
- operator;
- value;
- remove.

Do not expose YAML/JSON to normal users.

## 14. Rule families

Use the shared normalized Media Facts/projections.

### Identity / metadata
- media type;
- franchise/relation;
- genre/tag;
- studio/author/creator;
- year/decade;
- release/status.

### Availability
- local;
- complete/partial/missing;
- requested / active acquisition;
- monitored where canonical product semantics support it.

### Video / technical / language
- resolution;
- HDR;
- audio language;
- subtitle language;
- language coverage;
- codec only when genuinely useful.

### Reading
- edition language;
- format;
- volume/chapter completeness.

### Profile state
- not started / in progress / completed;
- watched/read/listened;
- rating;
- last activity.

Only show fields meaningful for the selected/current rule context.

## 15. Smart overrides

Smart Collections support:

### Always include
Canonical Work remains visible even when rules do not match.

### Always exclude
Canonical Work remains hidden even when rules match.

Overrides:
- use searchable Work selectors;
- survive rule reevaluation;
- reference Work IDs only;
- remain explicit and inspectable.

## 16. Smart live preview

The Rule Builder updates locally against cached/canonical facts.

Show:
- current result count;
- representative first matches;
- optionally `View all preview`.

Rules:
- no provider network calls just to render/preview a Smart Collection;
- invalid rule shows local validation;
- Save disabled while invalid;
- keep last valid preview where practical.

## 17. Smart sort / limit

A Smart Collection may persist:
- default sort;
- ascending/descending;
- optional item limit.

Temporary presentation sorting in the Collection detail does not silently rewrite the Smart rule/output settings.

## 18. Linked Collection

A Linked Collection is a local Jularr Collection synchronized from an external list.

Example:
`AniList Planning`

Header may show:
- `Linked · AniList`;
- item count;
- last successful sync;
- stale/error state only when meaningful.

Primary management action:
- `Sync now`.

There is no `Add media` or manual reorder while the Collection is linked.

The external source owns membership/order semantics unless an explicit mapping policy says otherwise.

## 19. Linked local/offline behavior

Normal rendering must use local Jularr state.

Flow:

```text
External list
 -> persist provider list/item snapshots
 -> resolve/create canonical Work identities
 -> update local Collection membership by WorkId
 -> render entirely from PostgreSQL/local Jularr state
```

Opening the Collection must not require a live provider API call.

If the provider is offline:
- previously synced membership remains browsable;
- show stale/last-sync state only where useful;
- Sync can retry later.

## 20. Linked identity resolution

External IDs never become Collection/Work IDs.

For every linked list item:
1. persist provider evidence/snapshot;
2. resolve to an existing Work when confidently known;
3. if no Work exists and identity is sufficiently unambiguous, create a minimal canonical Work and attach provider identity;
4. if ambiguous, retain provider snapshot and mapping problem without silently merging based on title similarity.

Consumer UI may show a concise state such as:
`2 Einträge konnten noch nicht zugeordnet werden`

Detailed merge/mapping correction belongs in Admin tooling, not normal Collection detail.

## 21. Linked disconnect / convert

Linked overflow can offer:

### Disconnect and keep local copy
- remove external synchronization link;
- preserve current canonical membership;
- convert to Manual;
- preserve title/description/artwork where useful.

### Delete Collection
- remove Collection and linked membership/sync record;
- does not delete Works/provider snapshots/media/progress.

Do not allow local manual membership edits that will be unpredictably overwritten on the next sync.

## 22. Provider persistence contract

External data fetched for durable Jularr features must be persisted locally as provider evidence/snapshots.

Conceptual `ProviderEntitySnapshot`:

- Provider;
- EntityKind;
- ExternalId;
- normalized searchable fields;
- provider payload snapshot where allowed/useful;
- fetched/refreshed timestamps;
- stale/refresh metadata;
- optional resolved WorkId.

For linked lists also persist:
- external list identity;
- list metadata;
- external membership identity/order/state;
- last sync outcome.

Provider snapshots can become stale but remain usable until an explicit retention/cleanup policy removes them.

Once fetched, normal Collection rendering/identity resolution must not depend on re-fetching the same provider data every page view.

## 23. Collection persistence contract

Conceptual `Collection`:
- Id;
- ProfileId;
- Mode: Manual / Smart / Linked;
- Title;
- Description;
- Artwork configuration;
- default sort;
- optional limit;
- timestamps;
- for Linked: ConnectionId/provider/list identity/sync metadata.

Conceptual `CollectionEntry`:
- CollectionId;
- WorkId;
- order where meaningful;
- source: Manual / Smart materialization / Linked sync / Derived;
- optional external membership identity;
- timestamps.

Smart:
- persisted versioned expression tree;
- include/exclude override Work IDs.

Never copy canonical title/progress/rating/media metadata into CollectionEntry as a competing source of truth.

## 24. Delete semantics

Deleting a Collection deletes only Collection-owned state:
- metadata;
- entries;
- Smart rules/overrides;
- Linked sync relationship.

It never deletes:
- canonical Works;
- Editions/Versions/Assets/Files/Tracks;
- progress/history;
- ratings;
- Requests/Wanted;
- provider evidence/snapshots solely because one Collection stopped referencing them.

Require confirmation.

## 25. Empty / partial states

### No Collections
- concise empty state;
- `New Collection`.

### Empty Manual
- `No media yet`;
- `Add media`.

### Smart zero matches
- `No matches`;
- `Edit rules`.

### Linked not synced yet
- source visible;
- `Sync now`.

### Linked mapping gaps
- render resolved Works normally;
- concise unresolved count;
- do not block entire Collection.

### Filter zero
- `No results with these filters`;
- Reset filters.

## 26. Mobile

Collections landing:
- compact list/grid of Collection cards;
- Search;
- Filter/Sort sheet;
- `+` New Collection.

Detail:
- compact header;
- normal media grid;
- mode-appropriate action.

Manual Edit/Add:
- sheets/full-screen flows;
- touch-safe reorder.

Smart Rule Builder:
- full-screen;
- vertically nested groups;
- no horizontal desktop tables.

Linked:
- sync action + last sync;
- management in overflow/sheet.

## 27. Tablet

Portrait follows Mobile editing patterns.

Landscape can use Desktop detail and split Rule Builder.

## 28. TV

TV is browse-first.

Supported:
- Collections landing;
- open Collection;
- normal media grid;
- Filter/Sort where remote-friendly;
- Play/Read/Listen/Request through normal media cards/detail.

Not supported on TV:
- create/delete;
- drag reorder;
- artwork editing;
- Smart rule editing;
- Linked connection management.

Complex management hands off to Web/Mobile/Tablet.

## 29. Visual styles

Clean and Original Jularr share the same structure/behavior.

Clean:
- neutral;
- purple default accent;
- artwork/mosaics provide most color.

Original Jularr:
- same layout;
- red/pink default accent;
- restrained theme decoration only;
- media/Collection artwork remains primary.

No separate Collection logic per skin.

## 30. Approved mockup direction

Approved visual reference covers:
- Collections landing;
- Manual detail;
- Smart detail;
- Smart Rule Builder;
- Linked detail;
- Mobile Collections list/detail.

The textual contract is authoritative where image-generation shortcuts conflict.

In particular:
- Collections remain under `Library -> Collections`, not permanent sidebar navigation;
- landing does not require permanent Manual/Smart/Linked chips;
- detail headers stay compact;
- Manual alone gets Add/Reorder;
- Smart gets Edit Rules;
- Linked gets Sync and local-copy/disconnect semantics.

The owner will upload the approved mockup image into this folder.

## 31. Must not implement

- no Collections top-level sidebar destination;
- no cross-media Collection kind;
- no separate Franchise persistence model;
- no fake built-in Collections duplicating existing system views by default;
- no permanent Collection-mode chip wall required for landing;
- no special media-card design;
- no duplicate Work/media data;
- no provider calls during normal Smart render/preview;
- no live provider dependency for Linked rendering;
- no local manual membership edits while Linked;
- no provider external ID as canonical Work/Collection ID;
- no title-only silent identity merge;
- no media/progress/request deletion when membership/Collection is removed;
- no TV Smart Rule Builder;
- no cross-Profile leakage.

Text specification wins over imagery on conflict.
