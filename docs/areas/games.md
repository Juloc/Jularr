# Games

Game metadata, platforms, ROM identification and the Game page.
Index: [CODEMAP](../CODEMAP.md).

## Backend

- Games metadata: multi-provider by design; each provider keeps its own titles, facts and identity.
  [IgdbClient.cs](../../src/Jularr.Integrations/Logic/Igdb/IgdbClient.cs) (Twitch application token cached until shortly before it expires, four
  requests per second) and [GameMetadata.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/GameMetadata.cs) (client id in the provider's
  `ExternalId`, secret protected; developer fact, cover, `igdb` identity); platforms are Jularr's own `GamePlatforms` (provider ids as columns) linked
  through `WorkPlatforms` by [GamePlatformLogic.cs](../../src/Jularr.Backend/Logic/Media/GamePlatformLogic.cs).
  ROM identification: library scans hash cartridge ROMs and single-ROM ZIPs up to 512 MiB
  ([GameRomIdentification.cs](../../src/Jularr.Backend/Service/System/Operation/V1/GameRomIdentification.cs)), import the platform's libretro No-Intro
  DAT once per 30 days through [LibretroClient.cs](../../src/Jularr.Integrations/Logic/Libretro/LibretroClient.cs) (address `Providers:Libretro`) and
  assign each hashed ROM to the single game of its title (the verified dump's name, else the cleaned file name), else to a game of its own on the
  platform its dump or extension names, which a later Match to IGDB keeps with its files, saves and play time; several games of one title, archives
  without a known ROM and disc images stay unassigned.
  Platforms, extensions and title rules in [GameRomMatching.cs](../../src/Jularr.Backend/Logic/Media/GameRomMatching.cs), storage in
  [GameRomLogic.cs](../../src/Jularr.Backend/Logic/Media/GameRomLogic.cs).
  Retro play (Emulation module): [GameServices.cs](../../src/Jularr.Backend/Service/User/Game/V1/GameServices.cs) names the game's EmulatorJS core by
  its platform (`GameRomMatching.Emulated`), serves its file under its own name; `ReadGameFilesV1` lists every file of the games libraries with its
  system, size and whether the browser plays it (a disc image needs a BIOS), and a file without its game streams by its stored file
  (`ReadGameStoredFileContentV1`, files the profile's games categories allow) with EmulatorJS's own browser saves; the services keep the profile's
  save memory and save state (`GameSaves`, opaque bytes up to 16 MiB) and play time (the game's `MediaProgress` of type Game) through
  [GamePlayLogic.cs](../../src/Jularr.Backend/Logic/Playback/GamePlayLogic.cs).
  ScreenScraper (needs a developer account), SteamGridDB and PCGamingWiki follow (see the source catalog).
- Games in Arr+: a game request names one of the game's listed platforms (`ReadWorkPlatformsV1`); wanted items are unique per Work and platform.
  [GameReleaseJudge.cs](../../src/Jularr.Backend/Logic/Acquisition/GameReleaseJudge.cs) (title words, updates/DLC/sequels to review, the wanted
  platform named, another platform rejected, unnamed releases count as PC; forms GOG/NSP/XCI/ISO/ROM) searches the platform's Newznab category;
  placement keeps the whole release below `Platform/Title (Year)` without NFO/SFV noise; every assigned game file tags its version in `GameReleases`
  with the given platform, else the one its verified dump or its top folder names (`TagGameReleaseAsync`), and details list the platforms on hand.

## Frontend

- [Game](../../src/Jularr.Frontend/Pages/User/Game/Game.cshtml.cs), [client](../../src/Jularr.Frontend/Modules/Game/game.entry.ts),
  [helpers](../../src/Jularr.Frontend/Modules/Game/Game.ts), [tests](../../src/Jularr.Frontend/Tools/game.test.mjs): EmulatorJS from `/emulator/`
  (installed by [emulator-assets.mjs](../../src/Jularr.Frontend/Tools/emulator-assets.mjs) during the build), the page's connections limited to
  Jularr; the profile's save memory replaces the browser copy at start and uploads when the emulator flushes it, save states travel gzip-compressed
  through the emulator's own Save/Load State, play time is reported while the game runs visibly; the page is exactly the window's height (EmulatorJS
  sizes itself from it), a touch-first device gets EmulatorJS's on-screen controls and a finger on the DS picture is its touch screen (the core reads
  the mouse).
  A file without its game opens at `/Game/Files/{id}` (route in [PageConventions](../../src/Jularr.Frontend/Framework/PageConventions.cs)) with
  EmulatorJS's own browser saves; [GameFiles](../../src/Jularr.Frontend/Pages/User/GameFiles/GameFiles.cshtml.cs) at `/Games/Files` lists the files of
  the games libraries (name, system, size, Play or why not), linked from the Library when only games are chosen.
