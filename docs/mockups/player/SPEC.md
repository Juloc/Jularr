# Player — Cross-platform Clean Design

Status: **binding planning specification for Player mockups**. This document refines the baseline in `UX.md` and issue #403. It does not authorize feature implementation before the canonical Playback/File/Track/Progress contracts are ready.

Architecture source of truth:
- `Work -> Structure -> Edition -> Version -> Asset -> File -> Track`
- `PlaybackPlan` decides Direct Play / Direct Stream or Remux / Transcode / Unavailable.
- `ActiveSession` owns the active playback session.
- canonical `MediaProgress` owns resume/completion state.
- learning subtitles are a presentation/learning layer over canonical subtitle cues, not another playback model.

## 1. Purpose

Provide one clean playback experience for canonical playable Assets while preserving the same server-owned playback/session semantics across Web/PWA, Android phone/tablet, TV and iOS/iPadOS WebKit.

The Player must:
- start or resume the selected canonical Work/unit;
- expose only the controls relevant to normal playback;
- make audio, subtitle, quality and speed choices easy to reach without permanent technical clutter;
- support interactive learning subtitles without coupling Learning to player chrome;
- recover from buffering, storage and playback-plan changes without losing meaningful progress;
- keep platform interaction deliberately different where touch, pointer/keyboard, remote and WebKit require it.

The Player is not an Admin diagnostics page and does not choose its own media-processing strategy.

## 2. Route / entry contract

Entry can come from:
- Continue / Up Next;
- Anime/Series episode action;
- Movie Play/Continue;
- Audiobook Listen/Continue where the shared playback shell is appropriate;
- a compact Now Playing continuation surface;
- a deep link to a canonical playable unit.

The route receives canonical identity, never a raw filesystem path or legacy Anime/Episode-only playback identity.

Before playback the application resolves:
1. canonical Work/unit;
2. user/profile and playback preferences;
3. available Version/Asset/File/Track choices;
4. client capability document;
5. PlaybackPlan;
6. ActiveSession;
7. exact resume position.

Opening an item sets/updates the current item and resume state but **does not mark it completed**.

## 3. Primary page structure

The full Player is a media-first surface with five conceptual layers. These layers must stay independent so hiding controls never hides subtitles or learning UI.

### Layer A — media surface

Full available viewport behind all controls.

Video:
- preserve source aspect ratio by default;
- support Fit / Fill / Zoom;
- no decorative poster frame while video is playing;
- letterbox/pillarbox uses player background, not application-page chrome.

Audio-only mode:
- use a restrained artwork/backdrop treatment;
- keep the same transport/session model;
- do not invent a separate audio progress store.

### Layer B — subtitle layer

Normal subtitle rendering is centered in the safe subtitle zone and remains independent of transient controls.

Requirements:
- readable against both bright and dark frames;
- safe distance from bottom timeline/control chrome;
- preserve positioning/styling where supported and appropriate;
- embedded, external and generated subtitle provenance is not shown as permanent badges.

### Layer C — optional interactive Learning layer

Learning is an **optional live Player mode**, not a replacement Player and not a permanent modification of normal playback.

The Learning control exists only when all of these are true:
- the **instance Learning module** is enabled;
- the current **profile has Learning enabled for itself**;
- the profile is permitted to use the relevant Learning capabilities;
- the current content exposes usable Learning/subtitle data for the requested interaction.

If any of those conditions is false:
- no Learning toggle is shown;
- no hidden Learning hit targets remain active;
- no Learning panel, cue styling or gesture override affects playback;
- the Player behaves exactly like the normal non-Learning Player.

### Normal mode vs Learning mode

**Learning Off** is the normal Player:
- normal subtitles behave normally;
- Learning cues are not interactive;
- no Learning popover/sheet/sidebar is present;
- normal click/tap/seek behavior is unchanged;
- Learning must not reserve extra layout space or alter playback controls beyond the availability of the toggle itself.

**Learning On** activates only the Learning presentation/interaction layer:
- interactive cues become available;
- word/sentence inspection becomes available;
- Save / Learn / Known / Ignore and other permitted Learning actions can be used;
- Learning subtitles remain visible when transient playback controls auto-hide.

The user can toggle Learning **On/Off while playback is running**.

Toggling Learning must **not**:
- recreate the PlaybackPlan;
- recreate the ActiveSession;
- seek or reset playback position;
- change selected audio/subtitle tracks;
- change quality, speed or display mode;
- mark progress/completion differently by itself.

Turning Learning Off:
- closes any open Learning popover/sheet/panel;
- removes Learning hit targets immediately;
- returns subtitle interaction to normal playback behavior;
- preserves the same ActiveSession and position.

Turning Learning On:
- enables the Learning layer in-place;
- does not pause playback merely because the mode was enabled;
- does not force the user into an inspector or lesson.

### Initial Player state

The default session state is **Learning Off** so normal watching is never implicitly replaced by study behavior.

