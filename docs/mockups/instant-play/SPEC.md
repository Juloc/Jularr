# Instant Play / Start Watching — Consumer Orchestration

Status: **approved UX direction; binding planning specification**.

Issue/context: #596, #396, #403.  
Request creation: `docs/mockups/add-request-flow/SPEC.md`  
Request status: `docs/mockups/request-status-details/SPEC.md`  
Player: `docs/mockups/player/SPEC.md`

## 1. Purpose

Jularr supports two valid installation styles with the same acquisition backend:

1. **Playback-enabled Jularr** — Jularr can acquire missing media and play it itself.
2. **Manager-only Jularr** — Jularr replaces Seerr/Sonarr/Radarr-style discovery, requests, monitoring and acquisition, but this Jularr instance does not offer playback.

These modes must not create separate Request, Wanted, Search, Download or Import implementations.

The consumer rule is:

- **Request** remains the only explicit consumer acquisition action.
- **Play / Continue / Start watching** are playback intents.
- When a playback intent targets media that is not local and policy permits instant acquisition, Jularr may transparently create or reuse the canonical Request, auto-approve it according to policy, prioritize the required playable unit and continue through the normal acquisition pipeline.
- Instant Play never creates a second acquisition pipeline or a second state store.

## 2. Capability resolution

The primary action is derived server-side from effective capability and current media state.

Effective capability is resolved from:

`instance module -> authorization/capability -> profile preference -> feature/policy state -> target availability`

At minimum the decision considers:

- media-type module enabled;
- Acquisition enabled;
- Playback available/enabled for this instance/client;
- user allowed to Request;
- user/policy allowed to auto-approve the Request;
- target already local/playable;
- an equivalent Request/acquisition already active;
- selected language/Edition intent;
- storage/acquisition health where it changes the valid action.

The UI never infers permission from hidden controls alone.

## 3. Primary-action matrix

| State | Primary consumer action |
| --- | --- |
| Local/playable + no resume | **Play** |
| Local/playable + resume | **Continue** |
| Series with next playable unit local | **Start watching** / **Continue** according to progress |
| Missing + Playback enabled + instant acquisition permitted | **Start watching** for Series, **Watch now** for Movie, equivalent explicit play intent for a selected episode |
| Missing + Request allowed but approval/manual request required | **Request** |
| Missing + Playback unavailable/disabled on instance + Request allowed | **Request** |
| Equivalent Request already active | show the current Request/acquisition state; do not create another Request |
| Acquisition disabled/not permitted | no acquisition action; show only valid personal/detail actions |
| Available on manager-only instance | **Available** state; no Jularr Play button |

A card click/tap itself never starts acquisition. It opens Detail/Preview. Acquisition starts only from an explicit Request or playback intent.

## 4. What Start watching means for a Series

`Start watching` acquires **the next required playable episode**, not the entire Series.

Target resolution:

- no watch history -> first eligible episode;
- completed through episode N -> next eligible episode after N;
- resumable episode exists -> Continue that episode instead of acquiring another;
- user explicitly selects an episode -> that canonical WorkEpisode is the target;
- unavailable specials/alternate branches are not guessed.

Example:

`Start watching -> S1E1`

After completing S1E4:

`Start watching -> S1E5`

The Start Watching intent does not implicitly request every historical or future episode. Broader acquisition comes only from the saved Request Scope, monitoring rules or explicit user/Admin actions.

Jularr may prepare/prefetch a following episode only under an approved playback/acquisition policy. Such prefetch never changes monitoring intent and never causes the whole Series to be downloaded merely because playback started.

## 5. Playback-enabled missing-media sequence

When instant acquisition is allowed, the visible consumer sequence is deliberately simple:

```text
Start watching / Watch now
-> Looking for media…
-> Getting episode/movie/media… [optional reliable progress]
-> Preparing…
-> Starting playback…
-> Player
```

These are **consumer projections**, not new backend states.

### Looking for media

Covers internal work such as:

- Wanted creation/reuse;
- search;
- identity matching;
- candidate validation;
- release scoring/selection;
- retrying another acceptable candidate within policy.

Normal UI label:

`Looking for media…`

Optional quiet sub-label may rotate to a generic message such as:

`Choosing the best match…`

Do not expose candidate names, indexers, scores or release groups.

### Getting media

Use a media-specific label where it reads naturally:

- `Getting episode…`
- `Getting movie…`
- `Getting media…`

This projects transfer/download work without implying an offline download to the user's device.

Do **not** use `Downloading` as the primary consumer label for this instant-play flow.

Show percentage, transferred size, item count or ETA only when the value is reliable and meaningful. Never invent progress.

### Preparing

`Preparing…`

Covers consumer-irrelevant processing such as verification, extraction, import, library registration, technical probing and final playable-file resolution.

Do not expose `Importing`, extractor names, filesystem paths or post-processing phases in the normal view.

### Starting playback

`Starting playback…`

Used only after canonical media is playable and Jularr is resolving/starting the PlaybackPlan/ActiveSession.

It is never shown on manager-only instances.

