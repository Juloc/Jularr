# Imports: judgement and manual import

Import judgement of finished downloads and the manual import dialog.
Index: [CODEMAP](../CODEMAP.md).

## Decisions

Owner decisions; change them only with the owner.

- The **manual import** is a dialog over the Queue like Sonarr's (also opened from Wanted): choose a folder, then one row per file, green when it
  fits, amber/red with the reason in plain words; only flagged rows need a person; Import moves the chosen files and each row shows where its file
  went or why not.

## Backend

- Import judgement ([ImportJudgement.cs](../../src/Jularr.Backend/Logic/Acquisition/ImportJudgement.cs), used by
  [DownloadImport.cs](../../src/Jularr.Backend/Service/System/Acquisition/V1/DownloadImport.cs); `ImportJudgementTests`,
  `AcquisitionTests.CompletedSabImport_*`): a finished film or series download is *clear* (the files name the series or film, season and episodes the
  grab was made for, in a quality the profile allows; a pack brings every recognised episode) and imports by itself, a *discard* (another series or
  film, only samples or extras, no video; of any type a folder with no file of a format the import takes, as one holding only archives, or
  `import_empty_folder` for none at all; `ReadUnassignableDownloadsAsync` fails such downloads that already wait with "nothing placeable", once, in
  the cleanup pass of `MonitorDownloadsV1`) fails the download so the release is excluded and the target is searched at once, or a *doubt* (another
  episode than wanted, several possible placements, names that say nothing, an unclear title or year, a quality the profile does not allow) that waits
  with its reason in `Operations.AttentionKey` and the files with where their names say they belong in `AttentionProposal`; such a download is not
  read again until a person decides.
- Manual import as Sonarr's ([ManualImportServices.cs](../../src/Jularr.Backend/Service/Admin/Activity/V1/ManualImportServices.cs),
  [ManualImportLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/ManualImportLogic.cs), a dialog over the Queue (`/Admin/Queue?import=true`,
  `&importOperation=` for a waiting download) opened from the Queue's and Wanted's dock and from a waiting queue row's Import, the old
  `/Admin/ManualImport` redirects; `AcquisitionTests.ManualImport_*`, `ManualImportLogicTests`, `MangaTests.Book_FromAnyFolder_*`, E2E
  `ManualImportTests`): a folder of any connected storage is read without a title (`ReadManualImportFilesV1`): every video (no samples or extras),
  ebook and comic is recognised by `ManualImportLogic.Recognise` over its names (its own, then its folders up to the chosen one; a video that counts
  seasons or episodes is an episode, any other a film or else a series, a document a book; titles of the library that contain the name's longest word
  are the candidates, `VideoReleaseJudge.JudgeTitle`/`TitleMatcher` decide, several fitting titles and no clearer name are `work_ambiguous`, none
  `work_unknown`), then each title's files are proposed together as the automatic import names them (`ManualImportLogic.Propose`,
  `ImportPlacementLogic.Plan`/`PlanChosen`: season and episode, a book's volume, a manga's chapter with its volume, else the whole Work), with quality
  (a book's format), languages, release group and what a person must check (`ProblemOf`: `episode_unknown`, `volume_unknown`, `already_in_library`, a
  film's smaller second video `film_has_larger_file`); a waiting download's files are its own title's (`ReadManualImportDownloadV1`, with the files
  already imported as `Handled` and why the import left one out); a film or episode placed for the title its download was grabbed for keeps that
  release's name, group and edition (`ReleaseFacts.OfRelease`, as an import by itself does) and its release counts as imported while the download
  still waits (`MarkDownloadReleaseImportedAsync`), so its language and format score are those of the release.
  The page chooses what fits; a person fixes flagged rows in the Change dialog (title searched in the library, numbers, quality, audio and subtitle
  languages) and `PreviewManualImportV1` names them again.
  `RunManualImportV1` (each choice names its title) and `RunManualImportDownloadV1` place through `DownloadImport.CommitAsync` like every downloader:
  library check, space, a link on one filesystem (else a checked copy), assignment, replacement, Plex, then the sources go (owner decision: every
  import moves; a read-only connection is refused, `import_source_read_only`; a failure leaves the source as it was), and the folders it emptied, the
  chosen one too; each file's outcome names where it went (`ManualImportOutcomeV1.TargetPath`, `SourceKept` when the source could not be deleted).
  The page imports the chosen rows one by one by script (`RunFile`), each row showing its state and outcome, then the figures.
  A manual import records `ActivityEventType.ManualImport` with the person (and the download); chosen languages are stored with the file
  (`StoredFiles.ChosenAudioLanguages`/`ChosenSubtitleLanguages`, `ImportCommitRequest.Languages`) and `SaveStoredFileProbeAsync` applies them to later
  scans ([FileLanguageChoice.cs](../../src/Jularr.Backend/Logic/Library/FileLanguageChoice.cs)).
  A download that waits keeps where its files lie (`ImportSourceRef` inside `AttentionProposal`,
  [ImportProposal.cs](../../src/Jularr.Backend/Logic/Acquisition/ImportProposal.cs)): a doubt, nothing placeable, unmatched book files, an EPUB
  language doubt, and a placing that found every name taken or could not assign (`import_target_conflict`, `import_assignment_failed`); a storage that
  comes back heals by itself.
  Its files may go to several titles; it finishes once every video or document of it is imported (`ManualImportLogic.CompletesDownload`) or when the
  person presses Finish download (`RunManualImportDownloadDataV1.FinishesDownload`); files nobody imported stay.
  After any import (automatic or by hand) the client lets go of a job whose folder no longer holds media (`DownloadImport.RemoveFromClientAsync`,
  `ManualImportLogic.HoldsMedia`; release notes, pictures, samples and subtitles left out do not count): SABnzbd where the connection removes
  completed jobs (the verified leftovers, `DownloadSourceInventory`, an emptied folder is `Empty`), qBittorrent always with what the move left (it
  could not seed moved files); Jularr's own downloaders clear their job folders.
  Test `AcquisitionTests.WaitingDownload_ImportedByHand_*`.

