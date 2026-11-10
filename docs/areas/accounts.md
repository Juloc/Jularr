# Accounts, profiles, sessions and sign-in

Accounts, profiles, roles, sessions, authentication, registration, device pairing and profile settings.
Index: [CODEMAP](../CODEMAP.md).
Specs: [APP_API](../APP_API.md), [COMPANION_V1](../COMPANION_V1.md).

## Decisions

Owner decisions; change them only with the owner.

- Sign in (owner 2 Nov): one card in the app's style (panel corners, 12px text, 40px pill fields) with the accent gradient button as a pill.
  Wrong credentials ("Wrong username or password.") and an empty field are the server's messages in a line kept free under each field, never the
  browser's bubble; a message never moves the card.

## Backend

- [Admin account services](../../src/Jularr.Backend/Service/Admin/Account/V1/AccountServices.cs), [versioned account
  contracts](../../src/Jularr.Backend/Data/Admin/Account/V1/Accounts.cs): directory, approval and complete name/role/status replacement through
  Account Logic; last write wins.
  The canonical Account writer retains an enabled Owner; Session Logic revokes obsolete credential proofs in the same transaction.
- [AccountProfileServices.cs](../../src/Jularr.Backend/Service/Admin/Account/V1/AccountProfileServices.cs),
  [AccountProfileLogic.cs](../../src/Jularr.Backend/Logic/Account/AccountProfileLogic.cs): existing profile access assignment/removal, retained owner
  link and transactional session invalidation.
- [AccountSessionServices.cs](../../src/Jularr.Backend/Service/Admin/Account/V1/AccountSessionServices.cs): Owner account-scoped session
  reads/revocation through the canonical Session writer.
