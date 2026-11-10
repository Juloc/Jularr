# Works administration and media list import

The admin Works list, the title management page, deletion and media list import.
Index: [CODEMAP](../CODEMAP.md).
Specs: [MEDIA_LIST_IMPORT](../MEDIA_LIST_IMPORT.md).

## Decisions

Owner decisions; change them only with the owner.

- Deleting selected Works processes the entire page selection, with visible progress after each title.
  An individual failure does not stop the other titles; successful deletions leave the list, failed titles remain selected and show their own reason
  for a retry.
  During execution the confirmation cannot be submitted twice or dismissed.
  Scripted execution calls the existing deletion service one title at a time; the native form accepts the shared bounded bulk selection, including a
  full 50-title page.
  File/recycle settings, exclusions and download cancellation rules remain with the existing deletion owner.
  An unconfirmed response is reported as uncertain and is never automatically retried; the list is read again before offering another deletion.
- The admin Media list starts in the posters view on a first visit (the cookie keeps any other choice), shows the figures of the list under it
  (titles, monitored, unmonitored, files, total size, for the types, library, profile and words chosen) and starts the library search (filtered, all)
  from its dock; a search run shows as one quiet line under the toolbar only while it runs or right after it was started.

## Backend

- Title management page (built for films and series): the approved mockups for every media type are in
  [docs/mockups/work-management](../mockups/work-management/index.html).
  `ReadWorkManagementV1` (units are seasons, episodes and volumes; an episode carries its absolute number among the title's episodes and, where its
  season maps to AniList entries, the number within the entry, `AcquisitionTests.WorkManagement_NumbersAnimeEpisodesTwoWays_*`; tracks of an album are
  units; `Groups` are a book's editions with language, format, publisher, year and ISBNs, or a game's platforms with their target and pause state, and
  a file carries its `GroupId` (edition or platform) and, for a game, whether its hash is in a ROM catalog of the platform:
  `WorkManagement_GroupsABooksFilesByEdition_AndAGamesByPlatform`;
  [WorkManagementServices.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/WorkManagementServices.cs), contracts in
  [WorkManagement.cs](../../src/Jularr.Backend/Data/Admin/Media/V1/WorkManagement.cs)) reads a title's facts, its seasons and episodes with their
  status (present, missing, downloading, unaired, not monitored, computed in SQL) and its video files, each graded by
  [WorkGradeLogic.cs](../../src/Jularr.Backend/Logic/Library/WorkGradeLogic.cs): quality against the profile's cutoff through `UpgradePolicy` (met,
  upgrade wanted, not allowed), size in MB per minute against the quality's limits (a lean codec may be much smaller) and the custom-format score of
  the release name.
  Tests `WorkGradeLogicTests`, `AcquisitionTests.WorkManagement_*`.
  An administrator deletes files with `DeleteLibraryFilesV1`
  ([ReplacementServices.cs](../../src/Jularr.Backend/Service/Admin/Library/V1/ReplacementServices.cs)): at once, a media file takes its subtitles
  along, each goes as a replaced file does (deleted or recycled by `LibraryReplacements.DeleteAsync`), a missing file only loses its record, the unit
  is missing again and its target is searched at once; the setting `UnmonitorsDeletedUnits` (Library page, off by default) also switches the unit off
  ([FileDeletionLogic.cs](../../src/Jularr.Backend/Logic/Library/FileDeletionLogic.cs); tests `AcquisitionTests.DeleteLibraryFiles_*`).
  `ReadLibraryRenamesV1`/`ApplyLibraryRenamesV1` take an optional `WorkId` for the plan of one title (`LibraryRenameLogic.PlanFor`; the whole plan
  still decides which names are taken).
  `UpdateLibraryFileFactsV1` ([FileFactsLogic.cs](../../src/Jularr.Backend/Logic/Library/FileFactsLogic.cs)) overrides the quality (one the title's
  type knows) and the audio language (every audio track of the files, from `FileLanguages`) of chosen files like Sonarr's editor; a scan keeps it
  while the file is unchanged (`AcquisitionTests.UpdateLibraryFileFacts_*`).
  `DeleteWorkV1` ([WorkDeletionServices.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/WorkDeletionServices.cs),
  [WorkDeletionLogic.cs](../../src/Jularr.Backend/Logic/Media/WorkDeletionLogic.cs)) deletes a title as Sonarr does: refused while a download runs,
  files only when asked (and refused when one stays), requests, targets and releases go, the history keeps its texts (`WorkDeleted`); an exclusion
  (`WorkExclusions` with every provider identity of the title) makes the Discover request endpoints refuse it until `DeleteWorkExclusionV1` removes it
  (listed on the Works page; tests `AcquisitionTests.DeleteWork_*`).
