# Account/auth/session baseline

`Account.sql` installs one fresh PostgreSQL baseline in one transaction. It is
embedded as `Jularr.Database.Account.sql` for isolated tests. The host never applies it.

Included tables:

- Accounts, AccountRoleTypes, Profiles, AccountProfiles and UiLocales.
- UiTranslationMessages, UiTranslations and UiTranslationStatusTypes share UiLocales.
- AccountPasswords, AccountPasskeys, AccountTotpFactors and AccountRecoveryCodes.
- Providers, AccountExternalLogins and AccountExternalAuthFlows.
- AccountSessions, AccountAuthChallenges and AuthChallengePurposeTypes.

The original CLEAN_CUT_DATABASE snapshot supplied by the owner defines the scope;
the existing Phase-B core and auth-lifecycle drafts supply the exact column and
constraint definitions. Latest owner decisions take precedence: Account/Profile/
Passkey entity IDs remain bigint, without an extra public UUID. Session/challenge
public identifiers remain UUIDs; their secrets are represented only by hashes.
WebAuthn Aaguid is protocol metadata, not an entity or session identifier.

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