## Frontend

- [Manual import](../../src/Jularr.Frontend/Pages/Admin/Queue/Queue.Import.cs) (a wide
  [Modal](../../src/Jularr.Frontend/Framework/Components/Modal.cs) over the Queue, part of `QueueModel`: `/Admin/Queue?import=true`,
  `&importOperation=` for a waiting download; opened from the Queue's and Wanted's dock (Manual import) and from a waiting queue row's Import; the old
  `/Admin/ManualImport` redirects there through [QueueRedirect](../../src/Jularr.Frontend/Pages/Admin/QueueRedirect/QueueRedirect.cshtml.cs)): first a
  small dialog to choose a folder on any storage ([FolderPicker](../../src/Jularr.Frontend/Framework/Components/FolderPicker.cs)), or the download's
  own folder, then every video, ebook and comic as one row (box, name with the library name or the reason below it, title, episode or volume/chapter,
  quality with release group, language, size, state chip: green Fits, amber/red Please check; a missing title or number is a button that opens the
  Change dialog; rows are named by their file) in one form `#manual-import` inside the region `#manual-import-region`, so its actions swap only the
  dialog; one switch for every file that can go ([client](../../src/Jularr.Frontend/Pages/Admin/Queue/ManualImport.entry.ts),
  [rule](../../src/Jularr.Frontend/Pages/Admin/Queue/ManualImport.ts), `manual-import.test.mjs`).
  Only the files scroll; the dock is the dialog's foot, a phone gets the whole screen and one card per file.
  The Change Modal lies inside that form with local `Form` fields (`Form.Action`) and sets title (searched), numbers, quality and languages (hidden
  `ImportFiles[i].*`), so the server reads all rows again and shows the library's names.
  With script the dock's Import imports the chosen rows one after another through `ImportRunFile` (a fragment: the status chip, and where the file
  went or why not as the line under its name, then the figure strip); without script it posts `Intent=run` and the dialog shows the result list (E2E
  `ManualImportTests`).
