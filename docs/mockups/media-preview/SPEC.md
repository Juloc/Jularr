# Media Preview / Quick View

Status: **approved UX direction; binding planning specification**.

This specification defines Jularr's optional quick-preview surface for Discover/Search, recommendations and TV browsing.

It is not a mandatory intermediate step before opening a normal media detail page.

## 1. Purpose

Media Preview answers a small set of questions without forcing a full route change:

- What is this title?
- Is it available to me?
- Can I Play / Continue / Read / Listen / Request it?
- What language/Edition context matters?
- Is there a trailer or useful backdrop?
- Do I want to open the full detail page?

The Preview should feel like a modern streaming-platform preview, not a generic form dialog.

## 2. Where Preview is used

Primary surfaces:

- Discover/Search;
- Home recommendation rows;
- TV browse/focus experiences;
- other recommendation/discovery shelves where a short preview is useful.

Optional:
- Library, only where a secondary Quick View action is useful.

Do not make Preview a mandatory step for:
- Continue Watching / Reading / Listening;
- Calendar entries;
- Episode/Chapter selection;
- already-open detail pages;
- Admin flows.

## 3. Entry interaction

### Desktop

Cards remain stable in the layout.

Do **not** enlarge/reflow the card on hover.

Pointer behavior:
- hover may reveal a restrained highlight and a Quick View affordance;
- ordinary card click/tap opens the normal canonical detail page unless the specific surface contract says otherwise;
- Quick View opens the Preview overlay/modal;
- no trailer/player is instantiated merely because the pointer crossed a card.

This replaces the old Discover behavior where the card itself expanded after hover delay.

### Mobile

No hover behavior.

Default:
- tapping the normal card opens Detail;
- secondary Quick View action opens the Preview as a large bottom sheet / modal.

Do not force every card tap through Preview.

### Tablet

Touch-first.

Use a large adaptive dialog/sheet with the same content hierarchy as Desktop/Mobile.

### TV

Preview is a first-class browsing aid.

After focus remains stable on a card for a short delay:
- show the Preview/hero area;
- trailer may begin when allowed;
- do not instantiate/start a new trailer for every transient D-pad movement;
- Back restores the prior row/focus position.

## 4. Surface structure

The Preview uses one consistent information hierarchy across media types.

### A. Hero / trailer region

Video-capable title:
- wide 16:9 hero;
- autoplay trailer when a supported trailer exists and Preview is actually open/visible;
- muted by default;
- otherwise use backdrop/artwork fallback.

Reading title:
- use a strong backdrop/illustration/cover composition;
- trailer is optional and should only appear when a meaningful official/promotional trailer exists;
- do not fabricate video for written media.

### B. Identity / metadata

Show:
- poster/cover;
- canonical Work title;
- year where useful;
- media type;
- concise status, e.g. ongoing/completed where useful;
- runtime or structural context where useful;
- compact language/Edition availability.

Do not show:
- provider IDs;
- release-group names;
- codec/container details;
- file paths;
- acquisition scoring;
- Admin metadata.

### C. Description

Use a short synopsis:
- about 2–4 lines Desktop;
- shorter on Mobile;
- expandable only when useful.

The Preview is not a full metadata page.

### D. Primary actions

One clear primary action based on canonical state:

Available:
- Play;
- Continue;
- Read;
- Continue Reading;
- Listen;
- Continue Listening.

Unavailable:
- Request.

Request already exists:
- show current consumer Request state/progress instead of a second Request action.

Secondary actions may include:
- Favorite;
- Add to Collection;
- Details.

Do not overload the surface with many permanent actions.

## 5. Request integration

Preview reuses the canonical Request contract.

When acquisition intent is unambiguous:
- one-click Request may start directly.

When Scope / Language / Edition selection is still required:
- open the shared Request dialog with the Work already resolved.

During request/acquisition:
- the primary request action becomes the same live user-facing state projection used elsewhere;
- examples: Requested, Looking for media, Downloading 32%, Preparing, Available;
- do not expose Wanted/indexer/download-client internals.

Preview does not own a second Request state machine.

## 6. Video trailer behavior

Trailer autoplay occurs only when the Preview itself is open and sufficiently visible.

