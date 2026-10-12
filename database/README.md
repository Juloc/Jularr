# Fresh PostgreSQL baselines

`Account.sql` installs one fresh PostgreSQL baseline in one transaction. It is
embedded as `Jularr.Database.Account.sql` for isolated tests. The host never applies it.

`Instance.sql` follows `Account.sql` in a new installation. It adds the singleton
InstanceSettings (Name, UiLocaleId, Version) and InstanceModules, using the shared English locale as
its initial language. It is embedded as `Jularr.Database.Instance.sql` for focused
isolated tests; it is never installed automatically on an existing database.
Owner services provide version-checked settings/module edits. Library gates manual
media/watchlist Services; Requests gates request creation/moderation and manual
Wanted management. Disabling either retains data. Public presentation exposes only name/default language, User
capabilities expose implemented grants, and Razor consumes both. Setup module
progress, per-media/profile visibility and Arr+/Playback/Tracking remain pending.

`Media.sql` follows `Account.sql` in a new installation. It is embedded as
`Jularr.Database.Media.sql` and adds MediaTypes, Works, WorkTitles and
WatchlistEntries. It is not a migration for an existing database. Media integration
tests explicitly install it in their disposable Account fixture.

`Acquisition.sql` follows Account and Media in a fresh installation. It adds
AcquisitionRequests, their status catalog, shared whole-Work WantedItems and
RequestTargets. A profile's active Work/language intent is unique; only approval
creates its Wanted link, atomically in the same transaction. Owner/MediaManager
moderate Open requests and pause/resume Wanted. Language intent uses UiLocaleId.
This is manual administration, not an automatic downloader or import pipeline.
Unit/edition targets, rules/quotas, cancellation, search/queue/history and automatic
fulfilment remain pending. Tests explicitly install this embedded baseline only
in their disposable Media fixture; the host never installs it.

The seven seeded media types follow the original table snapshot. Anime remains a
Movie/Series classification, not another media type. A manually created Work has
one neutral manual title source and a managed CanonicalTitle projection. Updates
check Version atomically and preserve other localized/provider title sources.
Watchlist identity is the composite ProfileId/WorkId key; it never changes
monitoring, acquisition, files or another profile's list.

This catalog foundation does not yet install structure units, editions/versions,
assets/files/tracks, metadata policy, image cache, monitoring or Requests/Wanted.

Included tables:

- Accounts, AccountRoleTypes, Profiles, AccountProfiles and UiLocales.
- UiTranslationMessages, UiTranslations and UiTranslationStatusTypes share UiLocales.
- AccountPasswords, AccountPasskeys, AccountTotpFactors and AccountRecoveryCodes.
- Providers, AccountExternalLogins and AccountExternalAuthFlows.
- AccountSessions, AccountAuthChallenges and AuthChallengePurposeTypes.
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
registration creates disabled requests; Plex attempts use expiring, browser-bound,
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