A future explicit user preference such as `Start Player in Learning mode` may opt into another initial state. Do not infer the initial state from historic Learning cards or merely from Learning being enabled for the profile.

### Learning interaction rules

When Learning mode is On:
- tapping a term/sentence must never trigger generic player tap/click gestures;
- opening a word/sentence detail preserves playback position, selected tracks and the pre-detail paused/playing state;
- closing the detail returns to the same ActiveSession;
- if Learning data becomes unavailable, normal playback remains fully usable.

Desktop can use a compact anchored popover/side panel. Tablet landscape may use a side sheet. Mobile uses a bottom sheet. TV uses a remote-focusable overlay/panel with large targets and no pointer assumptions.

### Layer D — transient player chrome

Top chrome:
- Back/Close;
- concise Work + unit title;
- optional episode/chapter context;
- Cast / PiP / overflow only when actually supported;
- no permanent technical playback details.

Center:
- large Play/Pause affordance while controls are visible where the platform composition uses an over-video transport;
- contextual **-10 seconds / +30 seconds** seek feedback;
- buffering indicator only when buffering is real.

Bottom:
- current time;
- timeline / seek bar;
- duration / remaining time where appropriate;
- platform-specific transport and settings arrangement as defined below;
- volume only where the platform/input model calls for a visible volume control;
- secondary actions for audio, subtitles, quality, speed and display mode;
- fullscreen where platform supports custom fullscreen.

The global manual seek increments are:
- **Back: 10 seconds**
- **Forward: 30 seconds**

Clients must not silently use different default seek increments for the same actions.

Controls auto-hide only during active playback and only when no menu, sheet, learning surface or focused TV control requires them.

### Layer E — sheets / menus / diagnostics

Menus never navigate away from the active Player.

Shared menu/action groups:
- Audio;
- Subtitles;
- Quality;
- Speed;
- Fit / Fill / Zoom;
- Chapters / skip segments where available;
- Diagnostics in overflow;
- **Learning On/Off** as a real playback-mode toggle when instance + profile + capability + content allow it.

Learning is a relatively frequent mode switch when available and must not be buried only in deep Settings:
- Desktop: direct action in the right-side Player action group;
- Tablet: direct action in the settings/action row below the timeline;
- Mobile: compact direct Learning action while chrome is visible; the general Settings gear remains for low-frequency settings;
- TV: direct focusable Learning action in the settings row.

The toggle uses an explicit selected/on state (`aria-pressed` or platform equivalent) and a clear label/icon; it must not look like a one-shot command.

Only one secondary panel should be open at a time.

## 4. Desktop composition

Pointer + keyboard first.

### Layout
- media fills viewport;
- top title row is compact;
- timeline spans most of the bottom width;
- the lower controls are divided into **three independent zones**:
  1. **left:** volume/mute;
  2. **center:** Previous, -10 seconds, Play/Pause, +30 seconds, Next;
  3. **right:** subtitles, audio, quality, speed, chapters, PiP/fullscreen/overflow as supported;
- the center transport group is geometrically centered in the viewport and does not shift when right-side settings appear/disappear;
- Play/Pause may be slightly larger than adjacent transport controls, but must not become a giant permanent toolbar centerpiece;
- volume never moves into the centered transport group;
- audio/subtitle/quality/speed use compact anchored menus;
- diagnostics opens as an on-demand side panel or modal, not permanent text.

Preferred Desktop control grammar:

`Volume | Previous · -10 · Play/Pause · +30 · Next | Subtitles · Audio · Quality · Speed · Chapters · PiP · Fullscreen · More`

Timeline sits directly above this control row. Chapter names/thumbnails are not permanently expanded above the timeline; Desktop hover/focus may show the current/hovered chapter preview on demand.

### Pointer behavior
- single click on unobstructed video = Play/Pause;
- double click on unobstructed video = enter/exit fullscreen;
- moving pointer reveals controls;
- moving pointer out / inactivity hides controls while playing;
- click on subtitle-learning content never falls through to video;
- timeline hover shows time preview and thumbnail when available;
- chapter/segment markers may appear on the timeline without turning it into a dense editor.

### Keyboard baseline
- Space / K: Play/Pause;
- Left: -10 seconds;
- Right: +30 seconds;
- J/L may map to the same backward/forward increments where supported by the global shortcut policy;
- Up/Down: volume;
- M: mute;
- F: fullscreen;
- Escape: close menus/fullscreen before leaving Player.

Do not introduce shortcuts that conflict with text input or accessibility behavior.

### Desktop mockup states required
1. playing with chrome visible;
2. playing with chrome hidden + learning subtitles visible;
3. audio/subtitle menu open;
4. diagnostics panel;
5. buffering/error overlay.

## 5. Mobile composition

Touch-first. Portrait and landscape are intentionally different compositions.

### Portrait
- video/media surface occupies the upper available area when not fullscreen;
- over-video transport centers on **-10 seconds / Play-Pause / +30 seconds**;
- Previous/Next are not required as permanent portrait controls and may move into the settings/more sheet when width is constrained;
- secondary controls are consolidated behind a **single Settings gear** rather than a dense icon row;
- safe areas and browser/PWA chrome are respected;
- compact title/context at top.

