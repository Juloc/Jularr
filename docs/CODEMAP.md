# Jularr CODEMAP

One line per owner area: main code paths and focused tests. Rules and behaviour live in the area file; add new behaviour there, not here.
All documents: [INDEX](INDEX.md). Frontend folders: [Frontend CODEMAP](../src/Jularr.Frontend/CODEMAP.md).

## Areas

- [Platform](areas/platform.md): `Jularr.Backend/Service/Core`, `Jularr.Backend/Infrastructure/Sql`, `Jularr.Backend/Data`; `ServiceApiTests`, `SqlContextTests`
- [Accounts and sign-in](areas/accounts.md): `Jularr.Backend/Logic/Account`, `Jularr.Backend/Logic/Session`; `AuthenticationTests`, `AdminAccountTests`
- [Localization](areas/localization.md): `Jularr.Backend/Logic/Localization`, `Jularr.Backend/Service/Admin/Localization`; `UiLocalizationTests`
- [Instance and setup](areas/instance.md): `Jularr.Backend/Service/Admin/Instance`, `Jularr.Backend/Service/Public/Setup`; `SetupTests`
- [System](areas/system.md): `Jularr.Backend/Service/Admin/Backup`, `Dockerfile`; `DashboardTests`, `BackupTests`
- [Storage](areas/storage.md): `Jularr.Backend/Service/Admin/Storage`, `Jularr.Backend/Logic/Storage`, `Jularr.Integrations/Logic/Nas`; `StorageTests`
- [Library](areas/library.md): `Jularr.Backend/Service/Admin/Library`, `Jularr.Backend/Logic/Library`; `LibraryTests`, `NamingPatternTests`
- [Activity](areas/activity.md): `Jularr.Backend/Service/Admin/Activity`, `Jularr.Backend/Logic/Live`; `LiveTests`
- [Imports](areas/imports.md): `Jularr.Backend/Logic/Acquisition/ImportJudgement.cs`; `ImportJudgementTests`, `ManualImportLogicTests`
- [Jobs](areas/jobs.md): `Jularr.Backend/Service/Core/WorkerJobs.cs`, `Jularr.Workers`; `WorkerJobTests`, `BackgroundWorkTests`
- [Requests and Wanted](areas/requests-wanted.md): `Jularr.Backend/Service/User/Request`, `Jularr.Backend/Service/Admin/Wanted`; `RequestTests`
- [Arr+ core](areas/arr.md): `Jularr.Backend/Service/Admin/Acquisition`, `Jularr.Backend/Logic/Acquisition`; `AcquisitionTests`
- [Arr+ admin pages](areas/arr-admin.md): `Jularr.Backend/Service/Admin/Acquisition/V1/AcquisitionProfileServices.cs`; `ArrAdminTests`
- [Downloads](areas/downloads.md): `Jularr.Backend/Logic/Storage/DownloadStorageLogic.cs`, `Jularr.Backend/Logic/Network`; `UsenetTests`
- [Books](areas/books.md): `Jularr.Backend/Logic/Acquisition/BookReleaseJudge.cs`, `Jularr.Integrations/Logic/AnnasArchive`; `BookTests`
- [Catalog](areas/catalog.md): `Jularr.Backend/Service/Admin/Media`, `Jularr.Backend/Logic/Media`; `MediaTests`, `WorkRelationTests`
- [Metadata](areas/metadata.md): `Jularr.Integrations/Logic/Tmdb`, `Jularr.Integrations/Logic/Tvdb`; `MetadataFillTests`, `TvdbTests`
- [Covers](areas/covers.md): `Jularr.Backend/Service/Core/CoverStore.cs`; `CoverTests`
- [Works admin](areas/works-admin.md): `Jularr.Backend/Service/Admin/Media`; `WorkListTests`, `MediaImportTests`
- [Discover](areas/discover.md): `Jularr.Backend/Service/User/Discover`; `DiscoverTests`, `ExternalFeedTests`, `HomeLayoutTests`
- [Media cards](areas/media-cards.md): `MediaCardTests`, `HoverPreviewTests` (E2E)
- [Reading](areas/reading.md): `Jularr.Backend/Logic/Reading`, `Jularr.Backend/Service/User/Reading`; `ReadingTests`
- [Reader UI](areas/reader-ui.md): `ReaderV3Tests` (E2E)
- [PDF and OCR](areas/reading-pdf.md): `Jularr.Backend/Logic/Reading/BookConversion.cs`, `Jularr.Integrations/Logic/Ocr`; `PdfBookTests`, `OcrTests`
- [Translation, speech](areas/translation-speech.md): `Jularr.Backend/Service/Core/TranslationProviders.cs`, `Jularr.Backend/Logic/Speech`; `TranslationTests`
- [Playback](areas/playback.md): `Jularr.Backend/Service/User/Playback`, `Jularr.Backend/Logic/Playback`; `PlaybackTests`
- [Transcoding and subtitles](areas/transcoding-subtitles.md): `Jularr.Integrations/Logic/Media`; `TranscodeTests`, `SubtitleTests`
- [Media servers](areas/media-servers.md): `Jularr.Integrations/Logic/Plex`, `Jularr.Integrations/Logic/MediaServer`; `MediaServerTests`
- [Anime and manga](areas/anime-manga.md): `Jularr.Integrations/Logic/AniList`; `AniListTests`, `MangaTests`, `VolumeUploadTests`
- [Music](areas/music.md): `Jularr.Integrations/Logic/MusicBrainz`; `MusicTests`
- [Games](areas/games.md): `Jularr.Integrations/Logic/Igdb`; `GameTests`
- [User features](areas/user-features.md): `Jularr.Backend/Service/User/Statistics`, `Jularr.Backend/Service/User/Calendar`; `StatisticsTests`
- [AI (Codex)](areas/ai-codex.md): `Jularr.Integrations/Logic/Codex`, `Jularr.Backend/Service/Admin/Ai`; `CodexConnectionTests`
- [UI framework](areas/ui-framework.md)
- [UI shell](areas/ui-shell.md)

## Tests

- Backend: `tests/Jularr.Backend.Tests`, isolated PostgreSQL only (`tests/Jularr.Backend.Tests/Infrastructure/TestPostgres.cs`).
- Browser: `tests/Jularr.E2E.Tests` (Playwright); paths and setup in [Platform](areas/platform.md#tests).
- Frontend tools: `src/Jularr.Frontend/Tools` (`npm test`); see [UI framework](areas/ui-framework.md#tests).
