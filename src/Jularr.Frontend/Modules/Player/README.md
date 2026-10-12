# Player assets

Player-specific CSS and eventual TypeScript live in this module, not a global
bundle. The page opts in by declaring `Modules/Player/player.entry.scss`.
The Razor entry and composition are owned by
[the Player page](../../Pages/User/Player/Player.cshtml.cs).
Do not introduce playable media URLs, playback sessions or controls before the
canonical Backend PlaybackPlan contract exists.