Default:
- autoplay;
- muted;
- one active trailer at a time;
- stop/unload when Preview closes;
- do not continue hidden trailer playback behind another Preview.

If autoplay is blocked by the browser/platform:
- show the hero frame with a Play Trailer action;
- the rest of Preview remains fully usable.

User-muted/unmuted preference may remain local/session-level where appropriate, but must not affect media audio-language preferences.

## 7. Trailer source strategy

Jularr should avoid proxying/streaming ordinary public trailers through the Jularr server when an approved external embed/source can deliver them directly.

Preferred conceptual order:

1. known official trailer reference from persisted metadata/provider evidence;
2. YouTube embedded trailer when supported/allowed;
3. another explicitly supported external trailer provider;
4. local/provider backdrop;
5. poster/cover with derived background treatment.

Jularr may persist trailer metadata/reference locally, but the external provider remains responsible for serving externally embedded video.

Do not download/rehost a YouTube trailer merely to remove provider UI/branding.

## 8. YouTube embed contract

When YouTube is used:

- use the official embedded/IFrame player integration;
- autoplay may be used only when the Preview player is actually visible;
- autoplay begins muted by default;
- only one YouTube player may autoplay at a time;
- `controls=0` may be used where suitable to reduce standard controls;
- Jularr must not assume YouTube branding can be removed entirely;
- deprecated branding-hiding parameters are not part of the contract;
- do not cover/obscure the embedded player with arbitrary custom overlays;
- provider attribution/required player behavior must remain compliant;
- closing Preview destroys/stops the embed cleanly;
- instantiate the external player only when Preview opens, not for every card hover.

If provider policy/platform behavior conflicts with Jularr's desired minimal chrome, provider requirements win and Jularr falls back to a compliant presentation rather than hacking around them.

## 9. Minimal trailer controls

The Preview trailer region should stay visually quiet.

Where supported and compliant, expose only:
- Mute / Unmute;
- optional Replay;
- optional Fullscreen / open external player where useful.

Do not recreate the full Jularr playback toolbar for a trailer.

The trailer is promotional context, not an `ActiveSession` and does not create MediaProgress.

## 10. Trailer privacy / loading

External trailer embeds can contact third-party services.

Therefore:
- do not instantiate them for cards merely present in the grid;
- instantiate on explicit Preview open or stable TV preview activation;
- honor instance/profile privacy settings if such a policy is introduced;
- a blocked external trailer must degrade to artwork without breaking Preview.

Trailer availability is optional.

## 11. Reading Preview

Reading Preview uses the same modal/sheet shell but adapts the hero/content.

Show:
- backdrop/illustration or cover composition;
- Work title;
- media type;
- current publication status;
- language/Edition availability;
- short synopsis;
- current progress if already started;
- next useful structure context, e.g. `Vol. 4 · Ch. 29`;
- Read / Continue Reading or Request;
- Favorite;
- Collection;
- Details.

Do not show Reader controls, typography controls, TOC or annotations.

Those belong inside Reader.

## 12. Video Preview

Video Preview may show:
- trailer hero;
- poster;
- Work title;
- year;
- Movie / Anime / Series;
- runtime or season/episode summary;
- compact audio/subtitle availability;
- short synopsis;
- progress if started;
- next relevant episode when useful;
- Play / Continue or Request;
- Favorite;
- Collection;
- Details.

The Preview is not the Player.

Starting Play/Continue closes Preview and enters the canonical Player flow.

## 13. Similar / related titles

The approved visual direction may show a small related/similar row below the main actions/content.

Rules:
- optional;
- small and subordinate;
- reuse normal media-card identity;
- selecting a related title updates/opens Preview or Detail predictably;
- never turn Preview into an endless recommendation page.

On Mobile the row may be omitted when space is constrained.

## 14. Desktop composition

Approved direction:

- centered large cinematic overlay over dimmed Discover/Home content;
- wide 16:9 hero/trailer on top;
- poster overlaps/anchors the lower identity area where useful;
- title + compact metadata;
- short synopsis;
- primary action row;
- optional small related row;
- close button top-right;
- background page retains position and does not reflow.

The overlay should feel like a streaming-platform Quick View, not a settings dialog.

