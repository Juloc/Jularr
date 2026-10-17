# Fresh PostgreSQL baselines

`Account.sql` installs one fresh PostgreSQL baseline in one transaction. It is
embedded as `Jularr.Database.Account.sql`. The host applies all baselines in installation order
in one transaction when its database has no table yet ([DatabaseBaseline.cs](../src/Jularr.Backend/Service/Core/DatabaseBaseline.cs)).
`Schema.sql` records the installation's ordered schema history. Existing unversioned
pre-release databases keep their current startup behavior and are not adopted or migrated.
Explicitly versioned release schemas use the same startup owner for backup-first upgrades;
see [release upgrades](../docs/DATABASE_UPGRADES.md).

`Instance.sql` follows `Account.sql` in a new installation. It adds the singleton
InstanceSettings (Name, UiLocaleId, Version) and InstanceModules, using the shared English locale as
its initial language. It is embedded as `Jularr.Database.Instance.sql` for focused
isolated tests; it is never installed on an existing database.
Owner services provide version-checked settings/module edits. InstanceSettings also say
whether several releases of a film or episode are kept and what happens to a replaced
file (deleted, moved to a recycle folder inside its library or to one folder on a storage
connection) and after how many days recycled files leave, plus the public address for QR
codes and the trusted reverse proxies (`cidr[]`). RecycledFiles lists what Jularr
moved there, the only files its cleanup removes; MediaAssets.IsProtected keeps versions
an administrator added or protected from automatic upgrades. Library gates manual
media/watchlist Services; Profiles (off by default) allows several profiles per account,
otherwise the view SelectableProfiles offers each account its main profile only; Requests gates request creation/moderation and manual
Wanted management; Acquisition (Arr+) gates indexers, download clients, search,
grabs, monitoring and import, so Requests also work without automatic acquisition;
Playback gates playback. Disabling a module retains data. Public presentation exposes only name/default language, User
capabilities expose implemented grants, and Razor consumes both. Setup module
progress, per-media/profile visibility and Tracking remain pending.

`Media.sql` follows `Account.sql` in a new installation. It also holds a profile's
Collections (hand-picked CollectionWorks or a saved filter) and WorkUnitGroups, named display
sections over a Work's unit numbers (UnitGroups module), and Franchises, TMDB film series a
Work may name (Franchises module). It is embedded as
`Jularr.Database.Media.sql` and adds MediaTypes, Works, WorkTitles,
WorkExternalIdentities (tmdb/tvdb/imdb mappings, never an alternative Work ID) and
WatchlistEntries. Episode units carry their air date for acquisition; seasons their poster, episodes their still, summary and length from TMDB. It is not a migration for an existing database. Media integration
tests explicitly install it in their disposable Account fixture.

