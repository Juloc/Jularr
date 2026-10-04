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

### Binding control policy

The default iPhone/iPad Safari and Home Screen/PWA experience is the **Jularr custom Player**, not Safari's built-in `<video controls>` UI.

For normal inline playback:
- render the media element with `playsinline`;
- do **not** add the HTML `controls` attribute;
- keep `video.controls === false`;
- Jularr owns play/pause, -10/+30 seek, timeline, audio/subtitle/quality menus, chapters, Learning controls, Minimize, Popout/PiP and the normal fullscreen action;
- attach `playsinline` before playback starts so iPhone does not enter native fullscreen merely because playback began;
- keep PlaybackPlan, ActiveSession, MediaProgress and track selection exactly the same as every other web client.

Safari/Apple controls are an explicit compatibility surface, not a second normal Player design. Jularr must never accidentally show both its own chrome and the browser's normal inline controls at the same time.

### Presentation modes

WebKit presentation is a client-only presentation concern over the same ActiveSession. Model it explicitly as one of:

`Inline | Theater | ElementFullscreen | NativeVideoFullscreen | PictureInPicture`

This state does not create another playback session, progress record, quality decision or track-selection owner.

#### Inline

Inline is the default. The video stays inside the Jularr composition with custom controls and overlays.

#### Jularr Theater Mode

Theater Mode is the mandatory fallback when Jularr cannot put the whole Player container into standards-based fullscreen while retaining custom HTML chrome.

Theater Mode:
- keeps the same video element and ActiveSession;
- expands the Jularr Player root to the complete usable viewport with fixed positioning;
- keeps Jularr controls, subtitles and Learning overlays fully functional;
- hides normal application chrome behind the Player;
- locks page scrolling/overscroll while active and restores it exactly on exit;
- respects `env(safe-area-inset-top/right/bottom/left)`;
- uses modern dynamic viewport sizing such as `100dvh` with a safe fallback rather than assuming `100vh` equals the visible iOS viewport;
- responds to portrait/landscape viewport changes without recreating playback;
- does not claim that browser chrome has been removed when Safari itself still owns visible browser UI.

For an installed iPhone/iPad Home Screen app, Theater Mode is the preferred full-screen-like Jularr experience when standards-based element fullscreen is unavailable because the standalone app viewport already removes normal Safari tab/address chrome.

#### Element fullscreen with custom controls

When the Player root exposes a working standards-based element Fullscreen API, the normal Fullscreen action should request fullscreen on the **Player root/container**, not directly on the `<video>`.

This preserves:
- custom transport;
- Jularr subtitle rendering;
- Learning overlays;
- settings sheets;
- safe custom exit/fullscreen actions.

Use actual runtime capability detection, for example whether the Player root has a callable `requestFullscreen` and whether the document exposes matching fullscreen state/events. Do not hard-code `iPad = supported`; WebKit/browser version and presentation context are the deciding capability.

If the element-fullscreen request rejects or is unavailable, fall back immediately to Jularr Theater Mode. Do not fall through automatically to the native Apple video player.

#### Native Apple video fullscreen

Native video fullscreen is a **last-resort or explicitly requested system-player path** because WebKit owns that surface and Jularr HTML overlays/custom controls cannot be assumed to remain visible.

Only use native video fullscreen when:
- the user deliberately chooses a system-player/system-fullscreen action; or
- a WebKit limitation makes native fullscreen the only usable playback surface for that media/context.

Where supported, this may use the WebKit video fullscreen API such as `webkitEnterFullscreen()`.

Before entering native fullscreen:
- flush meaningful current progress/session state;
- preserve selected audio/subtitle/quality preference in the canonical session;
- record that the presentation changed, not that a new playback session started.

While native fullscreen is active:
- do not display fake Jularr buttons that cannot control the native surface;
- do not claim interactive Learning overlays remain available;
- native subtitle/track behavior may be used only when it maps safely to the selected canonical tracks.

On return:
- keep the same ActiveSession;
- restore the same logical position and selected tracks;
- restore Jularr inline/Theater chrome without creating a second Player instance;
- process `webkitbeginfullscreen` / `webkitendfullscreen` or equivalent supported events so state does not drift.

The normal iPhone Fullscreen button must therefore prefer **Jularr Theater Mode**, not `webkitEnterFullscreen()`, when custom Player behavior is required.

### Fullscreen action resolution

The recommended implementation for the normal Jularr Fullscreen action is:

```text
user presses Fullscreen
  -> Player-root element fullscreen supported and usable?
       yes -> ElementFullscreen
       no  -> Theater

explicit "Open in system player/fullscreen"
  -> native video fullscreen supported?
       yes -> NativeVideoFullscreen
       no  -> keep current Jularr mode and hide/disable the unsupported action
```

Exiting Fullscreen reverses only the presentation mode. It must not stop playback unless the user separately chose Close/Stop.

### Picture in Picture / Popout

PiP must be capability-based and must not be inferred only from `iPhone`, `iPad`, Safari version or PWA status.

Recommended order:
1. use the standards-based Picture-in-Picture API when it is actually exposed and succeeds;
2. where necessary, use WebKit presentation-mode capability checks such as `webkitSupportsPresentationMode("picture-in-picture")` plus `webkitSetPresentationMode(...)`;
3. if neither is usable, hide/disable Popout rather than presenting a dead button.