### Landscape / fullscreen
- media uses the full safe viewport;
- transport overlays the media and remains centered;
- preferred narrow layout: **-10 / Play-Pause / +30**;
- sufficiently wide landscape may add Previous/Next symmetrically around that group;
- timeline stays reachable without crowding subtitle safe zones;
- only a Settings gear plus fullscreen/window actions remain outside the transport where possible;
- learning details use a bottom/side sheet depending on available width.

### Mobile top/window actions
When the full custom Player owns the screen:
- **top left: Minimize** — collapse playback into Jularr's bottom mini-player bar and reveal the normal app;
- **top right: Popout** — move playback into the supported platform popout/PiP/detached-player mode so the app can be hidden/backgrounded where supported;
- **top right beside Popout: Close** — stop/close the full Player and return to the prior Jularr context.

Minimize, Popout and Close are distinct actions and must not share ambiguous icons/behavior.

### Mobile Settings
A single Settings gear opens the touch sheet containing supported controls such as:
- Subtitles;
- Audio;
- Quality;
- Speed;
- Chapters;
- Fit / Fill / Zoom;
- Auto-Skip options;
- other low-frequency playback settings.

Do not permanently line up all Desktop settings as tiny Mobile buttons.

### Touch behavior
- single tap toggles controls only;
- double tap left = -10 seconds;
- double tap right = +30 seconds;
- double tap center must not be overloaded;
- left vertical gesture may control brightness only where the client can reliably support it;
- right vertical gesture may control volume only where supported;
- Mobile has no permanent volume slider when gesture/system volume is available;
- volume/brightness gestures show brief transient percentage feedback only;
- pinch controls Fit/Fill/Zoom where supported;
- screen/control lock disables accidental transport gestures and exposes a clear unlock affordance;
- interactive subtitle taps take precedence over player gestures.

No gesture is allowed to silently perform an unsupported platform action.

### Mobile mockup states required
1. portrait paused;
2. portrait playing + controls;
3. landscape playing + controls;
4. locked-controls state;
5. learning bottom sheet;
6. track/quality bottom sheet;
7. recoverable playback error.

## 6. Tablet composition

Tablet keeps the touch interaction model but deliberately does **not** reuse the Desktop control bar.

### Shared Tablet window actions
- **top left: Minimize** — collapse to the Jularr bottom mini-player and reveal the normal app;
- **top right: Popout** — use the platform's supported PiP/popout/detached-player mode;
- **top right beside Popout: Close** — close playback and return to the previous Jularr context.

### Portrait
- close to Mobile with wider sheets;
- over-video transport remains centered;
- transport baseline: Previous where space allows, -10 seconds, Play/Pause, +30 seconds, Next where space allows;
- timeline stays below the video-centered transport;
- low-frequency playback settings live below the timeline or in a touch sheet;
- no permanent volume slider.

### Landscape
- full media-first composition;
- the transport group is centered **on the video**, not in the bottom settings row;
- preferred transport: **Previous · -10 · Play/Pause · +30 · Next**;
- below the timeline, show only supported settings such as Subtitles, Audio, Quality, Speed, Chapters and Fullscreen;
- volume is controlled through system buttons and/or the right-side vertical gesture where supported;
- brightness may use the left-side vertical gesture where supported;
- learning details may use a right side sheet while video remains visible;
- audio/subtitle/quality sheets can be narrower anchored side sheets rather than full-width mobile sheets;
- no desktop-only hover assumptions.

Tablet controls therefore have two distinct layers:
1. **video center = transport**;
2. **below timeline = settings**.

iPadOS Safari/PWA follows the WebKit compatibility rules below.

## 7. TV composition

Remote-first. TV is not a scaled Desktop Player and does not use touch/mobile window controls.

### TV layout hierarchy
- media fills the TV safe area;
- compact title/context at the top only while chrome is visible;
- contextual **Skip Intro/Recap/Outro/Credits** appears as a large first-class focusable action near the lower-right safe area;
- one wide timeline row sits above the control rows;
- played, buffered, remaining, chapter markers and eligible skip segments remain distinguishable at viewing distance;
- current/duration times remain readable but subordinate;
- below the timeline, the **primary transport row is geometrically centered**:
  **Previous · -10 seconds · Play/Pause · +30 seconds · Next**;
- a second centered row contains supported settings:
  **Subtitles · Audio · Quality · Speed · Chapters · More**;
- do not show a volume slider as a primary TV control; TV/system volume remains owned by the device/remote unless the platform specifically exposes in-app volume;
- no tiny status icons, hover-only affordances or dense Desktop menus.

### TV focus
Focused controls use the approved TV language:
- strong accent focus ring/glow;
- modest scale/lift;
- high contrast;
- clear selected label/state;
- predictable D-pad path with no traps;
- focused control remains fully inside TV safe areas.