## 15. Mobile composition

Use a large bottom sheet / near-fullscreen sheet:

1. trailer/backdrop at top;
2. close action;
3. title + compact metadata;
4. short synopsis;
5. primary actions;
6. optional small related row.

The sheet scrolls when necessary.

Bottom navigation is obscured only while the modal sheet owns the interaction.

## 16. Tablet composition

Portrait:
- Mobile-like sheet.

Landscape:
- larger centered dialog/overlay, closer to Desktop.

No pointer-only behavior required.

## 17. TV composition

TV uses the same data but a remote-first presentation.

Recommended:
- large preview/hero region adjacent to or above browse rows;
- trailer after stable focus delay;
- Play/Continue/Start watching/Request and Details as large focusable actions;
- no tiny metadata chips;
- no trailer restart while focus is still moving quickly;
- one active trailer maximum.

Back returns exactly to the prior card focus.

## 18. Clean / Original Jularr

Both skins use the same Preview behavior and structure.

### Clean
- neutral surfaces;
- purple default accent;
- media artwork/trailer provides most color;
- no anime mascot or Japanese decorative background by default.

### Original Jularr
- same cinematic layout;
- red/pink default accent;
- restrained ink/sakura theme tokens may frame the app around the media;
- media trailer/backdrop remains visually dominant;
- do not cover the trailer with decorative theme art.

Theme skin never changes trailer provider logic or available actions.

## 19. Light / Dark

Both supported.

Preview may darken the surrounding application regardless of base mode to preserve cinematic focus, but:
- text/control contrast must remain accessible;
- the underlying theme remains identifiable;
- do not permanently force the profile into Dark mode.

## 20. Loading / states

Support:

- Preview data loading;
- trailer metadata loading;
- trailer unavailable;
- external trailer blocked/fails;
- partial provider metadata;
- discover-only Work;
- local/available Work;
- requested;
- downloading/preparing;
- missing backdrop/poster;
- permission-restricted action;
- Work removed while Preview is open.

Trailer failure should fall back to artwork without replacing the whole Preview with an error state.

## 21. Canonical identity

Preview always resolves around canonical Work identity.

Provider-native search entries may supply a presentation target, but:
- no duplicate Work is created by Preview;
- durable actions target canonical Work/Edition/structure identity;
- provider IDs remain evidence/mapping details;
- Details opens the canonical detail page;
- Play/Read/Listen resolves through canonical availability/session contracts.

## 22. Performance

- do not instantiate external players for every visible card;
- do not preload many trailers simultaneously;
- only one autoplay trailer;
- cancel/unload stale Preview media promptly;
- metadata/artwork may use normal cached/persisted provider data;
- opening/closing Preview must not re-layout the card grid.

## 23. Accessibility

- modal/sheet has proper dialog semantics;
- focus is trapped while open where appropriate;
- close returns focus to the source card;
- autoplay trailer starts muted;
- trailer controls are keyboard/touch/remote reachable where exposed;
- reduced-motion/autoplay preference should be respected when available;
- Preview actions have explicit text/accessible names;
- metadata is not color-only.

## 24. Must not implement

- no card expansion/reflow on ordinary Desktop hover;
- no trailer instantiation on casual hover;
- no mandatory Preview before Detail;
- no separate playback/progress session for trailers;
- no proxy/rehost of YouTube solely to remove provider UI;
- no attempts to illegally/unsupportedly hide provider branding;
- no custom overlays obscuring YouTube embedded player UI;
- no multiple autoplay trailers;
- no Admin/acquisition internals;
- no full detail-page metadata dump;
- no Reader controls in Reading Preview;
- no full Player toolbar in trailer Preview.

## 25. Approved mockup direction

Approved visual direction:

- cinematic streaming-platform style;
- large Desktop Video Preview overlay;
- Mobile Video Preview as large bottom sheet;
- Desktop Reading Preview using the same shell without forced video;
- darkened background context;
- large media hero;
- compact metadata/actions;
- optional small related titles row;
- no hover-driven card expansion.

The owner will upload the approved mockup image into this folder.

Text specification wins over imagery on conflict.


## Instant Play integration

Binding specification: `docs/mockups/instant-play/SPEC.md`.