A capability probe that reports support is not enough after a real invocation returns `NotSupportedError` or otherwise fails. Downgrade the current-session capability and stop offering a broken action until the environment changes/reloads.

Listen to the corresponding enter/leave/presentation-mode events so Jularr chrome and ActiveSession state remain synchronized.

### Safari vs installed PWA

Safari and an installed Home Screen/PWA must share the same Player implementation and contracts. Do not create a separate iOS Player page.

Presentation differences may be detected only to improve layout:
- standalone display mode may use the full app viewport for Theater Mode;
- Safari browser mode must account for dynamic browser chrome;
- neither mode implies that PiP, fullscreen, orientation lock, Wake Lock or Media Session definitely works.

Use `matchMedia("(display-mode: standalone)")` and, where useful for Apple Home Screen detection, the platform-exposed standalone signal only as presentation context. Never use those checks as codec/playback compatibility rules.

### Orientation and safe areas

Do not require orientation lock for correct playback. iPhone/iPad Player layout must remain usable when orientation APIs are missing or denied.

On viewport/orientation change:
- recompute Player geometry and subtitle safe zones;
- preserve playback position and controls state;
- do not reload the media;
- keep top/bottom actions out of notch/Dynamic Island/home-indicator unsafe areas.

### Subtitles and Learning

Custom subtitle and Learning layers are first-class reasons to prefer Inline/Theater/ElementFullscreen over native video fullscreen.

- Jularr-rendered subtitles remain above the media and independent from transient player chrome.
- Learning hit targets remain interactive only while Jularr owns the composition.
- Entering native system fullscreen must not silently pretend those HTML overlays are still available.
- If native fullscreen is explicitly selected while Learning is On, the action must communicate that interactive Learning controls are unavailable in the system surface; returning restores them without changing Learning state.

### Media Session, background transitions and progress

Use Media Session and related APIs only when actually supported.

Flush meaningful state on:
- pause;
- completed seek;
- visibility/background transition;
- entering/leaving native fullscreen or PiP;
- route leave;
- Player close.

Do not recreate PlaybackPlan or ActiveSession solely because Safari changed presentation mode.

### Implementation ownership

Keep WebKit-specific behavior in the shared web Player presentation/capability layer. Do not spread `navigator.userAgent` checks through controls, playback planning or media-domain code.

The implementation should have:
- one media element;
- one Jularr control tree;
- one presentation-mode resolver;
- one runtime capability snapshot that can be downgraded after failed API calls;
- event synchronization for fullscreen/PiP/native-video presentation;
- CSS presentation states for Inline/Theater/ElementFullscreen rather than separate Player pages.

Playback compatibility remains owned by the canonical capability document + PlaybackPlan. Presentation capability must never become a rule such as `iOS = transcode`.

### Required iPhone/iPad verification matrix

Manual real-device verification is required in addition to browser/DOM regression tests because headless automation cannot prove all WebKit media surfaces.

At minimum verify:
- iPhone Safari portrait + landscape: playback begins inline with Jularr controls and no duplicate Safari controls;
- iPhone installed Home Screen/PWA portrait + landscape: same Player, Theater fills the standalone viewport and respects safe areas;
- iPad Safari portrait + landscape: custom controls remain intact through the best supported custom fullscreen path;
- iPad installed Home Screen/PWA portrait + landscape;
- normal Fullscreen never unexpectedly launches native Apple video fullscreen when Theater/custom fullscreen is available;
- explicit native/system fullscreen preserves position/session and restores Jularr correctly on exit;
- PiP button appears only when the actual current environment can use it and failure does not leave a dead control;
- selected audio/subtitle state survives Inline <-> Theater <-> fullscreen/PiP transitions;
- Learning subtitles/overlays remain usable in Jularr-owned presentation modes and are not falsely promised in native fullscreen;
- Minimize, Popout, Fullscreen and Close remain four distinct actions;
- rotation, safe-area changes, Safari browser chrome changes and PWA standalone sizing do not reload or restart playback.

The mockup represents the intended Jularr-owned Player state. Apple system playback UI is an explicit compatibility fallback, not the primary iOS/iPadOS design.

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
- preparing/acquisition state when Play triggered missing-media acquisition.

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
- No `controls` attribute or `video.controls = true` for normal iPhone/iPad inline playback while Jularr custom controls are active.
- No normal iPhone Fullscreen action that blindly calls `webkitEnterFullscreen()` and discards Jularr controls/overlays.
- No UA-only `iPhone/iPad/Safari` switch deciding fullscreen, PiP, playback compatibility or transcoding behavior.
- No duplicate iOS/iPadOS Player page or second ActiveSession merely to handle WebKit presentation differences.

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
- iPhone/iPad inline playback uses `playsinline` with Jularr custom controls and does not expose duplicate Safari inline controls;
- normal Fullscreen preserves Jularr chrome by resolving to Player-root element fullscreen when usable, otherwise Jularr Theater Mode;
- native Apple video fullscreen is explicit/last-resort and round-trips through the same ActiveSession;
- PiP/Popout is shown from actual runtime capability and can downgrade cleanly after a failed invocation;
- Safari and Home Screen/PWA share one Player implementation while handling standalone viewport/safe-area differences deliberately;
- real-device iPhone/iPad Safari + Home Screen verification covers portrait, landscape, fullscreen/Theater, PiP, track persistence and Learning-overlay behavior.
