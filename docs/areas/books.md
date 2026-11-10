# Book acquisition and sources

Book release judgement, book sources, Narō, Anna and the source browser.
Index: [CODEMAP](../CODEMAP.md).
Specs: [PARITY_READARR](../PARITY_READARR.md).

## Backend

- Books in Arr+: [BookReleaseJudge.cs](../../src/Jularr.Backend/Logic/Acquisition/BookReleaseJudge.cs) (title and author before format; unnamed
  formats are `EBOOK`; the pure name parser `ReadName`, kind, size per volume and points as in [automatic release
  selection](../AUTOMATIC_RELEASE_SELECTION.md), golden corpus `BookReleaseCorpusTests`), book qualities in
  [VideoQualities.cs](../../src/Jularr.Backend/Data/Enums/Acquisition/VideoQualities.cs), Newznab categories per media range, book placement
  `Author/Title (Year)/Title.ext` with a content check in
  [ImportPlacementLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/ImportPlacementLogic.cs) and
  [LibraryFileTypes.cs](../../src/Jularr.Backend/Logic/Library/LibraryFileTypes.cs) (`CheckDocumentAsync`: an EPUB whose `mimetype` entry is not first
  is read from its bounded ZIP directory, one without it needs `META-INF/container.xml`, which the reader also accepts; a DOCX needs
  `word/document.xml` and a zipped FB2 (`.fb2.zip`, one extension through `LibraryFileTypes.Extension`) an `.fb2` entry; FB2 by its `FictionBook`
  root, RTF by `{\rtf`, TXT by having no binary bytes, MOBI by `BOOKMOBI` or an old PalmDOC's `TEXtREAd`, AZW and AZW3 by `BOOKMOBI`; a DRM-protected
  Kindle book is taken, the reader says why it does not open it; a text file beside other documents of a download is its release note and stays, and
  the library scan skips one beside a book in its folder (`BookTests.Scan_BookFolders_*`); `BookTests.Book_ContentCheck_*`, `BookFormatTests`,
  `MangaTests.LightNovel_Fb2AndDocxDownload_*`).
  A light novel pack naming two or more volumes places each volume on its unit with the volume in its name, skips volumes a present file holds and
  leaves files of no volume in the download; a manga file names a volume by `v01`, or by `Title 01`/`Title - 01`/`Title #01` when the Work has that
  volume, and a chapter by `Chapter 12`/`Ch. 12`/`c012` when the Work has that chapter (placed as `… v02 c012` with its volume); a range, a part
  chapter or a bare number that is also a chapter is never guessed.
  Every file not placed gets an `UnplacedFile` reason (format, failed check, no number, unknown unit, ambiguous, name not storable, present) kept in
  `AttentionProposal`; such a download waits as `import_unmatched_files` (some placed) or `import_nothing_placeable`, and Activity lists the files
  with their reason (`UnmatchedFiles`, `UnmatchedFileReasons`, texts `acquisition.file.*`; `MangaTests.LightNovel_*`,
  `MangaTests.Manga_NumberedAndChapterFiles_*`, `BookTests.Book_ContentThatIsNoEbook_*`); an import never takes a name the inventory still gives an
  assigned file, also a missing one; Open Library metadata via
  [OpenLibraryClient.cs](../../src/Jularr.Integrations/Logic/OpenLibrary/OpenLibraryClient.cs) and
  [BookMetadata.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/BookMetadata.cs) (author fact, cover, `openlibrary` identity); Project Gutenberg
  as a keyless source: [GutenbergClient.cs](../../src/Jularr.Integrations/Logic/Gutenberg/GutenbergClient.cs) searches Gutendex and downloads through
  the confirmed Direct storage policy; the monitor imports through the shared `PlaceAsync`.