- [ProfileServices.cs](../../src/Jularr.Backend/Service/User/Profile/V1/ProfileServices.cs): the one read and one update; permissions, profile
  content-language choices and orchestration.
  Availability: [WorkCardProjection](../../src/Jularr.Backend/Service/User/Library/V1/WorkCardProjection.cs), [bounded catalog
  overlay](../../src/Jularr.Backend/Service/User/Library/V1/WorkAvailabilityServices.cs); scope and checks in [Library
  reads](../LIBRARY_PERFORMANCE.md#current-availability-and-content-languages).
- [ServiceGate.cs](../../src/Jularr.Backend/Service/Core/ServiceGate.cs): DB-role-based account/Profile and Admin grants; MediaManager is limited to
  manual media/Requests/Wanted.
  Role changes are revalidated in the write transaction.
- [ProfileSelectionServices.cs](../../src/Jularr.Backend/Service/User/Profile/V1/ProfileSelectionServices.cs): membership-scoped paged list and
  session profile selection; a PIN-protected profile is selected through `VerifyProfilePinV1`, which commits failed attempts before reporting them.
  PIN rules, hashing and the five-failure/one-minute lockout: [ProfilePinLogic.cs](../../src/Jularr.Backend/Logic/Profile/ProfilePinLogic.cs); the
  profile sets or removes its PIN (`UpdateProfilePinV1`, `RemoveProfilePinV1`), the Owner resets a forgotten one (`ResetProfilePinV1`).
  The `Profiles` instance module (off by default) switches multiple profiles: off, the view `SelectableProfiles`
  ([Instance.sql](../../database/Instance.sql)) offers each account only its main profile (own first, then the oldest), sessions pick it
  automatically, PINs do not apply and creation, deletion and PIN services are unavailable; capability `CanManageProfiles` hides their UI.
  An account creates up to ten own profiles (`CreateProfileV1`); deleting a profile (`DeleteProfileV1`, owner account, active profile) removes its
  progress, lists and links, cancels its open requests, keeps the others with the account (they still count against its request limit) and returns its
  sessions to the picker ([ProfileLogic.cs](../../src/Jularr.Backend/Logic/Profile/ProfileLogic.cs)).
- [SessionServices.cs](../../src/Jularr.Backend/Service/User/Session/V1/SessionServices.cs): live owned session list, revocation and logout.
- Registration (module `Registration`, off by default): [Register](../../src/Jularr.Frontend/Pages/Account/Register/Register.cshtml.cs) and
  `RegisterV1` create a disabled User account with its profile; the sign-in page links to it while the module is on; an administrator enables the
  account on Admin Accounts.
- Device pairing (module `Devices`): a TV or app starts a pairing and shows a six-digit code
  ([DevicePairingServices.cs](../../src/Jularr.Backend/Service/Public/Device/V1/DevicePairingServices.cs)), a signed-in profile looks it up and
  confirms exactly that pairing ([DeviceApprovalServices.cs](../../src/Jularr.Backend/Service/User/Device/V1/DeviceApprovalServices.cs), page
  `/Devices/Pair`), and the device fetches its own revocable session once
  ([DevicePairingLogic.cs](../../src/Jularr.Backend/Logic/Session/DevicePairingLogic.cs)); paired devices are sessions with a renamable device name,
  act as normal users of their profile (no administration, no further pairings, switching profiles with their PIN but neither creating nor deleting
  them), and are listed and revoked like sessions by the account and the Owner; five wrong codes per account in ten minutes are counted in
  `DevicePairingAttempts`, approvals are also limited per address; an expired code is replaced at once; a device without its own name keeps none and
  is shown with the translated default; QR codes use the optional public address of the instance settings, else the request's address as forwarded by
  a trusted reverse proxy ([TrustedProxies.cs](../../src/Jularr.Backend/Service/Core/TrustedProxies.cs): read at start, replaced when the settings are
  committed); apps follow [APP_API.md](../APP_API.md); pages in the [Frontend CODEMAP](../../src/Jularr.Frontend/CODEMAP.md); test `DeviceTests`.
- [ProfileLogic.cs](../../src/Jularr.Backend/Logic/Profile/ProfileLogic.cs): entity locking, business validation and the canonical writer.
- [SessionLogic.cs](../../src/Jularr.Backend/Logic/Session/SessionLogic.cs): the shared read-only session proof for middleware and Execute read
  batches; write authorization locks in account/session/profile order, compatible with foreign-key references, and canonical expiry renewal.
  Concurrency and proof revalidation: `SessionLockTests.cs`, `ProfileServiceGateTests.cs`; batch modes, rollback and read-only guards:
  `ServiceFoundationTests.cs`.
- [AuthenticationServices.cs](../../src/Jularr.Backend/Service/Public/Authentication/V1/AuthenticationServices.cs),
  [PlexAuthenticationServices.cs](../../src/Jularr.Backend/Service/Public/Authentication/V1/PlexAuthenticationServices.cs): Public auth state, login,
  registration requests and Plex orchestration.
- [SessionAuthentication.cs](../../src/Jularr.Backend/Service/Core/SessionAuthentication.cs),
  [SessionCookieEvents.cs](../../src/Jularr.Backend/Service/Core/SessionCookieEvents.cs): request-level database-backed cookie verification; half-life
  renewal commits the locked DB session before refreshing the persistent cookie; signed-out background requests get 401 instead of the sign-in page,
  whose new antiforgery cookie would spoil an open form.
  [ServiceApi.cs](../../src/Jularr.Backend/Service/Core/ServiceApi.cs) registers the stable Data-Protection context and optional persistent key
  directory.
- [AccountLogic.cs](../../src/Jularr.Backend/Logic/Account/AccountLogic.cs),
  [PlexAuthenticationLogic.cs](../../src/Jularr.Backend/Logic/Authentication/PlexAuthenticationLogic.cs): credentials, canonical account/profile
  creation, external identity and single-use completion.
- [Profile.cs](../../src/Jularr.Backend/Data/User/Profile/V1/Profile.cs): public Profile parameters, inputs and result.
- [ProfileSelection.cs](../../src/Jularr.Backend/Data/User/Profile/V1/ProfileSelection.cs),
  [Session.cs](../../src/Jularr.Backend/Data/User/Session/V1/Session.cs): versioned selection/session contracts with hidden credential transport.
- [Data/Public/Authentication/V1](../../src/Jularr.Backend/Data/Public/Authentication/V1): versioned Public auth contracts;
  [BaseParameters.cs](../../src/Jularr.Backend/Data/Common/BaseParameters.cs),
  [BaseResponse.cs](../../src/Jularr.Backend/Data/Common/BaseResponse.cs): service contract roles over BaseData.
- [ProfileFields.cs](../../src/Jularr.Backend/Data/Fields/Profile/ProfileFields.cs),
  [AccountSessionFields.cs](../../src/Jularr.Backend/Data/Fields/Account/AccountSessionFields.cs): canonical Profile/account-session fields.
- [Data/Fields/Account](../../src/Jularr.Backend/Data/Fields/Account): Account, AccountProfile, credential, challenge and external-login fields; names
  match the singular schema entity.
- [Account.sql](../../database/Account.sql): the single fresh Account/Profile/auth/session/localization baseline; [schema
  scope](../../database/README.md).
  [UiTranslationFields.cs](../../src/Jularr.Backend/Data/Fields/Localization/UiTranslationFields.cs) and
  [UiTranslationStatusType.cs](../../src/Jularr.Backend/Data/Enums/Localization/UiTranslationStatusType.cs) own translation fields/catalog values.

## Frontend

- [Settings](../../src/Jularr.Frontend/Pages/User/Settings/Settings.cshtml.cs): profile settings, the profile's playback section (audio language,
  subtitles with their language and mode, speed, autoplay; one form with the save pill), home shelf order, appearance, the profile's [AniList
  card](../../src/Jularr.Frontend/Pages/User/Settings/AniList.entry.scss) (Open AniList and the pasted code to connect; connected: who, a switch for
  sending progress, disconnect; with ProgressSync) and granted administration links;
  [Sessions](../../src/Jularr.Frontend/Pages/Account/Sessions/Sessions.cshtml.cs) sets each device's quality.
