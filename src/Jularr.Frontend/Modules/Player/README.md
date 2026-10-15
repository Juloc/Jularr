# Player assets

`player.entry.ts` is the Player page's only entry: it imports `player.scss`,
probes browser capabilities, requests the Backend PlaybackPlan, reports progress
and drives the server-rendered [UiPlayer](../../Framework/Components/Player.cs)
chrome. The page composition is owned by
[the Player page](../../Pages/User/Player/Player.cshtml.cs). Icons beside this
module use the `player-` prefix because sprite symbols are named by file name.

The shared chrome centers Previous / -10 / Play-Pause / +30 / Next over the
video. One timeline sits above the bottom toolbar, with volume on the left and
track/speed/display options on the right. Neighbour targets come from the
canonical playback-item service. Buttons reuse the shared Button styles.

The Development-only `/Design?pane=player` workshop composes this same control
with illustrative episodes and chapters. Its own entry demonstrates local UI
states, including minimize/expand, without loading the playback client, making
API calls or starting a stream/session. Quality, casting, dual subtitles and
real chapter/episode integration in this layout remain outside this prototype.