Default focus when controls first appear should land on Play/Pause unless a contextual action such as Skip Intro was explicitly invoked/focused by policy.

### TV remote behavior
- hardware/media Play/Pause key directly toggles playback;
- dedicated seek keys use the canonical increments where applicable: **back 10 seconds / forward 30 seconds**;
- when the primary transport row is focused, Left/Right moves focus between transport actions rather than unexpectedly seeking;
- selecting -10/+30 performs one discrete seek with visible feedback;
- when the timeline enters explicit seek/scrub mode, Left/Right adjusts time with clear on-screen position feedback;
- Up/Down moves predictably between timeline, transport row and settings row;
- OK/Select activates the focused action;
- Back closes the deepest open panel first, then exits seek mode/chrome, then leaves Player according to platform navigation policy;
- no action requires a mouse, hover state or text cursor.

### TV settings panels
Audio, Subtitles, Quality, Speed and Chapters open large remote-focusable panels/lists:
- current selection is obvious;
- focus returns to the invoking control when the panel closes;
- Chapters shows chapter title and start time with the current chapter selected;
- panels never expose technical codec/transcode details unless Diagnostics is explicitly opened through More.

### TV learning
Learning mode must be usable with D-pad focus.
- interactive subtitle focus must not collide with transport focus;
- opening a word/sentence detail pauses or preserves playback according to the shared learning policy;
- dense dictionary/explanation content opens in a dedicated remote-safe panel rather than covering the entire media surface by default;
- Back returns from learning detail to the same playback position/session.

### TV seek feedback
A discrete remote seek shows a short transient overlay such as:
- `-10s`
- `+30s`

Repeated presses may accumulate visually while the seek is being applied, but the canonical single-step increments remain 10 seconds backward / 30 seconds forward.

### TV mockup states required
1. playing with controls visible;
2. focused Play/Pause transport action;
3. explicit timeline seek/scrub state;
4. subtitle/audio/quality panel;
5. Chapters panel;
6. contextual Skip Intro action focused;
7. learning overlay;
8. playback failure/retry state.

## 8. iOS / iPadOS WebKit path

Use capability detection and progressive enhancement. Do not fork server/domain playback rules.

Where WebKit permits:
- keep Jularr inline controls;
- use normal PlaybackPlan/ActiveSession;
- support fullscreen, PiP, Media Session and wake-lock-like behavior only when the platform actually exposes it.

Where WebKit requires system surfaces:
- degrade deliberately to supported system playback/fullscreen controls;
- preserve canonical progress, track preferences and session identity;
- never claim a custom control exists when WebKit does not allow it;
- return from system fullscreen/native surfaces to the same logical ActiveSession.

The mockup represents the intended Jularr inline state, but implementation may use a system-control fallback for unsupported WebKit capabilities.

## 9. Timeline, buffer, chapters and skip segments

The timeline is the primary playback-position surface and must clearly separate **played**, **buffered** and **remaining** media.

Timeline must show:
- played position;
- buffered range whenever the delivery layer can report it reliably;
- remaining/unbuffered range;
- current time and duration;
- hover/seek preview on Desktop where available;
- chapter markers;
- canonical intro/recap/outro/credits/preview markers only when resolved and above the configured confidence threshold.

### Buffer visualization

Buffer must be visible directly in the timeline as a secondary fill/range behind the played position.

When playback actually stalls waiting for media:
- preserve the current frame where possible;
- show a centered buffering indicator after a short delay so tiny network fluctuations do not flash UI;
- if the stall becomes prolonged, add a concise `Buffering…` status;
- keep the timeline and valid controls accessible;
- do not show a fake percentage unless the delivery layer exposes a meaningful buffer/download value.

Buffer display is distinct from acquisition/download progress. Normal playback buffering must never expose indexer or download-client internals.

### Chapters

Chapters are visible in two places:
1. discrete markers on the timeline;
2. a **Chapters** menu/sheet listing chapter title and start time.

Behavior:
- selecting a chapter seeks to its canonical start position;
- the current chapter is clearly selected;
- Desktop hover/focus may show the chapter title near its marker;
- Mobile/Tablet use a touch-friendly sheet;
- TV uses a large D-pad-focusable chapter list;
- chapter markers must remain visually subordinate to the main played/buffered timeline.

### Manual skip actions

Eligible canonical segments expose contextual, real buttons:
- **Skip intro**
- **Skip recap**
- **Skip outro**
- **Skip credits**
- **Skip preview** where applicable

Rules:
- button appears only while current playback position is inside the corresponding eligible segment;
- button is directly reachable in player chrome, not hidden only in a settings menu;
- Desktop keeps it compact but obvious;
- Mobile/Tablet use a large touch target;
- TV exposes it as a first-class focusable action;
- the segment is also marked on the timeline;
- button disappears after leaving/skipping the segment;
- segment editing never happens inside the normal Player.

### Optional Auto-Skip

Playback settings include per-profile/user options:
- **Auto-skip intro** — Off/On;
- **Auto-skip recap** — Off/On;
- **Auto-skip outro/credits** — Off/On.

