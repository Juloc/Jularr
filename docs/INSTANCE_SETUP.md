# Instance setup: agreed flow

Consolidated flow requested by the owner. The first-Owner form and its Public
services, instance settings and the authenticated Storage connection page are
implemented. Readiness, resumable navigation and final completion remain pending.
Current code owners: [CODEMAP](CODEMAP.md). Keep one Frontend host,
versioned Backend Data/Services, canonical Logic writers and shared transactions.

## Pages and canonical owners

Except for first-Owner services/state and instance name/language settings,
new service names and tables below remain proposals, not existing contracts. Each
Admin owner provides its ordinary read/update/test operations for both Setup and
later Admin settings; do not create a separate configuration system per page.

| Page | Content | Services / Logic | Persistence |
| --- | --- | --- | --- |
| 1. Start & system | First-Owner form is connected. Remaining database/system/storage readiness checks are pending. | Public `ReadSetupV1`, `CreateOwnerV1`; Account Logic owns exactly-once account/profile/password creation and the shared Session writer signs in. | Existing Accounts, AccountPasswords, Profiles, AccountProfiles, UiLocales; implemented InstanceSetupState. |
| 2. Setup type | New instance; later supported backup restore or migration. A source cannot be both restored and migrated. | Setup orchestration delegates to canonical Backup or Migration operations. | InstanceSetupState references the operation; proposed Operations/OperationLogs. No duplicated backup or migration data. |
| 3. Instance & modules | Owner name/language and Library module editor implemented; region/timezone and further preferences remain pending. | Admin settings/module reads and updates; Instance Logic owns revision checks, locale validation and mutation locks. User `ReadInstanceCapabilitiesV1` is separate from configuration; Public presentation supplies name/language only. | Implemented InstanceSettings, InstanceModules and existing UiLocales. MediaTypes remains a separate catalog; per-media/profile preferences are pending. |
| 4. Storage & roots | SMB/NFS configuration and explicit mount operations are connected at /Setup/Storage and Admin. Synology, Compose path registration, logical roots, probes and roles remain pending. | The same Owner Storage read/create/update/execute Services serve Setup and Admin; Storage Logic owns configuration, Integrations and the Linux helper own mount I/O. | Implemented StorageMounts and protocol/version catalogs; LibraryRoots and storage-role relations remain pending. |
| 5. Downloader / Usenet | Optional native Usenet settings or external client; TLS, credentials, connections, workspace/categories and path mapping; connection test. | Admin Downloader read/update/test; Downloader Logic owns configuration; Integrations performs external protocol calls. | Proposed UsenetServers / DownloadClients and their workspace/root references. No fake media module or Arr-specific copy of the library model. |
| 6. Providers | Identity/login, metadata, search/indexers, subtitles, translation/reading as needed; required versus optional status and bounded tests. TMDB is required for Movie/TV unless a supported substitute covers the same capability. | Admin Provider read/update/test; Provider Logic owns saved configuration and capability requirements. Reuse existing Public auth/Plex services. | Existing Providers; proposed ProviderConnections with protected credentials. Extend the canonical owner, not setup-only Plex/TMDB settings. |
| 7. AI | Optional endpoint, credentials, model discovery and Text/Vision/Image assignments. Arr-only setup needs no model download or runtime. | Admin AI read/update/test; AI Logic owns capability/model assignment; Integrations owns transport. | Proposed AiProviders, AiModels and AiModelAssignments, aligned with the agreed model registry before SQL implementation. |
| 8. Existing media | No import, scan allowed roots, or explicitly take over Sonarr/Radarr/Prowlarr settings and monitoring once. Preview mappings/conflicts; never silently rename/move/delete files or write source monitoring. | Library Logic owns inventory/mapping; takeover delegates supported facts into the standalone Jularr pipeline. | Reuse LibraryRoots and canonical media/request/Wanted/operation data; no permanent external-manager dependency. |
| 9. Review & finish | Optional branding near the end; summary/edit links, rerun required checks, warnings versus blockers, finish once checks pass. | Admin Setup `UpdateSetupV1` / `CompleteSetupV1`; Setup Logic owns progress/completion, Branding/Instance Logic owns branding changes. | InstanceSetupState, InstanceSettings; canonical managed assets for logos/icons, no arbitrary external logo URLs. |

