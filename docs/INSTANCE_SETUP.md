# Instance setup

Owner decision of 22 October 2026 (the setup mockup with the Sonarr and Radarr takeover) supersedes the
older flows. Setup uses the canonical configuration owners; it adds neither its own settings store nor
database migrations (the persisted wizard step and the way of starting live in `InstanceSetupState`).

## Standalone flow

Setup stands alone: no sidebar, header or other application menu. Every Setup page (also the Admin editors reused under
`/Setup/...`) is framed by [SetupWizard](../src/Jularr.Frontend/Framework/SetupWizard.cs): the one [Stepper](../src/Jularr.Frontend/Framework/Components/Stepper.cs)
at the top, Back and Next at the foot, and no heading, subtitle, help line or tab row of its own. Every topic is a step of its own; the last step is the
overview with Finish.

| # | Step | Page | What it does |
| --- | --- | --- | --- |
| 1 | Owner and language | /Setup | CreateOwnerV1: user name, profile name, password with confirmation (eye), language with its flag. Bootstrap happens exactly once and signs the Owner in. |
| 2 | Start | /Setup/Begin | The first choice: **New setup**, **Take over from Sonarr, Radarr, Readarr or Lidarr** or **Restore a backup** (offered only where a restore can run: `Backup:PgRestorePath`; the restore needs the Owner's session, so it follows step 1). UpdateSetupStartV1 stores the start; taking over adds step 4. |
| 3 | Storage | /Setup/Storage | The Admin connection cards, standalone. The folders the installation offers (`Storage:LocalPaths`, such as `/media`) are listed as tiles with one-click Connect; Add SMB, NFS or a local path opens a dialog with Cancel. A new connection is checked at once. |
| 4 | Takeover (takeover start only) | /Setup/Takeover | One card for each system (Sonarr, Radarr, Readarr, Lidarr, Prowlarr) with address and API key right in it (and the TMDB key where titles need it). Preview (read-only: the source is only read, nothing is written to it, no download starts) shows counters per kind (quality profiles, custom formats, download clients, indexers, root folders, titles), what needs a look (conflicts, unsupported profiles, missing keys, missing libraries) and every found item in folds; root folders are assigned to libraries (or new libraries are created on a connection). Take over then writes the same items and shows what was added. A wrong address or key is answered in plain words in that card. The key is sealed for ten minutes between preview and takeover instead of being typed twice. |
| 5 | Modules | /Setup/Instance | Starting sets, five primary modules, and the media types/subtypes at the top; all 22 other modules below, arranged into seven collapsed topic accordions (Playback & anime, Reading & manga, Games, Discover, Personalization & organization, Accounts & devices, Administration), each module exactly once. SaveSetupModulesV1 persists them together. **Complete** enables every module, media type and subtype. Takeover media choices remain prefilled until changed. |
| 6 | Quality profiles (Arr+) | /Setup/Profiles | The Admin profile list for the chosen media type (custom formats, sizes, release profiles and tags stay in Administration). |
| 7 | Libraries | /Setup/Libraries | One row for every media type that is switched on: its folders and the picker for another on any connection. Leaving the step switches off the media without a folder (SaveSetupLibrariesV1). |
| 8 | Requests (Requests module) | /Setup/Requests | The default request rule: which media may be requested, approval, limit per period, selectable quality profiles. |
| 9 | Download storage (Arr+) | /Setup/DownloadStorage | Incomplete and Complete folders, reserve and limits. |
| 10 | Download clients (Arr+) | /Setup/Downloads | Every client is a card with its settings inline and Test; a new one is added in a dialog. |
| 11 | Indexers (Arr+) | /Setup/Indexers | The same for indexers; Prowlarr import leads to the takeover step. |
| 12 | Providers | /Setup/Providers | Every provider is a card with its switch and key inline, saved together by Continue (SaveSetupProvidersV1; a blank key keeps the stored one, a typed one switches the provider on); Plex, Jellyfin and Emby connect from their own cards. |
| 13 | Overview | /Setup/Overview | A row for every step with its real state (set up, missing, skipped, check) from the backend checks, the plain reason where something lacks, the way back, and Finish (CompleteSetupV1): with something required missing it reads Finish anyway and asks once more. |

Not built because the backend lacks it: routing of download clients per media group or subtype with fallback chains, an audiobook type, an Anime-movies switch,
Seerr and SABnzbd import (extension points only), instance-level requestable media types, an admin-only requests switch and a downloads-wanted switch.

## Persistence and gate

`InstanceSetupState.SetupStepTypeId` is the furthest step reached (the steps are numbered in flow order, greatest wins) and `SetupStartTypeId` the way of starting.
Every service that finishes a step moves it forward; steps without a form post Next to `/Setup/Progress`. Values entered stay where they belong
(modules, providers, connections, libraries); keys are protected and never shown again.

[SetupGate](../src/Jularr.Frontend/Framework/SetupGate.cs) answers every page request until setup is finished: without an Owner every address leads to /Setup;
afterwards only the Owner continues, always at the step last reached (`/Setup/Progress`), the Owner's sign-in and the error pages stay open, and any
other account sees `/Setup/Waiting`. Assets, the live stream and the service API are not pages and stay as they are. After Finish
the setup addresses redirect to the application (the finished state is remembered by the process).

## Rules kept

- The three presets are Complete, Media server and Requests; module preset membership comes from the Backend (ReadSetupModulesV1). Complete also enables **all** media types and subtypes in the same form. Choosing the other presets changes only module switches and preserves the Owner's media selections. The currently chosen preset is highlighted only when its actual settings match.
- All modules from the Admin module registry appear exactly once in Setup. The five primary modules are up top; media-type/subtype switches follow; every smaller module (including Trickplay, Subtitles, AniList, Translation, OCR, SmartPdf, Uploads, Emulation) is grouped only in its relevant lower accordion. Accounts & devices groups Profiles, ProgressSync, Devices and Registration; Personalization & organization groups Notifications, Collections, Calendar, Statistics, UnitGroups and HomeLayout. Expanded module cards wrap their full names and help text; active module cards use the shared accent tokens. These seven groups start closed and can be expanded by pointer, touch or keyboard; their headers always show the live number of enabled modules. Turning on a dependent module cascades its module requirements; the Backend validates the final state.
- New things (a connection, an indexer, a client) are added in a dialog with Cancel; existing providers, clients and indexers are edited inline in their card.
- Library folders are selected in the shared native dialog (authorized server-rendered lists, cursor navigation, without JavaScript the browse links).
- Takeover never starts downloads and never writes to the source; missing monitored titles become Wanted and stay paused until the Owner resumes them.
- What the overview demands follows the chosen modules ([SetupLogic](../src/Jularr.Backend/Logic/Instance/SetupLogic.cs), reusing the health check for storage and connection state). Required (finishing needs the Owner's confirmation, `FinishSetupDataV1.AcceptMissing`): a function chosen (`modules`), a storage connection while Library or Arr+ is on (`storage`), something taken over after a takeover start (`takeover`), a folder for every media type left on, TMDB for movies and series, IGDB for games, and with Arr+ an indexer, a download client and the download storage of a configured client. Warnings: an empty library (`library.none`), a request rule never saved, AniList without the application for the profiles' sign-in (AniList and ProgressSync on), a storage, indexer or client that fails its check, FFmpeg missing. Without Arr+ no client or indexer is asked for. A passed step with an empty required or optional check carries the dash in the stepper.
- AniList's catalog needs no key: the id and secret inputs (the application profiles sign in with) exist only with the AniList module, in the providers step and in the administration; IGDB always asks for the Twitch pair.
- Only an Owner configures the instance; permissions, validation, transactions and safe paths remain Backend-owned. No active database is installed or changed.

## Playback cache safety

Video transcoding uses `Media:TranscodeCachePath` (by default `/data/jularr-transcode` where `/data` exists, otherwise a Jularr subfolder under the system temp directory). `Media:MaxTranscodeCacheBytes` defaults to 2 GiB and `Media:TranscodeCacheFreeSpaceReserveBytes` to 1 GiB; both can be configured through the standard `Media__...` environment variables. FFmpeg jobs are stopped when the cache exceeds its budget or storage becomes unavailable, and further segment requests fail instead of writing unchecked. The periodic check is a runtime safety budget, **not** a filesystem-enforced hard quota; operators must choose a suitable bounded volume for stricter isolation. Full UI selection and a mandatory explicit cache location remain separate pre-release work.

## Verification

SetupTests cover bootstrap concurrency/closure, generated-language selection, the first choice and the steps in order, atomic module edits and rollback, provider
saves, empty libraries, the checks per chosen modules (empty, Arr+ off, fully configured) and completion with and without confirmation. TakeoverTests cover the source reads. Browser tests: `SetupFlowTests` (the three branches, storage tiles and the add dialog,
takeover against the fake Sonarr, the steps in order with the overview, clicking through with nothing entered, AniList fields only with the module, leaving and resuming), `SetupOwnerTests` (owner and modules steps). The other browser tests
create the Owner in the real setup and mark the wizard finished.

The [original wizard specification](https://github.com/Juloc/Jularr/blob/dev/docs/mockups/setup-wizard/SPEC.md)
remains a reference for unchanged behavior; the newer owner decisions above take
precedence. See [CODEMAP](CODEMAP.md) and [Frontend CODEMAP](../src/Jularr.Frontend/CODEMAP.md).