Default for all automatic skip options is **Off**.

When Auto-Skip is enabled:
- only canonical resolved segment markers may trigger it;
- configured confidence/policy thresholds still apply;
- Jularr seeks to the segment end automatically;
- show a brief non-blocking confirmation such as `Intro automatically skipped`;
- expose **Undo** for a short period, returning to the segment start;
- auto-skip must never mark an episode completed by itself;
- an unavailable/ambiguous segment is never guessed client-side.

These preferences belong to normal Playback settings and are reused across clients. They must not be implemented as separate per-platform rules.

Segment correction/editing is an Admin/Episode-detail concern.

## 10. Audio, subtitles and quality

### Audio
Each option shows user-meaningful data:
- language;
- track title when useful;
- channel/layout where helpful.

Prefer the profile/user default, then the PlaybackPlan-selected compatible track.

### Subtitles
Options:
- Off;
- available canonical subtitle tracks/files;
- learning subtitle mode where available.

Normal subtitle selection and learning subtitle interaction remain distinct concepts.

If the selected subtitle forces remux/transcode/burn-in, the server resolves a new PlaybackPlan. The client does not decide this locally.

### Quality
Normal choices:
- Automatic;
- Original;
- configured bitrate presets such as 20 / 12 / 8 / 4 / 2 / 1 Mbps where enabled.

Do not present resolution-only labels if the actual decision is bitrate/network constrained.

Changing quality requests a new plan/session delivery while preserving position and track selections.

## 11. Playback mode status and diagnostics

Normal UI may show a compact status in overflow/details:
- Direct Play;
- Direct Stream / Remux;
- Transcode;
- Automatic quality.

Detailed diagnostics are explicitly secondary.

Diagnostics may show:
- PlaybackPlan mode;
- exact reasons / “Why not Direct Play?”;
- source/delivered container and codecs;
- resolution/bitrate;
- selected audio/subtitle;
- throughput and buffer health;
- transcode encoder/speed when applicable;
- dropped frames/segment status where available.

Diagnostics must never expose raw filesystem paths, secrets or arbitrary FFmpeg command strings.

## 12. Progress and completion semantics

The Player writes through canonical session/progress contracts.

Keep separate:
- CurrentItem;
- ResumePosition;
- CompletedThrough;
- external ProviderProgress.

Rules:
- opening or starting item N does not mark N completed;
- pause/seek/resume does not mark N completed;
- periodic heartbeats update exact resume position;
- meaningful state flushes on pause, seek completion, visibility/background transition, route leave and Player close;
- completion occurs only through the canonical completion policy, explicit Mark Watched, or a safe sequential-next transition;
- provider write-back uses completed progress, never merely the current item/resume position;
- progress survives delivery changes between Direct Play/Remux/Transcode.

Example:
`Episode 12 at 08:31` may coexist with `CompletedThrough = 11`.

## 13. Previous / Next behavior

Previous/Next targets canonical structure.

Series/Anime:
- Next resolves the next canonical episode according to selected context and policy;
- beginning playback of the next episode may finalize the previous episode only when the canonical completion policy allows it.

Movie:
- no fake Next button unless there is an explicit playlist/queue context.

Audio/Audiobook:
- previous/next may target canonical chapter/track boundaries.

When the next item is not locally ready but user policy allows instant acquisition:
- show Preparing state;
- keep current/ended context visible;
- auto-start only after the normal Play/Read acquisition contract says the requested item is ready.

### Offline playback and unavailable next item

Offline playback is not a separate Player.

When the current item has a verified device-local offline package:
- open the normal Player using the same Work/Structure/Edition/Version/Asset/Track semantics;
- use the local verified media bytes and packaged tracks;
- retain the normal timeline, transport, chapters/segments, subtitle and Learning interaction model;
- store progress through the same canonical MediaProgress/ActiveSession semantics, with local-first persistence when server connectivity is unavailable;
- synchronize queued progress/session-safe state when connectivity returns;
- do not create a second offline playback history or offline-only progress owner.

A restrained state indicator may show:
- **Offline available / Offline verfügbar**; or
- a small verified-download/check icon.

When playback starts while the server/network is unavailable, a short non-warning status may say:

**Offline-Wiedergabe**  
`Diese Episode wird von diesem Gerät abgespielt.`

Do not show a large blocking "You are offline" banner while verified local playback is functioning normally.

#### Offline audio / subtitle tracks

Only tracks that are actually usable from the local offline package may be selected while offline.

A track that exists canonically but was not included in the offline package must either:
- be hidden from the active offline selector; or
- remain visible with a clear **Nicht offline verfügbar / Not available offline** state.

Do not allow selection and then fail with a generic network error.

Changing locally available tracks must keep using canonical MediaTrack identity and must not create an offline-only track model.

#### Offline Learning

When the downloaded package includes the bounded Learning data required by the current Player interaction:
- Learning subtitles/cues remain usable offline;
- supported token/sentence interactions work from the local package;
- learning writes that are safe offline queue locally and synchronize later through the canonical Learning sync path.

