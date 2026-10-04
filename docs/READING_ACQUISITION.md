# Reading acquisition

Books, Manga and Light Novels use the same acquisition boundaries as the rest of Jularr. Razor pages only create or request a canonical target; they never submit directly to SABnzbd and never import a completed download themselves.

## Lifecycle

```text
catalog result
  -> AcquisitionRequest
  -> Searching
  -> Downloading
  -> Importing
  -> Completed / In library
```

A request can also be `Pending`, `Approved`, `Rejected`, or `Failed`.

`AcquisitionRequestService` is the authoritative add/request and permission boundary. The Books, Manga and Light Novel executors search enabled Newznab/Prowlarr Usenet indexers and submit the selected release through `DownloadClientSubmissionService`, which records the job id and the selected download client with its category on the Operation in one write. Books search is coordinated across the metadata catalogs, every enabled OPDS source and enabled Usenet indexers; OPDS records are merged through the existing canonical work matching and Usenet hits stay temporary release candidates. A Books request uses the default source priority direct/free catalog edition → matching OPDS EPUB → Usenet release. A Light Novel request resolves its canonical title/aliases, checks every enabled Reading catalog source for an exact-match `PublicFullText` copy (currently a supported Narō/Syosetu work can import directly), and only then falls back to the shared Usenet release search. Preview, shop, lending and external-reference sources are discovery evidence only and are never copied automatically. The Usenet handoff remains download-client-agnostic so the native downloader can replace external adapters without another Books path.

### Shared release-request state

Every release-backed request payload derives from `ReleaseRequestPayload`: `triedReleases`, `searches`, `nextSearchUtc` and `lastProblem`. `ReleaseRequestTracker` is the one lifecycle that uses them:

- the best untried release is submitted and remembered, so a release is never sent twice;
- without a usable release the request waits 6 h, 12 h, then daily, and fails after 12 searches;
- a release the download client refuses stays tried and the request searches again later;
- the reason the previous release was dropped is shown once in the next status and then cleared.

The media executors only decide what to search for and which releases qualify.

### Wanted

The SABnzbd monitor is the only component that projects external queue/history state onto Operations. It has no media-specific import code.

`WantedAcquisitionService` consumes persisted request and Operation state in bounded batches, the same way for Books, Manga and Light Novels. It:

- searches an `Approved` request only when its persisted backoff is due;
- continues a failed download with the next untried matching release;
- treats a download the owner cancelled as "stop": the request fails with a cancelled note and no other release is grabbed;
- changes a successful download to the real `Importing` state;
- resumes `Importing` after restart;
- keeps infrastructure/storage failures (no completed path, files not reachable) in `Importing` instead of grabbing a duplicate release, and fails the request with the reason when the files are still missing 24 hours after the download finished;
- returns an unsuitable downloaded package to Wanted so another release can be tried.

Retrying a failed download under Operations is refused once the request has moved on to a newer release; retrying the last download of a request that is waiting or failed puts the request back to `Downloading`.

## Completed downloads

`CompletedDownloadImportService` is the one import step for completed downloads:

