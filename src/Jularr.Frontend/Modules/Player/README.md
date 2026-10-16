# Player assets

`player.entry.ts` is the Player page's only entry: it imports `player.scss`,
probes browser capabilities, requests the Backend PlaybackPlan, reports progress
and drives the server-rendered [UiPlayer](../../Framework/Components/Player.cs)
chrome. The page composition is owned by
[the Player page](../../Pages/User/Player/Player.cshtml.cs). Icons beside this
module use the `player-` prefix because sprite symbols are named by file name.

The shared chrome consumes canonical neighbour targets and reuses Button styles.
The agreed Desktop toolbar places Previous / -10 / Play-Pause / +30 / Next below
the timeline, volume left, subtitle toggle/settings/view/fullscreen right. The
`Settings` composition slot enables that layout for the workshop. The live page
has not supplied this slot yet and retains its existing track/speed menus.

The Development-only `/Design?pane=player` workshop composes this same control
with illustrative episodes and chapters. Its own entry demonstrates local UI
states without loading the playback client, making API calls or starting a
stream/session. Its Basic/Audio/Subtitles/Playback/Info popover uses shared fields,
toggles, buttons and navigation styles. Caption text, defaults and technical facts
are explicit samples; none imply working persistence or processing.
The settings capsule has scope selection above and integrated tabs below. Its size
stays stable across tabs; only content scrolls. Small viewports use a full-screen
surface without stopping playback. Expand opens a Development-only settings view
with shared fields and the same video element in a preview; it is not a separate
production route. Scope, Reset and copy behavior are in-memory examples, not saves.
The four bottom-right controls use white circular surfaces.
Select popups use the shared native `base-select` styling where supported;
other browsers retain their native picker without a JavaScript replacement.

Binding decisions and remaining integration: [Player design](../../../../docs/PLAYER_DESIGN.md).