When Learning data was not included:
- playback remains fully usable;
- the relevant Learning action is hidden or disabled with concise copy such as **Learning-Daten für diese Episode sind nicht offline verfügbar**;
- do not block normal subtitles or playback.

#### Local progress while disconnected

During offline playback:
- position updates persist locally first;
- completion uses the same canonical completion policy;
- Mark Watched, where supported, queues through the same canonical path;
- provider write-back waits for connectivity and remains derived from canonical completed progress;
- reconnection must not blindly overwrite newer local progress with stale server progress.

A subtle sync-pending state may be exposed in diagnostics/status UI, e.g.:
`Fortschritt wird synchronisiert, sobald Jularr wieder verbunden ist.`

This is informational, not an error.

#### Next episode/chapter not downloaded

If the current item is locally playable but the canonical next item is **not** locally available and the client is genuinely offline:

- do not start an autoplay countdown that cannot succeed;
- do not show infinite buffering;
- do not open a browser/network error page;
- do not replace the completed item with a generic Player failure.

Instead, keep the current/ended media context visible and show a focused overlay/card:

**Nächste Episode nicht offline verfügbar**  
`E08 · Together Again`  
`Diese Episode wurde nicht auf dieses Gerät heruntergeladen.`

Actions:
- **Downloads anzeigen**;
- **Schließen**.

When the client is back online and the product permits explicit download from this context, a **Jetzt herunterladen** action may additionally appear. Do not show it while the request cannot actually be started.

For Audiobook/chapter-based audio use equivalent canonical-next wording.

#### Autoplay while offline

If autoplay is enabled but the next canonical item is not offline-ready:
- cancel/stop the autoplay transition before attempting playback;
- preserve completion of the current item according to canonical policy;
- surface the unavailable-next overlay;
- resume normal autoplay semantics only after a next item becomes genuinely playable.

Do not mark the next item started/completed merely because the autoplay timer fired.

#### Corrupt/incomplete local package

If an item previously appeared downloaded but its local package fails verification or cannot provide a valid playback descriptor:

**Offline-Kopie kann nicht abgespielt werden**

`Die heruntergeladene Datei ist unvollständig oder beschädigt.`

Valid actions, depending on connectivity/capability:
- **Downloads anzeigen**;
- **Erneut herunterladen**;
- **Zurück**.

The item must not remain labeled **Offline verfügbar** once the owning offline contract knows it is invalid.

#### Offline storage removed during playback

If the backing removable/managed storage disappears:

**Offline-Speicher nicht verfügbar**

`Der Speicher mit dieser Episode wurde getrennt.`

Actions:
- **Erneut prüfen**;
- **Zurück**.

If already-buffered bytes can continue safely, playback may continue only for the data the client actually holds. Do not imply future media availability once the local source is gone.

#### Offline reference composition

The approved Desktop/Mobile offline reference uses:
- normal dark media-led Player chrome;
- Frieren S1 E07 as a verified local item;
- a restrained **Offline verfügbar** badge;
- normal transport/timeline at `18:42 / 24:11`;
- a focused **Nächste Episode nicht offline verfügbar** overlay for E08;
- Desktop modal/overlay treatment;
- Mobile bottom-sheet treatment;
- **Downloads anzeigen** as the primary recovery action.

The reference intentionally does **not** combine corrupt-package, removed-storage, Learning-missing and sync-conflict states into one image.

## 14. Preparing / loading / partial states

### Resolving
Short initial state while capabilities, file availability and PlaybackPlan are resolved.

Show:
- media title/context;
- neutral progress indicator;
- no fake timeline.

### Preparing / acquiring
Used when content is not ready locally but the play action triggered the acquisition flow.

Show compact user information such as:
- Preparing;
- requested language/profile summary if useful;
- progress only when reliable;
- Cancel/Back only when policy permits.

Do not expose indexer/download-client internals.

### Remux/transcode startup
Show a normal loading state. Only show technical reason inside diagnostics.

### Buffering
Keep the current frame when possible, show a center spinner after a short delay, keep controls available, and continue to show the buffered range in the timeline. For prolonged stalls add a concise `Buffering…` label. Do not show unreliable percentages.

### Storage waking/offline
Explain that media storage is unavailable/waking and expose Retry/Back. Do not classify it as codec failure.

## 15. Empty / unavailable states

A full Player should rarely have a classic empty state. Use explicit unavailable states instead:

- no playable Asset/File;
- selected File disappeared;
- storage root unavailable;
- no compatible playback plan;
- permission revoked;
- requested track no longer available.

Each state must offer only valid next actions:
- Retry;
- choose another version/track;
- Request/Prepare when allowed;
- Back.

Never show raw server exceptions.

## 16. Error and recovery behavior

Errors are categorized, not collapsed into “Playback failed”.

Recoverable examples:
- network interruption;
- expired delivery session;
- failed Direct Play followed by server-resolved fallback plan;
- storage wake delay;
- selected track becoming unavailable.

