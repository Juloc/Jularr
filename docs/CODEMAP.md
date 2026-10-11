# Backend CODEMAP

Navigation only. Rules: [CONVENTIONS.md](CONVENTIONS.md). Startup and gaps: [README](../README.md).

## Host and Profile entry points

- [Backend project](../src/Jularr.Backend/Jularr.Backend.csproj): class library; [Frontend Program](../src/Jularr.Frontend/Program.cs): the only web host. Backend host registration: [README](../README.md#backend-im-gemeinsamen-host).
- [ServiceApi.cs](../src/Jularr.Backend/Service/Core/ServiceApi.cs): shared registration, explicit endpoint discovery, parameter/body binding and antiforgery.
- [ServiceEndpointAttribute.cs](../src/Jularr.Backend/Service/Core/ServiceEndpointAttribute.cs): class-level API opt-in; unmarked services stay internal, System services cannot be exposed.
- [ServiceExceptionHandler.cs](../src/Jularr.Backend/Service/Core/ServiceExceptionHandler.cs): central sanitized ProblemDetails, including binding and data validation failures.
- [ProfileServices.cs](../src/Jularr.Backend/Service/User/Profile/V1/ProfileServices.cs): the one read and one update; permissions and orchestration.
- [ServiceGate.cs](../src/Jularr.Backend/Service/Core/ServiceGate.cs): public boundary and verified session access; current protected grants are the two self-profile permissions.
- [ProfileLogic.cs](../src/Jularr.Backend/Logic/Profile/ProfileLogic.cs): entity locking, business validation and the canonical writer.
- [SessionLogic.cs](../src/Jularr.Backend/Logic/Session/SessionLogic.cs): mutable session authorization locks in the write transaction.
- [AuthenticationServices.cs](../src/Jularr.Backend/Service/Public/Authentication/V1/AuthenticationServices.cs), [PlexAuthenticationServices.cs](../src/Jularr.Backend/Service/Public/Authentication/V1/PlexAuthenticationServices.cs): Public auth state, login, registration requests and Plex orchestration.
- [SessionAuthentication.cs](../src/Jularr.Backend/Service/Core/SessionAuthentication.cs), [SessionCookieEvents.cs](../src/Jularr.Backend/Service/Core/SessionCookieEvents.cs): request-level database-backed cookie verification.
- [AccountLogic.cs](../src/Jularr.Backend/Logic/Account/AccountLogic.cs), [PlexAuthenticationLogic.cs](../src/Jularr.Backend/Logic/Authentication/PlexAuthenticationLogic.cs): credentials, canonical account/profile creation, external identity and single-use completion.
- [PlexClient.cs](../src/Jularr.Backend/Infrastructure/Plex/PlexClient.cs): bounded provider protocol calls and user-scoped library access; [AuthenticationOptions.cs](../src/Jularr.Backend/Service/Core/AuthenticationOptions.cs): trusted server configuration.

## Contracts and persistence

- [Profile.cs](../src/Jularr.Backend/Data/User/Profile/V1/Profile.cs): public Profile parameters, inputs and result.
- [Data/Public/Authentication/V1](../src/Jularr.Backend/Data/Public/Authentication/V1): versioned Public auth contracts; [BaseParameters.cs](../src/Jularr.Backend/Data/Common/BaseParameters.cs), [BaseResponse.cs](../src/Jularr.Backend/Data/Common/BaseResponse.cs): service contract roles over BaseData.
- [Data/Logic](../src/Jularr.Backend/Data/Logic): neutral Profile lookup/mutation and session authentication data.
- [ProfileFields.cs](../src/Jularr.Backend/Data/Fields/Profile/ProfileFields.cs), [SessionFields.cs](../src/Jularr.Backend/Data/Fields/Session/SessionFields.cs): canonical Profile/session fields.
- [Data/Fields/Account](../src/Jularr.Backend/Data/Fields/Account): Account, membership, credential, challenge and external-login fields.
- [Data/Enums](../src/Jularr.Backend/Data/Enums): enums grouped by Account, Service and Sql owner.
- [UiLocaleFields.cs](../src/Jularr.Backend/Data/Fields/Localization/UiLocaleFields.cs), [ProviderFields.cs](../src/Jularr.Backend/Data/Fields/Provider/ProviderFields.cs): required locale/provider catalog fields.
- [BaseData.cs](../src/Jularr.Backend/Data/Common/BaseData.cs): cached field validation, same-name property merging, Id-based list merging and partial/list JSON binding.
- [DataField.cs](../src/Jularr.Backend/Data/Fields/DataField.cs): C#-named field classes and their PostgreSQL types; [DataFieldAttribute.cs](../src/Jularr.Backend/Data/Common/DataFieldAttribute.cs): required/optional and length overrides.
- [Service.cs](../src/Jularr.Backend/Service/Core/Service.cs): invocation lifecycle, operation hooks and shared transaction.
- [ServiceRuntime.cs](../src/Jularr.Backend/Service/Core/ServiceRuntime.cs): gate, SqlContext factory and central runtime binding for direct service calls.
- [Infrastructure/Sql](../src/Jularr.Backend/Infrastructure/Sql): SQL object, binding, read guard and transaction execution.
- [SqlParams.cs](../src/Jularr.Backend/Data/SqlParams.cs): typed parameter metadata and binding.
- [Account.sql](../database/Account.sql): the single fresh Account/Profile/auth/session baseline; [schema scope](../database/README.md).

## Focused tests

All tests are in [Jularr.Backend.Tests](../tests/Jularr.Backend.Tests).

- Profile behavior/rollback: `ProfileReferenceVerticalTests.cs`; authorization/HTTP: `ProfileServiceGateTests.cs`.
- Invocation and boundaries: `ServiceFoundationTests.cs`, `ServiceArchitectureTests.cs`.
- API opt-in, public binding and common errors: `ServiceApiTests.cs` (no database).
- Connection/transaction and SQL binding: `SqlContextTests.cs`, `SqlExecutorTests.cs`; metadata: `DataFieldTests.cs`.
- Account/auth schema and catalog/field consistency: `AccountSchemaTests.cs`.
- Login/cookies, pending registration, Plex grants, browser proof, expiry and replay: `AuthenticationTests.cs` (isolated PostgreSQL and controlled provider responses).
- `Infrastructure/TestPostgres.cs`: explicit isolated PostgreSQL fixture; never use the application database.

## Not integrated

Frontend host/forms, owner bootstrap, email/approval workflows, 2FA/WebAuthn completion,
session logout/rotation, old Android/TV consumers, full Profile management and settings.
This reference backend does not complete Phase B or replace an active installation.
