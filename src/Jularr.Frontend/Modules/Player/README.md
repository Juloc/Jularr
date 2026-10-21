# Player assets

`player.entry.ts` is the Player page's only entry: it imports `player.scss`,
probes browser capabilities, requests the Backend PlaybackPlan, reports progress
and drives the server-rendered [UiPlayer](../../Framework/Components/Player.cs)
chrome. The page composition is owned by
[the Player page](../../Pages/User/Player/Player.cshtml.cs). Icons beside this
module use the `player-` prefix because sprite symbols are named by file name.

The shared chrome places the timeline above a responsive bottom toolbar:
volume on the left, five transport controls in the center, options on the right (Episodes first, then Chapters, subtitles, settings, views).
Previous/next episode controls remain disabled until canonical episode targets
are provided. Existing seek, play/pause, volume and display handlers are unchanged.
The Design lab uses an optional poster only; it loads no playback client or stream.