- Book sources: [SourceCatalog.cs](../../src/Jularr.Backend/Data/Enums/Acquisition/SourceCatalog.cs) (honest catalog: ready, needs account, planned,
  reference only; well-known Newznab indexers as presets that open the filled-in indexer form) with [source catalog
  services](../../src/Jularr.Backend/Service/Admin/Acquisition/V1/SourceCatalogServices.cs) (read, set up keyless sources);
  [DirectSources.cs](../../src/Jularr.Backend/Service/Admin/Wanted/V1/DirectSources.cs) searches and downloads Gutenberg, OPDS
  ([OpdsClient.cs](../../src/Jularr.Integrations/Logic/Opds/OpdsClient.cs), instance login sent to the catalog origin only) and Anna's Archive
  ([AnnasArchiveClient.cs](../../src/Jularr.Integrations/Logic/AnnasArchive/AnnasArchiveClient.cs), member key only to its API; a running browser
  handles subsequent Anna search and slow pages in its existing session without separate HTTP probes; without a key the public slow download, whose
  own countdown the job stores on the operation (`DownloadWaitUntil`, shown as queued with its time left) and resumes between jobs without a person;
  the countdown page names no file yet and is a wait, not a failure, while a check still goes to attention `source_manual_link`: a person passes the
  check in their own browser and pastes the HTTPS file link through Activity or, with instance setting `UsersConfirmSources` plus account switch
  `MayConfirmSources`, through their own request; waiting downloads are not asked again); releases keep their `SourceUrl`.
  [DirectDownload.cs](../../src/Jularr.Integrations/Logic/Common/DirectDownload.cs): bounded redirects, one renewal of expired signed links, gates go
  to `ISourceGateResolver` whose production implementation never passes them (attention `source_gate`); a page or file behind a browser check is read
  by [SourceBrowser.cs](../../src/Jularr.Integrations/Logic/Browser/SourceBrowser.cs), a real Chrome or Chromium driven over the DevTools protocol
  without automation flags, on a Linux host without `DISPLAY` drawn on its own Xvfb
  ([VirtualDisplay.cs](../../src/Jularr.Integrations/Logic/Browser/VirtualDisplay.cs): free display number, stale locks and a crashed run's server
  removed, stopped after the browser; WebGL through SwiftShader) and headless only without Xvfb (binary from `Browser:Path`, empty switches it off,
  otherwise one on the PATH or at the Windows standard places; a browser that already runs from `Browser:RemoteAddress`; profile in
  `DataPath/browser`; the port of its own from `Browser:Port`) over
  [DevToolsClient.cs](../../src/Jularr.Integrations/Logic/Browser/DevToolsClient.cs); a check the browser cannot pass (a captcha) leaves the source to
  a person; the image ships the script [browser.sh](../../deploy/browser/browser.sh) that adds a browser to a container later, not the browser itself;
  test overrides live only in `SourceTests.cs` and `SourceBrowserTests.cs`.
  A person's Anna's Archive file link is stored on the operation (`AttentionUrl` page to open, `SourceLinkUrl` pasted link; an expired link returns to
  the person) and fetched through [PublicNetwork.cs](../../src/Jularr.Integrations/Logic/Common/PublicNetwork.cs), which connects to public addresses
  only.