## 6. Primary button transformation

The existing primary action morphs in place instead of opening a technical job tracker.

Example:

```text
▶ Start watching
      ↓
◌ Looking for media…
      ↓
◔ Getting episode · 42%
      ↓
◌ Preparing…
      ↓
▶ Starting playback…
```

Rules:

- preserve button position/width as much as practical;
- no page layout jump;
- subtle spinner/ring/progress fill is allowed;
- reliable acquisition progress may fill a thin line/ring associated with the action;
- status remains readable without animation;
- reduced-motion preference disables decorative motion;
- the button/status remains keyboard, touch and TV-focus accessible.

The progress treatment must not resemble the Player's playback/buffer timeline.

## 7. Optional consumer Details

A compact `Details` disclosure may expose generic milestones only.

Allowed examples:

- Request approved;
- Looking for media;
- Suitable media found;
- Getting episode · 42%;
- Preparing for playback;
- Ready to watch.

Optional safe facts:

- requested language/Edition summary;
- selected episode/season;
- trustworthy total size/ETA;
- request time;
- next known future release.

Never expose:

- indexer/provider credentials or IDs;
- NZB/release names;
- release group/scoring breakdown;
- SABnzbd/download-client identity;
- operation/job IDs;
- server filesystem paths;
- extractor/importer names;
- raw exceptions/logs.

Technical detail belongs to Admin Wanted / Manual Search / Activity.

## 8. Leaving the surface while acquisition runs

A playback intent may outlive the current Detail/Preview surface.

If the user remains in the originating playback context until Ready:

- transition to `Starting playback…`;
- open the Player automatically.

If the user navigates elsewhere before Ready:

- acquisition continues according to canonical Request/monitoring ownership;
- **do not hijack navigation and suddenly open the Player**;
- clear the transient auto-start intent for that surface/session;
- show `Ready to watch` on the relevant media state when complete;
- a normal notification may say the media is ready, according to notification settings.

Returning to the media exposes Play/Continue normally.

## 9. Stop waiting vs Cancel Request

These are distinct.

### Stop waiting

Shown only while the current surface/session is waiting to auto-start playback.

`Stop waiting`:

- stops the transient auto-start/wait intent;
- returns the user to normal browsing/detail state;
- does **not** claim to cancel a shared acquisition;
- does not remove monitoring;
- does not cancel Requests from other profiles.

### Cancel Request

Lives in Request Status/Details where permitted.

It removes this profile's Request intent according to Request rules. Shared acquisition may continue if another Request or monitoring rule still requires it.

Never label the transient action `Cancel download`.

## 10. Failure/recovery vocabulary

Keep failures user-readable.

Examples:

- `Couldn't prepare this episode`
- `Couldn't get this movie`
- `Not available yet`
- `Status temporarily unavailable`

Actions may include:

- Retry;
- Keep looking, where monitoring/policy supports it;
- View details;
- Back.

Do not show raw downloader/import errors.

If no acceptable release exists but monitoring continues, prefer:

`Not available yet`  
`We'll keep looking.`

## 11. Manager-only / playback-disabled instance

A Jularr installation may intentionally provide discovery, Request, monitoring, Wanted, search, download and import without Jularr playback.

The consumer flow is then:

```text
Discover / Detail
-> Request
-> Waiting for approval (when required)
-> Looking for media
-> Getting media [optional progress]
-> Preparing
-> Available
-> Monitoring future releases (when applicable)
```

Rules:

- never show `Start watching`, `Watch now`, `Starting playback` or Jularr Player controls;
- missing media uses the normal Request action;
- owner/privileged auto-approval may make Request progress immediately without a different Add button;
- successful acquisition ends at `Available`;
- `Available` means the requested content is imported/managed by Jularr, not necessarily that this Jularr client can play it;
- future monitoring can coexist with `Available now`;
- an optional future external-media-server integration may expose `Open in …` only when explicitly configured and supported; it is not required for V1.

Manager-only mode must remain a first-class supported product mode, not an error/degraded Player mode.

## 12. Request relationship

Instant Play still creates/reuses the same canonical Request intent when acquisition is necessary.

When policy permits auto-approval:

```text
Playback intent
-> create/reuse Request
-> auto-approve
-> Wanted
-> Search
-> Download/transfer
-> Import
-> Available
-> Playback
```

When approval is required, the UI uses **Request** rather than pretending Start Watching can immediately play.

The explicit Request dialog remains the place for broader Scope, language/Edition and optional privileged overrides.

Instant Play should use safe defaults/profile preferences and the smallest required playable target. It does not silently invent a broad Series monitoring scope.

## 13. Existing active Request/acquisition

If an equivalent target is already being acquired:

- Start Watching attaches a transient playback wait intent to the existing canonical acquisition;
- do not submit a duplicate Request/download;
- show the same consumer projection;
- when Ready, auto-start only if the user still owns an active wait intent in the relevant context.

If an existing Request requires approval, show the saved Request state rather than bypassing approval.

## 14. Partial Series availability

A Series may be partly local.