`Acquisition.sql` also holds Notifications with their types and the per-profile
MutedNotificationTypes. It follows Account and Media in a fresh installation. It adds
AcquisitionRequests, their status catalog, shared whole-Work WantedItems and
RequestTargets. A profile's active Work/language intent is unique; only approval
creates its Wanted link, atomically in the same transaction. Owner/MediaManager
moderate Open requests and pause/resume Wanted. Language intent uses UiLocaleId.
RequestRules (limit per rolling period, approval, blocked media keys, offered quality
profiles, one default) and AccountRequestRules (assignment and single-value overrides)
resolve through the view EffectiveRequestRules; requests record their account and
chosen quality.
The Arr+ core adds AcquisitionConnections (Newznab indexers, SABnzbd clients and subtitle providers in
one table, the type's role deciding where a connection is used, protected API keys (indexers may be keyless), cached capabilities, client path mapping, indexer media scope, daily query and grab counts and a failure backoff),
AcquisitionProfiles (one default per media type and subtype, JSON definition for the shared
selection engine), search state on WantedItems and AcquisitionReleases (grabbed,
imported, failed and blocked releases of a target: history and blocklist in one
table; series releases record the season and episode range they claim). Downloads
are `Operations` of type 2 linked to their release. Providers keep an optional
protected credential (the TMDB key).
Unit/edition targets, rules/quotas, cancellation and profile editing remain pending.
Tests explicitly install this embedded baseline only in their disposable Media
fixture; the host never installs it.

`Storage.sql` also keeps StorageMountStates (last observed availability, the Owner's
connect wish, reconnect backoff) and the StorageMountHealth view. It is an explicit fresh baseline for StorageMounts, its SMB/NFS/Local
protocol/version catalogs and per-connection Wake-on-LAN settings. Owner Services protect credentials before persistence.
Setup and Admin use the same configuration; mounting is an explicit Linux helper
operation, not application-startup SQL. Tests install this baseline only in their
disposable fixture. [Runtime](../docs/STORAGE_RUNTIME.md) and
[original schema alignment](../docs/SCHEMA_SNAPSHOT_ALIGNMENT.md) describe its scope.

Media types are Movie, Series, Book, Music and Game. Anime (Movie/Series), Manga
and Light Novel (Book) are MediaSubtypes of their parents (MediaSubtypeParents), never
another Work identity; Works, LibraryRoots and AcquisitionProfiles reference the
pair. A manual subtype, also none, is kept by provider refreshes. MediaTypes and
MediaSubtypes carry the instance switches; ProfileMediaRules hold Owner blocks and
profile hiding. The views MediaCategories, WorkClasses, ProfileMediaCategories and
ProfileWorks are the one effective classification every reader uses. AniList is a
mapping only: WorkExternalIdentities for films and books, WorkUnitExternalIdentities
for series seasons with an episode offset, WorkIdentityMatches for the matching state. A manually created Work has
one neutral manual title source and a managed CanonicalTitle projection. Updates
check Version atomically and preserve other localized/provider title sources.
Watchlist identity is the composite ProfileId/WorkId key; it never changes
monitoring, acquisition, files or another profile's list.

Media.sql also defines `MediaAssetAdditionalUnits` and its read-only `MediaAssetUnitCoverage` view, a same-Work association for several episodes represented by one media asset. This is a fresh-install-only structural foundation; automatic multi-episode assignment and the necessary player/progress/availability integration are not enabled, and an existing installation requires an explicit migration before using this schema.

Media.sql also installs the shared logical chain: WorkUnits (seasons, episodes,
volumes, chapters and tracks in one table with a same-Work parent; episodes and volumes
are searched only while monitored, specials start unmonitored), WorkEditions,
WorkVersions and MediaAssets. Composite foreign keys keep every level on one Work.
A duplicate copy of the same unit becomes another version, never a second asset in
the same version. GamePlatforms are Jularr's platform identities (GameReleases per
owned version, WorkPlatforms per provider listing); GameRomCatalogs and GameRoms keep
the imported libretro No-Intro DATs (SHA-1 to dump name, CC BY-SA 4.0) per platform.
Metadata policy, image cache and monitoring are not installed.

`Playback.sql` follows Library. MediaProgress keeps one current position per profile
and exact Work/unit target; completion is explicit and sticky, and a completed item
restarts at zero. PlaybackSessions record active and ended playback with a public
UUID; ended sessions are the playback history. Instance.sql seeds the Playback module.
GameSaves keep a profile's save memory and one save state per game file, opaque to the
server; a game's play time is its MediaProgress of type Game. Instance.sql seeds the
Emulation module for them. ProviderProfileLinks tie a Jellyfin or Emby user to the profile
it shares progress with, and ProviderProgress keeps the state last agreed per server item
(ProgressSync module, off by default).

`Library.sql` follows Media, Acquisition and Storage. LibraryRoots place logical libraries below
one StorageMount without overlapping paths. StoredFiles are the root-relative
inventory with probe facts (former MediaTechnicalAnalyses columns, the SHA-1 of game ROMs) and release facts (release name,
group, quality key, edition, revision; from the grabbed release on import, otherwise from the file or folder name), MediaTracks hold
probed streams, and Operations/OperationTypes/OperationStatusTypes are the shared
background-operation owner, starting with library scans. Offline storage never
deletes inventory; only complete scans mark assigned files missing.
NamingPatterns hold the Admin's movie and episode file names (placeholders, '/' for folders),
seeded with the import layout.