Recovery rules:
- preserve position and track choices;
- report failed playback mode to the server;
- ask the server for a replacement PlaybackPlan;
- do not locally invent a fallback matrix;
- a network failure is not automatically a codec/capability failure.

Permanent/unsupported errors provide a concise reason and Back/Diagnostics actions.

## 17. Ended state

At completion:
- keep final frame/backdrop;
- clearly show Replay;
- show Next Episode/Next Chapter only when a real canonical next target exists;
- optionally show brief Up Next countdown only when autoplay is enabled;
- allow cancellation of autoplay;
- update completion through canonical policy before provider write-back.

No recommendation wall should obscure completion controls.

## 18. Continuation / mini-player

Binding shared specification: `docs/mockups/continuation-surfaces/SPEC.md`.

Leaving the full Player through Minimize or normal Jularr navigation may preserve the same `ActiveSession` in the persistent **Now Playing** surface.

### Desktop

The compact Now Playing bar uses:

- a full-width top progress line with played, buffered and remaining state;
- chapter/segment markers where available;
- hover-revealed scrub handle with seek/time/chapter preview;
- thumbnail first;
- bold Work title;
- structural unit line such as `S1 · E4 — Episode name`;
- current / duration such as `5:29 / 25:25`;
- geometrically centered transport: **Previous · -10 · Play/Pause · +30 · Next**;
- right-side direct settings icons while space permits;
- one Settings gear/menu for overflow/low-frequency actions;
- **volume slider inside Settings in this compact state**, not as a permanent bar control.

Hover/focus on the thumbnail exposes **Open full Player**. Opening it returns to the exact same `ActiveSession`.

The centered transport must not move when left metadata or right settings width changes.

### Mobile / narrow Tablet

Use a compact two-level composition when necessary:

1. identity/action row with thumbnail, title/unit, open-player and Close/Stop;
2. centered transport.

Prefer **Previous · -10 · Play/Pause · +30 · Next**. If width cannot preserve touch-target size, keep **-10 · Play/Pause · +30** centered and move Previous/Next into the action/settings sheet.

The progress line remains at the top. Settings use a touch sheet. No permanent volume slider.

### Session rules

- return opens the same `ActiveSession`;
- progress updates live;
- selected audio/subtitle/quality/speed/Learning state remains session-owned;
- Close/Stop explicitly ends this playback session but does not delete progress or mark completion by itself;
- no separate mini-player progress/session store;
- navigation/refresh can restore the bar only from a still-valid server-authoritative ActiveSession;
- Minimize, Popout/PiP and Close/Stop remain distinct actions.

Audiobook and actively playing TTS use the same Now Playing transport semantics.

TV does not require a persistent floating mini-player across browse screens.

## 19. Light / Dark

The media surface is intentionally dark/media-led in both application themes.

Dark application theme:
- dark neutral overlays/sheets;
- Fluent-style elevation;
- subtle translucent control backdrop.

Light application theme:
- do **not** turn the video surface white;
- player chrome remains dark/translucent for contrast;
- secondary dialogs/sheets opened outside the media surface may use light theme tokens;
- text/icons meet contrast requirements in both modes.

Accent is used for:
- timeline played state;
- focus/selected state;
- active menu item;
- TV focus ring.

No neon/glass-heavy styling and no theme-specific layout fork.

## 20. Accessibility

Required:
- keyboard operability on desktop;
- visible focus;
- screen-reader labels for icon-only actions;
- no color-only state communication;
- minimum touch targets on touch devices;
- captions/subtitles remain readable with user text scaling where technically possible;
- reduced-motion preference disables unnecessary scale/animation while retaining clear focus;
- TV focus remains obvious without relying only on animation.

## 21. Required mockup set

Create mockups in this order:

1. **Desktop Dark — primary playing state**  
   Establish hierarchy, timeline, transport and secondary controls.

2. **Desktop Dark — learning state**  
   Controls mostly hidden, learning subtitle + word/sentence interaction visible.

3. **Mobile Dark — portrait paused/controls**  
   Establish touch sizing and compact hierarchy.

4. **Mobile Dark — landscape playback**  
   Establish gesture zones, safe areas and subtitle placement.

5. **TV Dark — focused controls**  
   Establish remote focus language and seek hierarchy.

6. **Tablet Dark — landscape learning side sheet**  
   Only after Mobile/Desktop interaction is stable.

7. **Light-theme validation references**  
   Desktop and Mobile secondary sheet/dialog states. The media surface itself remains dark.

8. **Error/Preparing reference**  
   One platform reference is enough if state treatment is shared; platform-specific composition rules still apply.

9. **Desktop/Mobile Dark — offline playback reference**  
   Verified local playback with a restrained Offline-available indicator and a canonical-next item that is not downloaded locally. Desktop uses the focused overlay; Mobile uses the matching bottom sheet.

Do not produce decorative variants before these behavioral references are approved.

## 22. Data / information contract shown by the UI