- Media list import (`/Admin/Import`, [format and flow](../MEDIA_LIST_IMPORT.md)):
  [MediaImportParser.cs](../../src/Jularr.Backend/Logic/Media/MediaImportParser.cs) validates and normalizes the JSON,
  [MediaImportPlanner.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/MediaImportPlanner.cs) matches identities and exact local book credits,
  proposes catalog candidates (a series is added from TheTVDB, by its own id, IMDb's through TheTVDB or TMDB's through TMDB's link) and supports
  explicit list-book creation through the existing Work/edition owners,
  [MediaImportServices.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/MediaImportServices.cs) previews (`PreviewMediaImportV1`) and runs
  (`RunMediaImportV1`, new titles through `MetadataCatalog`, targets through `WantedLogic.EnsureWantedItemAsync`); contracts
  [MediaImport.cs](../../src/Jularr.Backend/Data/Admin/Media/V1/MediaImport.cs); tests `MediaImportTests`.
- Durable media-list imports: the page queues one server-owned job (`MediaImportJobServices`, `MediaImportJobLogic`, tables
  `MediaImportJobs`/`MediaImportJobRows` of migration 0013, operation type 7) that `ExecuteMediaImportJobV1` runs in the `mediaImportQueue` lane of
  `OperationWorker`, resumes after a restart and reports row by row to the page (`MediaImport.jobs.ts`).
  Tests `MediaImportJobTests`, `MediaImportDurabilityTests`.

## Frontend

- Selected Works deletion: [Works.cshtml.cs](../../src/Jularr.Frontend/Pages/Admin/Works/Works.cshtml.cs) invokes `DeleteWorksV1` for the native
  selection or the authorized per-title `DeleteOne` fragment; [Works.entry.ts](../../src/Jularr.Frontend/Pages/Admin/Works/Works.entry.ts) owns
  progress and selection across failures.
  Tests: backend `WorkListTests`, E2E `WorkDeletionTests`.
- [Works](../../src/Jularr.Frontend/Pages/Admin/Works/Works.cshtml.cs) and [its list](../../src/Jularr.Frontend/Pages/Admin/Works/Works.List.cs)
  (`/Admin/Works`: the media-type bar (`type=a,b`, as the Library), filters in the address, live title search and an order choice (newest added
  first), three views (posters first, overview, table; `view=`, kept in a cookie), the figures under the list, library search started from the dock,
  columns kept in the browser with the title page's picker, in-row monitoring and search, the mass editor with a per-title report, delete dialog;
  [Works.entry.ts](../../src/Jularr.Frontend/Pages/Admin/Works/Works.entry.ts); shared
  [SwitchButton](../../src/Jularr.Frontend/Framework/Components/SwitchButton.cs),
  [ColumnPicker](../../src/Jularr.Frontend/Framework/Components/ColumnPicker.cs); `WorkListTests`, `WorkPageTests.Works_*`),
  [Work](../../src/Jularr.Frontend/Pages/Admin/Work/Work.cshtml.cs) (metadata, its series and place, a wrong match replaced by another provider entry,
  and named unit sections; [manga volume upload](../../src/Jularr.Frontend/Pages/Admin/WorkUpload/WorkUpload.cshtml.cs)),
  [Person](../../src/Jularr.Frontend/Pages/User/Person/Person.cshtml.cs) (`/People/{id}`: a person's titles, linked from the details' creators, cast
  and crew), [Franchise](../../src/Jularr.Frontend/Pages/User/Franchise/Franchise.cshtml.cs) (a series' titles in their order), [AniList
  matches](../../src/Jularr.Frontend/Pages/Admin/Matches/Matches.cshtml.cs) (a Narō web novel says so; its candidates name their Japanese title and
  writer), [Library inventory](../../src/Jularr.Frontend/Pages/Admin/Library/Library.cshtml.cs): metadata, editing, matching, file identification,
  naming patterns and rename preview.
- [Title page](../../src/Jularr.Frontend/Pages/Admin/Work/Work.Management.cs) at `/Admin/Works/{id}` for films and series (other types keep [their
  metadata](../../src/Jularr.Frontend/Pages/Admin/Work/Work.Metadata.cs)): the hero on its banner with the monitoring switch, searches, refresh and an
  edit dialog; tabs for seasons (accordions with their round button at the exact centre, unit tables) or the film's files, history and metadata; the
  breadcrumb comes from `Page.Parent`.
  [Work.entry.ts](../../src/Jularr.Frontend/Pages/Admin/Work/Work.entry.ts) and [Work.ts](../../src/Jularr.Frontend/Pages/Admin/Work/Work.ts) (tests
  `work.test.mjs`) keep the columns a person chose, their order, sorting and the accordions; the choice lives in the browser.
  [WorkLanguages.cs](../../src/Jularr.Frontend/Pages/Admin/Work/WorkLanguages.cs) shows languages as fixed-colour flags, codes or names.
  `WorkPageTests` drive it; `/Admin/Wanted/{id}` of a film or series redirects here.
  Files are deleted with a trash icon on their row (film files, the only file of an episode, each version) and, for a series, on the tab Manage files:
  every file with a box, the plan of renames to the naming for this title and Delete selected (`DeleteFiles`, `RenameFiles`, `EditFiles` handlers of
  [Work.cshtml.cs](../../src/Jularr.Frontend/Pages/Admin/Work/Work.cshtml.cs); `FilmPathTests` edit and delete them).
  The Markers tab of a series opens [WorkMarkers](../../src/Jularr.Frontend/Pages/Admin/WorkMarkers/WorkMarkers.cshtml.cs) at
  `/Admin/Works/{id}/Markers`: a season's episodes with the marker of each kind and its source, and per episode the four kinds to set, copy over the
  season with an offset, or delete.
  A pencil on a film file and Edit selected files open one dialog for quality and audio language; the hero's Delete opens Sonarr's dialog (also delete
  the files, add an exclusion; `DeleteTitle` handler) and the [Works](../../src/Jularr.Frontend/Pages/Admin/Works/Works.cshtml.cs) page lists the
  exclusions with a remove button (`WorkPageTests`).
  [PostForm](../../src/Jularr.Frontend/Framework/Components/PostForm.cs) is a form of hidden values for such actions.
  In a container narrower than 720 px the unit tables of the seasons give way to strips (`work-strips`, built beside the table by
  `UnitStrip`/`StripFacts` from the same `FileFacts`): each unit is two lines, the first sticky (switch, number, name, actions), the second a tinted
  strip of facts with a handle; a season is one scroller, so its strips slide together by touch, and a mouse drags them (`initializeStrips`); the
  facts follow the column choice, and the columns are a sheet from the bottom (`work-columns--sheet`) opened with the Columns button (`FilmPathTests`
  checks it at 390 px).
  Anime has a column Absolute # and a Standard / AniList switch for the episode numbers (display only, kept in the browser); manga and light novels
  are managed too: their volumes in one table (`Volumes`, kind `volumes` with the columns Released and Format, the same strips and Manage files tab,
  search per volume only for manga, Upload a volume for manga), `WorkTypesTests`.
  An album lists its tracks (kind `tracks`: Length, Format, no monitoring or search per track, the album is monitored and searched as a whole).
  A book without subtype shows its editions and a game its platforms as accordions (`Groups`, kinds `editions` and `games`) with their files; a
  platform has its own target, so its switch, search and interactive search post `wantedItemId` to the Monitor and Automatic handlers, and a game's
  hero keeps only Refresh and Delete; the ROM check shows whether a game file's hash is in a ROM catalog of its platform, Region and Version come from
  the file name ([GameFileName.cs](../../src/Jularr.Backend/Logic/Library/GameFileName.cs)), and a pencil in a platform's header edits that target
  (profile, library, tags) in its own dialog (`work-edit-{target}`, `wantedItemId` to the Choice handler).
  An album of several discs shows one accordion per disc (`WorkUnits.DiscNumber`, set by the MusicBrainz import from the medium); volumes show their
  Pages (counted once per version of a `.cbz` by the scan, `StoredFiles.PageCount`,
  [PageCounter.cs](../../src/Jularr.Backend/Logic/Reading/PageCounter.cs)); the pencil of a film file with several audio tracks offers each track its
  own language (`TrackLanguages` of `UpdateLibraryFileFactsV1`).
  An edition of a book has a Monitored switch and a search of its own: an edition switched off (`WorkEditions.IsMonitored`, `UpdateWorkEditionV1`)
  leaves out releases in its language unless a monitored edition has it (`EditionLanguageFilter` in selection, reason `EditionNotMonitored`), and the
  search icon of an edition wants only its language (`Language` of `SearchWantedDataV1`); files of editions in the audiobook format show Codec, Length
  and Size per hour.
  A book lists its machine translations in an accordion (language, edition, share of the sections translated, size; `ReadingResources` paths
  `:translation/{language}/…`, `DeleteDocumentTranslationV1` clears one language, module Translation) and an edition without a file in a language it
  has a translation for says "Reads translated" (`WorkTypesTests.Book_ListsItsTranslations_*`).
  Every file table (a film's files, editions, platforms) has strips beside it like the unit tables, and Manage files has strips with the same boxes
  (select all follows both).
- [Admin library](../../src/Jularr.Frontend/Pages/Admin/Library/Library.cshtml.cs), one address per view in the sidebar's Library area: the overview
  of every library under the shared [media-type bar](../../src/Jularr.Frontend/Framework/Components/MediaTypeBar.cs) (`?type=`; titles, files, size,
  missing and unidentified, last scan, scan), unmatched and assigned files (`?root=` for one library) (keep only one version, protect from automatic
  upgrades), confirmed renames, naming patterns, and Files & versions (upgrade behaviour, several releases, recycle mode, folder and days); [subtitles
  of a video](../../src/Jularr.Frontend/Pages/Admin/Subtitles/Subtitles.cshtml.cs) searched and chosen by hand.
- [Import](../../src/Jularr.Frontend/Pages/Admin/MediaImport/MediaImport.cshtml.cs) (`/Admin/Import`, Works menu): the owner's mockup as two cards
  (source: [FileInput](../../src/Jularr.Frontend/Framework/Components/FileInput.cs) and pasted text with a live reading of the list; behaviour: Add as
  and a [CodeSample](../../src/Jularr.Frontend/Framework/Components/CodeSample.cs) of the format with the example download), then a preview table
  (icon tile, status pill with icon, candidate, per-row action/profile/library, a switch per row) with a dock (select all, count, Update preview,
  Import selected) and the result report; Continue with the rest carries each waiting row's choices and the defaults.
  Shared Form/Table, own [styles](../../src/Jularr.Frontend/Pages/Admin/MediaImport/MediaImport.entry.scss) and
  [client](../../src/Jularr.Frontend/Pages/Admin/MediaImport/MediaImport.entry.ts)
  ([rules](../../src/Jularr.Frontend/Pages/Admin/MediaImport/MediaImport.ts)); E2E `MediaImportTests`, [format](../MEDIA_LIST_IMPORT.md).