- [Login](../../src/Jularr.Frontend/Pages/Account/Login/Login.cshtml.cs) (the owner's login mockup: one card, a Plex button only when the
  administrator enabled that sign-in, an inline note that administrators reset passwords, each field's error in its own reserved line;
  [styles](../../src/Jularr.Frontend/Pages/Account/Login/Login.entry.scss)) and
  [Register](../../src/Jularr.Frontend/Pages/Account/Register/Register.cshtml.cs) (request access while the Registration module is on, then the
  approval-pending view; the new account waits until an administrator enables it); both carry the [two round appearance
  buttons](../../src/Jularr.Frontend/Modules/Appearance/AppearanceDock.cs) and the [password
  eye](../../src/Jularr.Frontend/Framework/Components/PasswordReveal.cs),
  [Profiles](../../src/Jularr.Frontend/Pages/Account/Profiles/Profiles.cshtml.cs),
  [PIN](../../src/Jularr.Frontend/Pages/Account/ProfilePin/ProfilePin.cshtml.cs) (numeric input that confirms itself at the PIN's length), [New
  profile](../../src/Jularr.Frontend/Pages/Account/ProfileNew/ProfileNew.cshtml.cs),
  [Sessions](../../src/Jularr.Frontend/Pages/Account/Sessions/Sessions.cshtml.cs),
  [Logout](../../src/Jularr.Frontend/Pages/Account/Logout/Logout.cshtml.cs): canonical service-driven authentication flows.
- [Accounts](../../src/Jularr.Frontend/Pages/Admin/Accounts/Accounts.cshtml.cs): roles, profile assignments, sessions and the account's transcoding
  priority.
- [DeviceLink](../../src/Jularr.Frontend/Pages/Account/DeviceLink/DeviceLink.cshtml.cs) (`/Devices/Link`, anonymous): a browser on a TV shows a
  pairing code and [QrCode](../../src/Jularr.Frontend/Framework/Components/QrCode.cs), kept in a strict cookie, and signs in once confirmed.
- [DevicePair](../../src/Jularr.Frontend/Pages/User/DevicePair/DevicePair.cshtml.cs) (`/Devices/Pair`): shows device, platform and code, then confirms
  it for the active profile; opening it never pairs.
  [DeviceNames](../../src/Jularr.Frontend/Framework/DeviceNames.cs) shows a device without its own name with the translated default.

## Tests

- Profile behavior/rollback: `ProfileReferenceVerticalTests.cs`; authorization/HTTP: `ProfileServiceGateTests.cs`.
- Account/auth schema and catalog/field consistency: `AccountSchemaTests.cs`.
- Owner account updates, last-Owner concurrency, session invalidation and Razor editor: `AdminAccountTests.cs`; catalog authorization, stale edits and
  encoded Razor forms: `AdminLocalizationTests.cs`.
- Account assignments and Owner session revocation: `AdminAccountTests.cs`; language creation/editing/fallback and Razor: `AdminLocaleTests.cs`;
  provider enablement/auth availability and forms: `AdminProviderTests.cs`.
- Login/cookies, pending registration, Plex grants, browser proof, expiry/replay, renewal and deployment-root persistence: `AuthenticationTests.cs`
  (isolated PostgreSQL and controlled provider responses).
- Real Razor forms, antiforgery, language settings, selection rotation, session revocation and logout: `FrontendAuthenticationTests.cs`; requires
  built Frontend assets.