Rules:

- if the next required episode is local, start immediately;
- if later episodes are missing, do not block current playback;
- if the next required episode is missing and instant acquisition is permitted, use the instant sequence;
- episode rows may independently show Play/Continue, Request, Getting/Preparing or unavailable state;
- the Work-level action always resolves from canonical progress + next eligible WorkEpisode.

## 15. Desktop

Light and Dark are first-class; **Light is the primary new mockup baseline for this contract**.

Desktop behavior:

- Detail/Preview remains visible;
- primary hero action morphs in place;
- optional compact status/detail disclosure appears adjacent/below without turning into a job dashboard;
- user may continue browsing the Detail page;
- navigating away stops only the transient auto-start intent, not shared acquisition;
- when still present and Ready, transition directly into Player.

## 16. Mobile

Mobile uses the same semantics.

Preferred composition:

- Detail/Preview sheet/page remains visible;
- large touch-safe primary action morphs in place;
- compact status text + optional progress immediately near the action;
- generic Details opens a bottom sheet/disclosure;
- do not force a full-screen technical progress page;
- leaving the page prevents surprise auto-open later;
- Ready state becomes Play/Continue if the user returns.

## 17. Tablet

Tablet follows Desktop when wide and Mobile sheet grammar when narrow.

Do not introduce a tablet-only state model.

## 18. TV

TV is remote-first.

- Detail/Preview shows one large focusable primary action;
- Start Watching may trigger the instant sequence;
- status remains in the hero/action region rather than a dense modal;
- no pointer-only disclosure;
- optional Details is a simple focusable secondary action with generic milestones;
- if the user navigates away to another title/row before Ready, do not steal focus/open Player later;
- if the user remains in the same playback intent context, Ready may transition directly to Player;
- manager-only TV surfaces show Request/Available, never disabled Player chrome.

## 19. Light-mode visual contract

For the requested Light mockups:

- white/off-white application background;
- subtle neutral cards/surfaces;
- purple Jularr accent for primary action/progress;
- semantic amber/red/green only for waiting/error/ready meaning;
- no dark cinematic full-page treatment merely because media is video;
- artwork/backdrop may remain rich but text/actions sit on accessible light surfaces/gradients;
- acquisition progress is restrained, not a saturated dashboard;
- same hierarchy on Desktop/Mobile/TV.

Dark mode later mirrors behavior, not layout.

## 20. Admin boundary

Admin continues to expose the detailed technical lifecycle where authorized:

- Wanted;
- Search attempts;
- candidate/rejection reasons;
- downloader/client;
- Operations;
- import results;
- retries/blocklists.

Consumer instant-play UX must never duplicate that control plane.

## 21. State naming authority

For normal consumer acquisition, prefer:

- Waiting for approval
- Looking for media
- Getting episode/movie/media
- Preparing
- Ready to watch / Available
- Monitoring future releases
- Needs attention / Not available yet

`Downloading` and `Importing` remain valid Admin/technical terms but are not the preferred primary labels for consumer Instant Play.

`Starting playback` exists only as a transient playback-enabled state after media is Ready.

## 22. Required Light mockups

Create two canonical reference boards:

### A. Playback enabled, selected media not local

Show the same Series flow on Desktop, Mobile and TV:

`Start watching -> Looking for media -> Getting episode -> Preparing -> Starting playback -> Player`

Include:

- in-place action transformation;
- optional generic Details;
- Stop waiting distinction;
- reliable progress example;
- rule that leaving the surface prevents surprise auto-start.

### B. Manager-only / Playback disabled

Show Desktop, Mobile and TV:

`Request -> approval if needed -> Looking for media -> Getting media -> Preparing -> Available -> Monitoring future releases`

Include:

- no Start Watching;
- no Player;
- Request remains the explicit acquisition action;
- Available + Monitoring may coexist;
- owner auto-approval uses the same Request action.

## 23. Acceptance

This contract is satisfied only when:

- playback-enabled missing media can be started with one explicit playback intent where policy allows;
- the intent uses the canonical Request/Wanted/acquisition pipeline;
- only the smallest required playable Series unit is prioritized by Start Watching;
- no duplicate Request/download is created;
- consumer states hide technical acquisition internals;
- `Downloading` is not required as the main instant-play label;
- reliable progress can be shown without fake percentages;
- leaving context never causes surprise Player navigation later;
- Stop waiting does not falsely cancel shared acquisition;
- manager-only instances work cleanly without any Playback UI;
- Desktop, Mobile, Tablet and TV share one semantic state model;
- Light/Dark presentation changes only styling/composition, never workflow ownership.

## 24. Must not implement

- no second Instant Acquisition engine;
- no `Add & Play` pipeline;
- no whole-Series download merely because Start Watching was pressed;
- no implicit approval bypass;
- no duplicate Request when one already exists;
- no consumer release picker/indexer/downloader detail;
- no `Cancel download` promise for shared work;
- no auto-opening Player after the user navigated away;
- no Player controls on manager-only installations;
- no separate Desktop/Mobile/TV business rules.
