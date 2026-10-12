# Original schema snapshot alignment

The [original table snapshot](https://github.com/Juloc/Jularr/blob/docs/sql-cutoff-schema-json-20261011/docs/SQL_CUTOFF_ORIGINAL_TABLES.json)
contains 73 named tables and explicitly declares `definitionComplete: false`.
It is the agreed entity/relationship foundation, not a complete installation script.
Null definitions are unresolved, not PostgreSQL nullability declarations.

| Scope | Fresh clean-start baseline |
| --- | --- |
| Accounts/Profile/authentication | Original Accounts, AccountRoleTypes, Profiles, AccountProfiles, password/passkey/TOTP/recovery/external-login/challenge/session tables and UiLocales are present. AccountProfiles retains its composite key and no CanManage field. |
| Media foundation | MediaTypes, Works, WorkTitles and WatchlistEntries use original identities and contracts. Seven media types; Anime is a classification, not an eighth type. |
| Localization | UiTranslationMessages and UiTranslations implement the shared keyed catalog and locale/message foreign keys. |
| Structure and inventory | WorkEditions, WorkVersions, MediaAssets, StoredFiles, MediaTracks, LibraryRoots and Operations are present. Owner decision (fewer tables for identical responsibilities): WorkSeasons, WorkEpisodes, WorkVolumes, WorkChapters and WorkTracks are one WorkUnits table with WorkUnitTypes and a same-Work parent; MediaTechnicalAnalyses is a set of probe columns on StoredFiles. MediaAssetTypes, MediaTrackTypes, OperationTypes and OperationStatusTypes are their catalogs. |
| Progress and playback | MediaProgress, ProgressPositionTypes and PlaybackSessions are present with PlaybackModeTypes. TimeProgressPositions is merged into MediaProgress position/duration columns; MediaPlaybackHistory is the set of ended PlaybackSessions. Reading and game positions follow their packages. |
| Remaining original entities | The other 37 snapshot table names (metadata facts, images, reader, timeline, reading/game positions, collections, games extensions and OperationLogs) remain unimplemented. |
| Later functional extensions | Providers, AccountExternalAuthFlows, AuthChallengePurposeTypes, UiTranslationStatusTypes, InstanceSetupState, InstanceSettings, InstanceModules, AcquisitionRequestStatusTypes, AcquisitionRequests, WantedItems and RequestTargets supplement the incomplete snapshot. Arr+ adds AcquisitionConnectionTypes, AcquisitionConnections, AcquisitionProfiles, AcquisitionReleaseStatusTypes and AcquisitionReleases; indexers and download clients share one connection table, and release history doubles as the blocklist. |
| Approved Storage extension | StorageMounts, StorageProtocolTypes and StorageProtocolVersions add configurable physical SMB/NFS/Local connections with Wake-on-LAN settings. LibraryRoots reference them; they are not canonical media identity. |

Twenty-nine original table names currently overlap (WorkExternalIdentities joined with TMDB metadata); eight more are merged as described above. The existing named positive field
definitions align in types/keys/relationships; exact unresolved columns are supplied
by the fresh baseline and later owner decisions. Current Account schema tests verify
PostgreSQL field types/nullability and catalog seeds; focused media and Storage
tests verify their installed baseline behavior.

Two snapshot drafting ambiguities must not become unwanted columns:

- Accounts lists UserName/NormalizedUserName as undefined placeholders, while its
  own contract explicitly excludes those login identities. They remain absent.
- UiTranslationMessages repeats the locale/message-link placeholders. Its own
  contract assigns those foreign keys to UiTranslations, where they are implemented.

The owner's later cover decision also takes precedence over the older image-binary
paragraph: standard artwork remains beside NAS media; compressed display covers
and locale variants are to remain available from PostgreSQL during NAS downtime.
That image/cache owner is not implemented by the connection package.

Fresh baselines are explicit and tested only in disposable databases. This alignment
does not claim all 73 entities or their full functional lifecycles are implemented.