Included tables:

- Accounts, AccountRoleTypes, Profiles, AccountProfiles and UiLocales.
- UiTranslationMessages, UiTranslations and UiTranslationStatusTypes share UiLocales.
- AccountPasswords, AccountPasskeys, AccountTotpFactors and AccountRecoveryCodes.
- Providers, AccountExternalLogins and AccountExternalAuthFlows.
- AccountSessions (paired devices carry their name and platform), DevicePairings and DevicePairingAttempts (wrong codes per account), AccountAuthChallenges and AuthChallengePurposeTypes.
- InstanceSetupState records first-Owner creation permanently; it is not a second account registry.

The original CLEAN_CUT_DATABASE snapshot supplied by the owner defines the scope;
the existing Phase-B core and auth-lifecycle drafts supply the exact column and
constraint definitions. Latest owner decisions take precedence: Account/Profile/
Passkey entity IDs remain bigint, without an extra public UUID. Session/challenge
public identifiers remain UUIDs; their secrets are represented only by hashes.
WebAuthn Aaguid is protocol metadata, not an entity or session identifier.

Public Setup creates the first enabled Owner directly, without an installation
code, only while bootstrap is open and Accounts is empty. The shared Account
writer creates its owned Profile and password; Session Logic signs in within the
same transaction. InstanceSetupState is locked and closed permanently on success.
Failed validation, profile creation or session creation rolls everything back.
This is first-Owner creation, not completion of all setup configuration.

Every column has a canonical field in Data/Fields, grouped by Account, Profile,
Provider or Localization. Enums live separately in Data/Enums by owner.
Role, challenge-purpose and translation-status enum values match
the smallint seeds. Fields describe value types and limits, not tables or SQL.
Cross-field rules and relationships are enforced by schema constraints and, when
implemented, Logic. Email is unique/case-insensitive; display names are non-unique.
There is no UserName, NormalizedUserName or CanManage compatibility column.

The baseline seeds the Plex provider, while runtime Plex features default to off.
Localization registration synchronizes the English locale and resource declarations
at host startup, never the schema. Message descriptions belong only to PostgreSQL
and start as null until explicitly supplied. Source synchronization never writes
descriptions or translated text. Source/context changes increment message versions,
mark generated/reviewed translations outdated and increment locale catalog revisions;
manual translations are preserved. Translation saves increment the affected locale's
revision. Owner localization services and Razor editors use these canonical writers;
manual edits require the current source and translation revisions.
Owner language administration creates supported locale codes and changes display
name, direction and enablement with a catalog revision check. English cannot be
disabled. Required locale names are constrained in the fresh baseline.
No translation generation, migration or active database installation happens implicitly.
Public login verifies password hashes and issues hashed-secret sessions. Public
registration (module Registration, off by default) creates disabled requests; Plex attempts use expiring, browser-bound,
single-use challenges with protected device keys. Cookie middleware validates the
database session; mutations lock its current state through the shared gate.
Rotation/revocation endpoints and Owner account approval/name/role/status editing
are implemented. Last-enabled-Owner protection uses serializable account updates;
role/status changes increment CredentialRevision and revoke prior sessions.
Enabling an account does not verify its email address. WebAuthn/TOTP completion and
email verification workflows remain pending. Confirmed TOTP factors fail closed during login.
AccountProfileLogic is the canonical association writer, also used by profile
creation. Owner administration adds/removes existing associations without deleting
profiles; removal preserves ownership and atomically detaches/revokes affected
sessions. Owner session revocation uses the existing SessionLogic writer.
Provider administration toggles existing registry entries with expected-state
checking; it does not introduce connection/credential tables or health claims.
PlaybackSessions are a separate domain. Conditional AccountPermissions overrides
are not introduced as a parallel authorization engine. Broader Profile settings,
groups and transfers are outside this base-table slice.

AccountSchemaTests verifies field/column coverage, nullability and PostgreSQL
types, enum seeds, typed binding and representative auth constraints. Profile
regression tests use this same baseline, never a second reduced schema.
