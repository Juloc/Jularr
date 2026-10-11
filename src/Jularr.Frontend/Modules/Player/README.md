# Player assets

Player-specific CSS and eventual TypeScript live in this module, not a global
bundle. The page opts in by declaring `Modules/Player/player.entry.scss`.
Do not introduce playable media URLs, playback sessions or controls before the
canonical Backend PlaybackPlan contract exists.