1. `CompletedDownloadLocationResolver` asks the exact download client recorded on the Operation for the completed path and applies the remote path mappings of the download's media type (Settings → Acquisition → Remote path mapping is set per media type).
2. `CompletedDownloadDispatcher` hands the path to the media type's adapter:
   - Book -> `BookCompletedDownloadImportAdapter` -> `BookCatalogService.ImportBooksFromPathAsync`
   - Manga -> `MangaCompletedDownloadImportAdapter` -> `MangaImportService`
   - Light Novel -> `LightNovelCompletedDownloadImportAdapter` -> `NovelEpubImportService`
   - Anime -> `AnimeImportExecutor` (episode mapping, Sonarr ownership, naming; see [ANIME_ACQUISITION.md](ANIME_ACQUISITION.md#import))
3. The result is recorded on the download Operation (see [Operations](#operations)).

Adapters normalize files; they never poll the download client or schedule retries. After an import, the adapter reconciles metadata where applicable and returns the library URL. Importers are idempotent, so a restart during `Importing` safely retries the same path.

Downloads the owner sends by hand (an NZB URL or file on the Books page, kind `sabnzbd-download`) have no request. Wanted imports them with the same adapters once, records the result on the Operation and gives up with the reason when the files never appear.

### Books

A completed Books job is imported from its own folder, including subfolders, as one book: EPUB is preferred, PDF accepted. The book is linked to the requested catalog entry. A job without a readable EPUB or PDF is an unsuitable release, and Wanted tries the next one. A path Jularr cannot read is not the release's fault: the request waits as `Importing` with the path in its status. When a Books library folder is configured, the original EPUB/PDF is placed there with the chosen import mode and the parsed reader state is derived from that copy. Without one, EPUBs are read in place and PDFs use the legacy `Books:FilesPath`.

### Manga

The Manga library folder (Settings → Acquisition → Media folders) is the final Manga folder on the NAS, for example `/data/media/manga`, with an optional own import mode. Without an own mode, Move / Copy / Hardlink / Hardlink or copy comes from the default import mode. The file operation is the shared `ImportFileTransfer`, the same one Anime uses.

- A completed download goes to `<library>/<series>/<release folder>/`. Only CBZ/ZIP archives and page images are placed. Existing files are never overwritten: an identical file is skipped, so a retry is safe, and a different one gets a numbered name.
- A series is identified by its AniList entry. When a series already matched to the requested AniList id exists, the new volume joins that series instead of creating one series per release. It uses the series' own folder when that folder is inside the Manga library.
- Without a Manga library folder, the download is read where the download client put it. It still joins the matched series.
- `/data/manga-cache` stays a disposable page cache. It is not the library.

### Light Novels

`NovelEpubImportService.ImportDownloadAsync` finds EPUBs at any depth and ignores folder names; the series comes from each EPUB's metadata. Every EPUB is parsed first. A package with EPUBs of several series, or with no usable EPUB, is refused before anything is stored, and Wanted tries the next release.

An approved Syosetu (`ncode`) request is imported directly with `NovelImportService` and never searched on Usenet.

## Reading sources

The Light Novel Add dialog is a manual search over enabled reading sources, shown as one ranked list. The current implementation configures them at `/Admin/ReadingSources` and stores them in `/data/integrations/reading-sources.json`. The target Admin ownership is the shared `Admin → Providers → Reading Sources` family; the legacy/current route must migrate or deep-link there once Provider Settings has feature parity. `ReadingSourceCatalog` remains the canonical adapter catalog rather than growing page-specific provider switches.

| Source | Capability | Access path | Can be added |
| --- | --- | --- | --- |
| Shōsetsuka ni Narō | Public full text | Official Narou API; public Ncode pages | Yes: imported directly, or as a request for request-only accounts |
| AniList | Published edition | GraphQL API | Yes: request and Usenet download |
| BOOK☆WALKER | Preview | Public bookwalker.jp search page (no public API); at most two requests per search, only `/search/` | No: links to the store's own series or book page |
| WebNovel | External reference | Public search page only; the site puts a bot challenge in front of non-browser clients, so it is off by default and reports itself blocked | No: links to the book's public page |
| Internet Archive | External reference | Documented `advancedsearch` API | No: links to the item's page |

### Admin-configurable source policy

Every registered Reading Source must appear in the shared Admin Provider configuration and be extendable without a new settings page.

For each source, an authorized admin can configure, where the adapter supports it:

- enabled/disabled;
- priority;
- participation mode: **Normal** or **Fallback only**;
- applicable reading media/content types;
- language/locale/region restrictions;
- provider-specific search limits/options;
- health/cooldown visibility.

Capabilities and safety limits are declared by the adapter and are not arbitrary admin toggles. The UI may show capabilities such as catalog/metadata, published edition/acquisition target, public full text, preview and external reference, but an admin cannot convert a preview/reference provider into a full-text importer or disable mandatory rights/access checks.

Internet Archive must be available as a configurable fallback provider. When set to **Fallback only**, normal providers are tried first; Internet Archive is queried only when they produce no acceptable result or when an authorized manual-search action explicitly expands to fallback sources. The two example Archive.org items or any other individual item are never hard-coded configuration; Jularr searches the provider generically and evaluates each result.

Future Reading Source adapters register their schema/capabilities with the shared Provider catalog and automatically become configurable in the same family. They do not get bespoke Admin pages.

Discovery-only sources (Preview and External reference) never fetch, import or copy content. BOOK☆WALKER trial reading and WebNovel free chapters are read on the sites themselves; locked and paid chapters stay locked and no DRM, login, session or challenge is touched. The server rejects adding a result from a source that cannot be added, whatever the form posts.

Internet Archive is rights-aware because anyone can upload there. A result is listed only when the Archive itself offers it openly (not access-restricted) or lends it (access-restricted but in the `inlibrary` lending collection). Restricted items without lending and items in the `no-preview` collection are left out. Openly available items are labelled "Open license" when they carry a Creative Commons or public-domain statement, otherwise "Rights not verified". The current implementation only links to the item and never downloads or borrows anything.

A future direct-import capability may be enabled only for content whose public/full-text access is explicitly safe according to the adapter's rights/access rules. Ambiguous uploads remain reference-only. This capability evolution belongs to the same Reading Source provider adapter and Admin Provider settings; it must not create a second Internet Archive configuration path.

Within one participation tier, ordering is relevance first (exact title, title prefix, title contains, other), then the admin-configured source priority, then title. Priority therefore decides between equally relevant results without burying a clearly better match from a lower-priority source, and no source gets a built-in head start. Normal-tier results are evaluated before Fallback-only providers are queried. A published edition and a web novel with the same title are never merged.

Every source is isolated: a source that times out (8 s), errors, rate limits or blocks automated access is reported above the results and the other sources still answer. Health is in-memory (not persisted) and shown on the settings page: a failing source is left alone for 30 s doubling to 10 min, a `429`/`Retry-After` is honoured (30 s to 1 h), and a source that refuses automated access (`401`/`403` or a bot challenge) is not asked again for an hour. There is no retry with other headers and no evasion. Sources are asked with an honest `Jularr/1.0` user agent. Usenet remains the only acquisition path; none of these sources download anything.

## Inbox folders

Each reading media type has its own inbox folder under Settings → Acquisition → Media folders, for files Jularr did not download (a manual copy, another tool). **Scan inbox** imports it with the same adapter as a completed download, as one `media-inbox-import` Operation:

- Books: every EPUB and PDF below the folder. Another media type's inbox inside the Books inbox is left alone.
- Light Novels: EPUBs directly in the folder resolve their series from metadata; EPUBs in a subfolder belong to the series named by that folder.
- Manga: each folder or CBZ/ZIP directly in the inbox is one series and is placed into the Manga library like a download.

A rescan never imports an unchanged file twice. The Books page and the Novels page scan the same folders.

Earlier builds had one Books inbox (Books → Integrations, `/data/books/integrations.json` or `Books:InboxPath`) and read Light Novels from its `light-novels` subfolder. On the first start of this version that inbox becomes the Books inbox folder and `<inbox>/light-novels` the Light Novel inbox folder, unless folders were already set. No media is moved. The old file is then removed and the old keys are not read again.

## NAS layout

All folders are settings; nothing is hard-coded. A layout that keeps completed downloads and the final library apart:

```text
/data/downloads/complete/          SABnzbd categories (Settings → Download clients)
├── anime/
├── manga/
├── lightnovels/
└── books/

/data/media/
├── anime/                         Anime library roots (Admin → System)
└── manga/                         Manga library folder
```

Books and Light Novels can also have final library folders, for example `/data/media/books` and `/data/media/lightnovels`. Jularr keeps the original EPUB/PDF there according to Move / Copy / Hardlink / Hardlink or copy, while parsed chapters and cached assets remain rebuildable derived state. Enter every path as the Jularr container sees it. When SABnzbd runs in another container and reports different paths, add a remote path mapping (Settings → Acquisition), for example `/downloads -> /data/downloads`.

## Operations

A download Operation shows the media type and the download client category. Once the download is handed to its importer, it also shows the import: the path the download client reported, the mapped local path Jularr read, the destination (library folder or Jularr store), the import mode (or "Read in place") and the result. The state is one of waiting for files, imported, release rejected, needs review (Anime), failed or gave up. Anime imports record the same details.

## Release safety

Reading acquisition is Usenet-only. Release identity is checked before ranking. Tried release identities are persisted so a failed or unsuitable NZB is not submitted again. Manga matching understands volume/chapter/range and CBZ/ZIP; Light Novel matching understands volume, language and EPUB; Books prefer EPUB over PDF. Unsupported formats and known wrong title/volume/chapter/language candidates are rejected before submission.

Public Syosetu/Ncode imports are separate legal web-source imports and do not bypass this Usenet lifecycle for published Light Novel acquisitions.

A Light Novel request keeps the author in `Subtitle` and the native title as a search alias in the payload, so a native title is never used as an author in queries.

## Download clients

The SABnzbd monitor polls each download client on its own. An unreachable client only delays its own downloads. Downloads pinned to a disabled client stay active, and the warning about them is logged at most every 15 minutes. Downloads pinned to a removed client fail, so their request can move on.