The screen consumes view data derived from:
- canonical Work and structural unit;
- selected Edition/Version when user-relevant;
- Asset/File availability;
- canonical MediaTrack list;
- duration/current position;
- PlaybackPlan and safe user-facing reasons;
- ActiveSession;
- canonical progress/completion state;
- buffered time ranges when the delivery/client can report them;
- chapters/segments and their confidence/policy eligibility;
- profile/user Auto-Skip playback preferences;
- subtitle cues / learning capability;
- client feature capabilities;
- asymmetric manual seek policy: **SeekBackSeconds = 10** and **SeekForwardSeconds = 30**;
- preparing/acquisition state when Play triggered missing-media acquisition;
- verified local offline-package availability for the current item;
- locally usable audio/subtitle/Learning package capability while disconnected;
- canonical-next local availability;
- local-first progress/sync-pending state when server connectivity is unavailable;
- local storage availability/verification state.

The Player must not query legacy Anime/Episode-only tables as its permanent source of truth.

## 23. Must not implement

- No client-specific playback decision engine.
- No second Player progress or session database.
- No permanent dependency on legacy `Anime.Id`, legacy `Episode.Id` or anime-only `MediaFile` ownership.
- No arbitrary filesystem path or FFmpeg command supplied by the client.
- No `iOS = transcode`, `MKV = transcode` or similar hard-coded platform decision matrix in UI code.
- No desktop “single click toggles controls” behavior; desktop single click on unobstructed video is Play/Pause.
- No Learning subtitle hit target that falls through to generic player gestures.
- No hiding Learning subtitles merely because transient player controls hide.
- No permanent Learning behavior, hit target, spacing or forced pause while Learning mode is Off.
- No Learning toggle when the instance or profile has Learning disabled or the current content cannot support it.
- No PlaybackPlan/ActiveSession recreation merely to toggle Learning On/Off.
- No TV mouse/hover assumptions.
- No dense technical codec/bitrate panel in the default Player.
- No admin release/indexer/download information in normal playback UI.
- No raw exception text.
- No automatic completion merely because an item was opened or started.
- No provider write-back from CurrentItem/ResumePosition.
- No separate subtitle identity model for learning.
- No permanent mini-player state disconnected from ActiveSession.
- No duplicated platform pages that reimplement business rules independently.
- No client-guessed intro/outro/chapter boundaries.
- No Auto-Skip enabled by default.
- No Auto-Skip without a canonical eligible segment marker.
- No single symmetric seek-step setting that forces backward and forward to use the same duration; the canonical Player contract must support 10 seconds backward and 30 seconds forward independently.
- No separate Offline Player implementation or offline-only playback progress/session model.
- No network/server error page while a verified local item is playable.
- No autoplay attempt, infinite buffering or fake preparing state for a next item that is known not to be locally available while genuinely offline.
- No selection of audio/subtitle tracks that cannot actually be resolved from the local package while offline.
- No item labeled **Offline verfügbar** after local verification has determined the package is corrupt/incomplete.
- No forced online dependency for normal playback, local progress writes or packaged Learning interactions when the required local data exists.

## 24. Mockup acceptance checklist

A Player mockup is acceptable only when:
- media remains visually primary;
- normal, subtitle and Learning layers are visibly separable;
- Learning Off is behaviorally equivalent to the normal Player except for the optional visible toggle;
- Learning can be switched On/Off live without recreating or changing the playback session;
- Learning controls appear only after instance gate + profile opt-in + permission/capability + content eligibility resolve true;
- Desktop click behavior can be implemented without ambiguity;
- Mobile gesture/tap regions do not conflict with learning subtitles;
- Desktop transport remains centered independently from left-side volume and right-side settings;
- Tablet/Mobile Minimize, Popout and Close are visibly distinct;
- Tablet transport is centered over the video while settings remain below the timeline;
- Mobile settings collapse behind a single gear rather than copying the Desktop icon row;
- all manual seek affordances use -10 seconds backward / +30 seconds forward;
- TV has a complete D-pad focus path with centered transport and a second settings row;
- audio/subtitle/quality are accessible without persistent clutter;
- Light/Dark contrast behavior is defined;
- played/buffered/remaining timeline states are visually distinct;
- chapters and segment markers are visible without making the timeline noisy;
- manual Skip actions are direct contextual buttons;
- Auto-Skip has explicit settings, defaults Off and provides temporary Undo feedback;
- preparing/loading/buffering/error states have clear treatment;
- completion vs resume semantics are not visually conflated;
- no legacy media model is implied;
- no control depends on a capability the platform may not have without a fallback state;
- verified local playback uses the normal Player rather than a separate offline UI;
- Offline availability is visible but does not dominate working playback;
- only locally usable tracks are selectable while disconnected;
- offline progress/completion remain canonical and sync later;
- unavailable canonical-next content stops autoplay cleanly and exposes **Downloads anzeigen** instead of buffering/network failure;
- corrupt/incomplete local packages and removed storage have explicit recovery states.
