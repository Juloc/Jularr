# Instance setup: four steps

Owner decision of 16 October 2026 supersedes the older nine-page wizard and the
separate acquisition-profile step. Setup uses the canonical configuration owners;
it adds neither its own settings store nor database migrations.

## Flow and owners

| Step | Page | Saved behavior |
| --- | --- | --- |
| 1. Owner and language | /Setup | Public ReadSetupLocalesV1 lists all installed languages, marking generated translations. CreateOwnerV1 atomically enables the selected language, applies it to the instance and the first profile, creates the Owner and signs in. Bootstrap remains exactly once. |
| 2. Instance and modules | /Setup/Instance | ReadSetupModulesV1 supplies the existing switches and preset membership. SaveSetupModulesV1 saves the instance name, all shown module rows and progress in one transaction, with ID/version checks. |
| 3. Libraries | /Setup/Storage | The shared Storage editor creates, checks and browses connections and library roots. Its bottom Continue invokes SaveSetupLibrariesV1, disables types/subtypes without a library and advances to Sources atomically. A parent needed by a subtype root stays enabled. |
| 4. Sources and finish | /Setup/Sources | Tabs for metadata, indexers, downloaders, native quality profiles, takeover and provider connections keep their canonical editors inside this step. SaveSetupSourcesV1 preserves an empty key and saves a supplied key. Save changes stays in Sources; Finish setup also completes setup after the canonical checks pass in the same transaction. |

The three presets are Complete, Media server and Requests. Complete includes all
implemented modules, including local Registration. Media server selects playback,
reading, emulation and the associated personal/operational features; Requests
selects Requests, Arr+, Notifications, Profiles, Devices, Diagnostics and Registration. The
Owner may change the displayed switches before Continue; choosing a preset does
not save anything. Preset membership is supplied by the Backend, not duplicated
in browser code. Source-confirmation policy and advanced instance limits remain
in Admin, outside setup.

Local account-registration requests remain available in all presets and still
require Owner approval. Media requests remain available to authorized Jularr
profiles independently of Plex. Plex sign-in and its registration policy are
separate: automatic Plex registration uses the existing access check against the
connected server. Both kinds of registration remain configurable.

## Connections, resume and validation

- New connections, library paths, tests and scans use their ordinary canonical
  operations. Continue saves each main step; it is placed below the content.
- Library folders are selected in the shared native dialog. Folder navigation fetches
  authorized server-rendered lists without navigating the surrounding page or losing
  its draft. Escape/Close preserves the prior selection; Use this folder updates the
  existing form. Large lists have cursor navigation. Without JavaScript the ordinary
  browse links remain available. The same directory service handles SMB, NFS and local paths.
- Sources has a shared tab row on every editor, including an individual profile.
  Indexer and downloader tabs open a creation form immediately and keep their
  connection list visible on mobile. Saving returns to that setup editor.
  Arr+ tabs are hidden when the Acquisition module is disabled. The profile tab
  embeds the native Arr+ settings; it adds no separate profile wrapper.
- Provider editors use native disclosure panels and retain the selected provider
  after saving. A Plex token can be granted through plex.tv instead of copied by
  hand. The existing challenge/flow tables hold an ExternalLink attempt bound to
  the initiating account, browser nonce and inspected server address. It expires
  after five minutes and is consumed with the server configuration, after external
  I/O, in one authorized mutation batch. Login challenges cannot complete it.
- Persisted progress resumes at Modules, Libraries or Sources; the earlier
  indexer/downloader/profile/media progress values all resume at Sources. The
  separate Finish page is removed. Configuration survives interruption; unsaved
  edits remain ordinary form state.
- Missing library roots do not require disabling each type by hand. Continuing
  from Libraries disables unconfigured categories. A configured video type still
  needs TMDB; missing optional indexers/downloaders and FFmpeg remain warnings.
- Unknown, duplicate or stale module rows roll the entire step back. Completion
  rechecks configuration in its transaction. Secrets remain write-only; a blank
  setup TMDB key keeps the saved credential.
- Restore remains available at /Setup/Backup for a compatible Jularr backup.
  Legacy migration remains in stopped Phase B. AI setup is a separate package.
- Only an Owner configures the instance. Permissions, validation, transactions,
  safe paths and provider access remain Backend-owned. NAS outages leave catalog
  and administration browsing usable. No active database is installed or changed.

## Verification

SetupTests cover bootstrap concurrency/closure, generated-language selection,
atomic module edits and rollback, progress, empty libraries and completion.
AdminProviderTests cover Plex PIN completion, expiry, browser/account/purpose
binding and replay. AuthenticationTests retain Plex registration access checks.
SetupWizardTests drive all four steps at desktop and mobile sizes, including
presets, source tabs, credential and connection saves without finishing, and
resume. Existing film/series path tests use the provider disclosure.

The [original wizard specification](https://github.com/Juloc/Jularr/blob/dev/docs/mockups/setup-wizard/SPEC.md)
remains a reference for unchanged behavior; the newer owner decisions above take
precedence. See [CODEMAP](CODEMAP.md) and [Frontend CODEMAP](../src/Jularr.Frontend/CODEMAP.md).