## Cross-page behavior

- Current first-Owner slice requires the explicitly configured `JULARR_DB` and
  installed fresh baseline; startup never creates or migrates a database. Login
  redirects fresh instances to `/Setup`. Owner creation commits account, profile,
  password, session and permanent bootstrap closure together. It currently opens
  the authenticated Storage connection page afterward; this is not full wizard completion. Other configuration
  steps and resumable progress remain pending.
- `/Admin/Instance` edits the instance name and UiLocaleId through the same
  services intended for Setup. Its fresh baseline is installed explicitly after
  Account.sql, never by application startup. Library disables the implemented
  catalog/watchlist services and navigation without removing data. Public display
  name/default language and User capabilities are connected to Razor; the wizard
  step, per-media/profile visibility and further modules are not yet integrated.
  No unsupported module toggle is presented; Learning remains deferred.
- Resume on the server: current page, completed configuration and pending operation
  references survive reloads. Recheck stale external health; it is not permanent truth.
- Setup reads use static SELECTs; all mutations go through their canonical Logic.
  Completion and its final database validation share one SqlContext transaction.
  External checks run with finite timeouts outside long transactions; reject changed
  configuration when committing completion.
- The first Owner is entered directly in the startup wizard, without an
  installation code or extra proof. Account Logic locks durable setup state and
  creates the enabled Owner, owned Profile and password exactly once. Later public
  requests cannot create Owners or reopen bootstrap when accounts are removed.
- Credentials are masked/write-only and preserved on unrelated changes. Integrations
  receives only required credentials; no application logs or responses expose them.
- Optional modules disable dependent pages/navigation without deleting stored data.
  Games is conditional on an implemented canonical capability; do not resurrect stale
  module lists or disguise Generic Downloads as a media library. Learning remains deferred.
- Branding supports optional name, safe logo variants/icons and independently optional
  hue; custom branding retains secondary “by Jularr”, default branding does not repeat it.
  All clients share its configuration; later Admin edits invalidate cached branding.
- First-login personalization is separate, skippable profile onboarding: module-enabled
  media order/presets, visibility, theme/accent/accessibility and reset to instance defaults.
  Hiding is not authorization or disabling a module; new types append deterministically.
- UI uses the shared PostgreSQL text catalog, English source and stable error/resource
  keys. Breadcrumb is the page heading; no duplicate heading/description. No runtime
  translation during page reads. Offline language packs remain a future requirement.
- No active database changes, legacy migration, automatic schema installation, heavy
  runtime deployment or unavailable placeholder wizard controls in the first clean-cut slice.

## Decisions before implementing

Owner administers configuration; MediaManager
is limited to manual media management and Requests/Wanted. Storage includes configurable
SMB/NFS/Synology and permitted Compose paths; exact runtime capabilities must be verified
before activating connection/mount controls. Support existing general mounts,
per-library Compose paths and NAS connections configured directly in the wizard;
the latter does not require prebinding every library path in Compose. Prefer system mounts when necessary or
better for throughput/reliability; configure options and privileges explicitly.
NAS downtime must leave ordinary browsing available through retained DB covers.
Store one standard cover on NAS and locale-specific variants in Jularr.
Instance metadata uses a fixed locale or profile locale; localized artwork is an
independent Owner setting. Timezone/region/default-preference contracts need their exact
field/catalog definitions before adding SQL. First Owner uses the current required
Email and DisplayName, not the obsolete UserName/optional-email form.

## Sources and precedence

- [Original nine-page wizard](https://github.com/Juloc/Jularr/blob/dev/docs/mockups/setup-wizard/SPEC.md)
- [Instance defaults and separate first-login onboarding](https://github.com/Juloc/Jularr/issues/877)
- [TMDB capability and actionable provider errors](https://github.com/Juloc/Jularr/issues/872)
- [Optional end-of-setup branding](https://github.com/Juloc/Jularr/issues/876)
- [Non-destructive library reconciliation](https://github.com/Juloc/Jularr/issues/696)
- [Optional managed AI runtimes: planning gate, not implementation](https://github.com/Juloc/Jularr/issues/881)

Latest owner decisions and current [baseline](../database/README.md) override older
module/identity assumptions. Older assistant chat messages were only available as
content references; this consolidation does not claim to recover their missing text.
