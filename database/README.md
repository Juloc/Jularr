# Account/auth/session baseline

`Account.sql` installs one fresh PostgreSQL baseline in one transaction. It is
embedded as `Jularr.Database.Account.sql` for isolated tests. The host never applies it.

Included tables:

- Accounts, AccountRoleTypes, Profiles, AccountProfiles and UiLocales.
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
Session, Provider or Localization. Enums live separately in Data/Enums by owner.
Role and challenge-purpose enum values match
the smallint seeds. Fields describe value types and limits, not tables or SQL.
Cross-field rules and relationships are enforced by schema constraints and, when
implemented, Logic. Email is unique/case-insensitive; display names are non-unique.
There is no UserName, NormalizedUserName or CanManage compatibility column.

Schema availability is not authentication implementation. Login/session issuance,
rotation/revocation services, password verification, WebAuthn/TOTP protocols and
request-level database-backed cookie validation are not implemented yet. Existing
Profile calls still use ServiceGate; replacing that path is the next auth slice.
PlaybackSessions are a separate domain. Conditional AccountPermissions overrides
are not introduced as a parallel authorization engine. Broader Profile settings,
groups and transfers are outside this base-table slice.

AccountSchemaTests verifies field/column coverage, nullability and PostgreSQL
types, enum seeds, typed binding and representative auth constraints. Profile
regression tests use this same baseline, never a second reduced schema.