- Narō web novels (provider `syosetu`, off until the Owner switches it on in Admin > Providers or sets the source up on the Sources page, which also
  adds the keyless connection type `Narou`; migration `0016_narou_source`): [NarouClient.cs](../../src/Jularr.Integrations/Logic/Narou/NarouClient.cs)
  asks only `api.syosetu.com` (official API: title search or one ncode) and `ncode.syosetu.com` (chapter pages), one request a second per host, pages
  at most 2 MiB; [NarouMetadata.cs](../../src/Jularr.Backend/Service/Admin/Media/V1/NarouMetadata.cs) adds its novels to the light novel search of
  Discover and the Works page through `MetadataCatalog` (a novel found is a catalog Work of `CatalogNamespace` `syosetu` under its Japanese name, the
  card shows the search's name and *Web novel*; unused ones go with the catalog cleanup) and details an opened or requested one as its own light novel
  Work under its ncode (`WorkExternalIdentities` namespace `syosetu`, original language `ja`, the writer as author); `SaveWorkMetadataAsync` never
  merges a web novel and a published edition by title.
  An English or romanized query reaches the web novels through AniList: the Japanese titles of the first three light novel entries found are asked at
  Narō, and a novel whose Japanese title and writer both equal the entry's native title and a native story credit (`AniListMatching.LinkWebNovel`)
  follows the entry under its name (the profile's language: Japanese the native title, else English or romanized); other novels keep their Japanese
  title.
  A requested web novel is matched at once and later by `MatchAniListV1` the same way (`AniListMatcher`, `IsWebNovel`): a clear entry only gives it
  its titles for every language but Japanese (`WorkTitles` of provider anilist, `WorkIdentityMatches` matched, never an `anilist` identity, so the
  edition stays its own Work), title or writer alone waits on the matches page as a web novel for a person to confirm.
  `DirectSources` offers Narō only to a Work with that identity and downloads it in passes of 30 seconds: chapters are kept in the job's staging
  (`narou/`), a pass that ends early reports its share on the operation and the next job continues; Narō failing waits ten minutes with the kept
  chapters (five failures without progress fail the download); then [NarouEpub.cs](../../src/Jularr.Integrations/Logic/Narou/NarouEpub.cs) writes one
  Japanese EPUB 3 (a section per chapter, ruby kept, foreword, afterword and pictures of other hosts left out) that the shared import places and the
  Reader opens.
  Chapter update monitoring is not built.
  Tests `NarouClientTests`, `NarouAcquisitionTests`.
- Anna search health is owned by [AnnasArchiveMirrors.cs](../../src/Jularr.Integrations/Logic/AnnasArchive/AnnasArchiveMirrors.cs); the existing Anna
  client classifies and parses search pages.
  [AnnasArchiveSearchTests.cs](../../tests/Jularr.Backend.Tests/AnnasArchiveSearchTests.cs) covers observed list/table/empty fixtures, mirror
  cooldowns, scoped answer reuse and failure reasons.
  The wanted search shares an `AnnasArchiveSearchContext` across configured Anna connections for one invocation only.

## Tests

- Manual source-browser view: [BrowserAssistance](../../src/Jularr.Backend/Service/Core/BrowserAssistance.cs) owns sign-in session leases and
  reconnect descriptors; [assistance services](../../src/Jularr.Backend/Service/Admin/Browser/V1/BrowserAssistanceServices.cs) enforce permissions and
  modules and resume the closed source's waiting downloads through `DownloadLogic.ResumeSourceDownloadsAsync`;
  [SourceBrowser.Assistance](../../src/Jularr.Integrations/Logic/Browser/SourceBrowser.Assistance.cs) exposes bounded screenshot/input controls;
  [RemoteBrowser](../../src/Jularr.Frontend/Framework/Components/RemoteBrowser.cs) owns the view and its adjacent TS/SCSS.
  Unfinished checks retain the live browser page; closing assistance retries automatic countdown/link resolution without a pasted URL.
  Without a person, [SourceGateRelease](../../src/Jularr.Backend/Service/Core/SourceGateRelease.cs) lets `MonitorDownloadsV1` release the waiting
  downloads of every Anna source once the running source browser's last read succeeded, at most every 30 minutes.
  Focused checks: `BrowserAssistanceTests.cs`, the gate/resume/import/reader regression in `SourceTests.cs`, manual-control/cookie and primary-page
  session tests in `SourceBrowserTests.cs`, and [RemoteBrowserTests.cs](../../tests/Jularr.E2E.Tests/RemoteBrowserTests.cs) for image faults, bounded
  recovery and revoked access in the built client.
- A waiting Anna download opens its server-validated attention page through `OpenDownloadBrowserAssistanceV1` in the assistance services.
  Activity posts only its operation ID; the lease and reconnect retain that ID.
  The source browser preserves matching pages across the Anna client's existing trusted mirrors and navigates to a different requested download
  explicitly.
  `SourceTests`, `SourceBrowserTests` and `RemoteBrowserTests` cover the entry, mirror boundaries, reconnect and automatic import continuation.
- Book derivative identity regressions: [BookReleaseIdentityTests.cs](../../tests/Jularr.Backend.Tests/BookReleaseIdentityTests.cs); automatic Anna
  acquisition and manual conflict decisions: `AnnasArchive_DerivativeIsRejectedBeforeAutomaticGrab` in
  [SourceTests.cs](../../tests/Jularr.Backend.Tests/SourceTests.cs).
