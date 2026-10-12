# Player assets

`player.entry.ts` is the Player page's only entry: it imports `player.scss`,
probes browser capabilities, requests the Backend PlaybackPlan, reports progress
and drives the server-rendered [UiPlayer](../../Framework/Components/Player.cs)
chrome. The page composition is owned by
[the Player page](../../Pages/User/Player/Player.cshtml.cs). Icons beside this
module use the `player-` prefix because sprite symbols are named by file name.
