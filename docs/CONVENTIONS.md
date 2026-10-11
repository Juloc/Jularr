# Backend conventions

## C#

Use [Microsoft C# conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
and [.NET naming guidelines](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/naming-guidelines)
as the baseline. The explicit repository choices below take precedence; external
documents need not be reread for every task.

- Four spaces, no tabs; Allman braces for declarations, control flow and block-scoped namespaces.
- PascalCase for types, methods, properties and constants; camelCase for parameters and locals; `_camelCase` for private instance fields; `I` prefix for interfaces.
- Use descriptive names, not technical abbreviations. Backend namespaces start with `Jularr.Backend` and follow the folder path, e.g. `Jularr.Backend.Service.User.Profile.V1`.
- Async methods use `Async` when naming is under our control. Framework hooks retain their declared names, such as `OnRead` and `OnUpdate`.
- Prefer `string`, `long`, `bool` and other C# aliases; use `var` when the initializer makes the type clear.
- Use nullable types accurately; do not hide missing values with defaults or unjustified null-forgiving operators.
- Keep coherent control flow together. No forwarding wrappers, artificial mini-methods, speculative interfaces or dependencies.
- Keep straightforward method/constructor signatures, class declarations and calls on one line up to a 200-character guideline (including indentation). Do not wrap a short parameter list merely because it has several arguments. Wrap genuinely long signatures/inheritance or complex expressions at logical boundaries; keep Allman body braces and meaningful section spacing. The backend/integration EditorConfig uses the same limit.
- Propagate cancellation and finite I/O timeouts. Dispose owned resources; preserve exception meaning and do not swallow failures.
- Group meaningful steps with blank lines and short section comments, normally 1-5 words. Apply this throughout handwritten backend/integration code, not just new services. Avoid dense call chains, continuous registration blocks and comments repeating simple properties. XML docs only for reusable contracts needing non-obvious caller guidance.
- No issue numbers, history or specification prose in source comments. Use documentation for that material.

## Structure and ownership

Keep one modular monolith and one canonical owner per responsibility. Current
implementation is one backend class library hosted by `Jularr.Frontend`, not separate
webservers or projects for every layer. Razor uses DI service calls; external clients
use the same services through the shared API adapter.

- Frontend consumers use versioned services and their Data contracts. Host registration and authentication use the designated `Service/Core` adapters. The Backend owns database configuration, connections and transactions; Logic and SQL infrastructure are internal implementation details.
- `src/Jularr.Backend/Data/<Area>/<Feature>/V1`: related public typed parameters, input data and results together, including Public authentication. `V1` is the agreed contract version, not a legacy fallback.
- `Data/Logic/<Entity>`: neutral lookup and mutation data for Logic. `Data/Fields/<Entity>`: canonical database field definitions. `Data/Enums/<Entity>`: enums grouped by their entity or framework owner. `Data/Common`: genuinely shared contracts only.
- Canonical field definitions declare CLR/PostgreSQL types, nullability and value limits once; parameter/data properties reuse the same definition. Fields do not carry table/column metadata. Required/optional variants use the existing overloads.
- Versioned services and their public contracts consistently use the `V1` suffix. Parameters use entity-qualified names (`ProfileId`); results use `Id` for their own entity and qualified names for referenced entities.
- Data contracts are explicit classes with named properties and constructors, not positional records. Service parameters derive from `BaseParameters`, results from `BaseResponse`, inputs and neutral Logic data from `BaseData`; these share field validation. Typed collections use `BaseDataList<T>`. Empty parameter contracts can be omitted on direct calls; required resource parameters cannot. Field classes use C# names (`LongField`, `StringField`, `GuidField`, etc.) and own database type rules, required values and lengths; basic validation runs centrally before service execution or SQL binding, not in each procedure.
- Keep Data properties explicit. Secret properties use `JsonIgnore`; do not hide transport secrets behind private fields or explicit interfaces. Backing fields remain appropriate for partial input change tracking.
- `BaseData.SetData(params BaseData[])` copies provided values into writable properties with exactly the same name. It does not infer aliases from field definitions: assign `updateData.Id = Parameters.ProfileId` explicitly. Omitted partial fields stay unchanged; explicit null is validated separately. List updates match declared `Id` properties, never positions, and reject unknown or duplicate rows before applying changes.
- `Service/User/<Feature>/V1`: declared permissions, result types, module requirements, extra access checks, static SELECTs and orchestration through operation hooks.
- Services do not write tables or contain mutation business rules. Thin HTTP endpoints bind input and expose service results; they do not duplicate permissions or persistence.
- `Logic/<Feature>`: canonical mutations, business validation, locking and transactional state changes. Logic uses neutral Data types, not Service or HTTP types.
- Call logic extensions directly on `Context`, such as `Context.UpdateProfileAsync(...)`; no `Context.Sql.SomeDomainFunction(...)` or extra domain wrapper.
- `Service/Core`: shared invocation and authorization lifecycle. `Infrastructure/Sql`: SQL execution, parameter binding and connection/transaction ownership.
- `src/Jularr.Integrations/Data/<Provider>`: explicit external protocol parameters/results; `Data/Common`: genuinely shared integration contracts/errors. `Logic/<Provider>`: external communication and protocol handling, using those Data classes. Namespaces follow these paths under `Jularr.Integrations`. No Backend dependency, database business rules, roles or session authorization. Plex is implemented. NAS file access and manufacturer administration remain separate responsibilities in this same library once their scope is decided. No extra web host.
- Service constructors do not take `ServiceRuntime`. Each concrete service has one DI constructor, internal when it accepts implementation dependencies. Shared Backend registration activates services and binds the internal runtime once; consumers resolve services through DI. No global runtime or service locator.
- API exposure is explicit through `[ServiceEndpoint(method, route)]` on the service class. Shared `ServiceApi` handles discovery, DI, binding and antiforgery; `ServiceExceptionHandler` handles errors. No feature-specific endpoint/registration classes or repeated HTTP try/catch.
- `PublicService` is the fourth service area, not an Account role. It requires no existing session, grants no protected resource scope and still uses the shared validation/transaction lifecycle. Login/session business rules belong to its concrete service and Logic; public exposure alone does not authenticate credentials.
- `database/`: explicit PostgreSQL schema definitions. `tests/Jularr.Backend.Tests`: focused behavior and integration tests.
- Extend existing owners before adding new helpers or configuration paths. Remove superseded code and stale docs when replacing a path; no compatibility layer without a supported requirement.

## SQL and transaction rules

- PostgreSQL only. Follow `database/Account.sql`, not the old EF/SQLite layout or SQL Server examples.
- Entity IDs/FKs are `bigint` / C# `long` and may also be used externally. Session/token identifiers must not expose sequential internal IDs. Byte-backed catalogs use constrained `smallint`; do not turn them into resource IDs.
- Locale settings refer to `UiLocaleId` (`long`), not a string locale code. Lookup IDs are not session/token secrets and need no fabricated UUID mapping.
- A session UUID is an identifier, not a bearer secret. Keep token secrets separate, persist their hashes and never expose them in results or logs.
- Put each SQL raw-string literal directly in the executing method on `sql.Statement`; pass that SQL object to the context. No SQL constants, external query catalogs or string-built value substitution.
- Bind typed parameter/data objects through `sql.Parameters.Add(...)`. Trusted actor/profile IDs come from the verified invocation context, never request data.
- Parameter binding uses readable data properties, cached field metadata and compiled getters. Npgsql handles named placeholders; PostgreSQL evaluates explicit SQL casts. Unused mapped properties are allowed; duplicate names across supplied objects are rejected. Each property type must have an explicit mapping in the data contract.
- Paged reads explicitly set `sql.Page = Page` (or a validated `PageRequest` in Backend code outside a service). The list-service hook is evaluated once per invocation; SQL binding and the result use that same request. `PageResult<T>` is a BaseResponse and validates nested BaseData items.
- SQL infrastructure has no Account/Profile/auth state and injects no entity IDs. Bind needed trusted values explicitly from the verified caller. Session Logic reads and locks the session by its proof; the gate compares its current Account/Profile with the verified caller.
- Uppercase SQL keywords; double-quote PascalCase table/column names; use clear aliases and explicit columns, never `SELECT *`.
- Format multi-line queries by clause (`SELECT`, `FROM`, `JOIN`, `WHERE`, `SET`); one selected/assigned column per line and aligned conditions.
- Service reads are read-only. All domain writes and reads with mutation locks belong to Logic.
- Use ordinary `ReadRequiredAsync` / `ReadOptionalAsync` for single-row SQL, including Logic locks and mutation `RETURNING`. Put `FOR UPDATE` / `FOR SHARE` in SQL; no separate `ReadForUpdateAsync` or `ExecuteReturningAsync` path. Commands accept `SqlStatement`, not duplicate string/parameter overloads. Read-only and Logic facades share execution but retain their transaction/read guards.
- One service operation owns one `SqlContext` connection and transaction shared across all participating Logic calls. No inner connection, independent transaction or early commit.
- Revalidate mutable authorization in the write transaction and hold appropriate locks. Fail closed for missing, disabled, revoked or foreign resources without leaking private state.
- Cookie middleware verifies the current database session once per HTTP request; the shared gate consumes that verified proof and resolves the permission, including ownership requirements. Direct invocations without middleware revalidate each invocation. Service `ValidatePermissions` compares request entity IDs with verified auth IDs. Do not repeat ownership, membership or session checks in each service or entity SQL. Writes revalidate and lock session state through `Logic/Session` in the same transaction.
- Commit only after successful validation, execution and result checks; any failure rolls the whole operation back. Check expected affected-row counts.
- Read services prepare one read-only transaction and open it on the first SQL call. Cache-only reads still validate inputs, permissions and results but do not open a connection. Writes and Execute batches retain their existing transaction boundaries.
- Enforce relationships, uniqueness and required values in the schema as well as business rules in Logic. Do not bypass integrity constraints.
- Avoid N+1 queries, application filtering of whole tables and per-row save loops; use bounded reads and set-based writes where needed.
- During clean cut, edit only the explicit fresh baseline, not migrations. Never install or migrate the active database automatically. Schema tests use explicit isolated PostgreSQL connections and only their own disposable databases.

## Tests and documentation

- Test changed behavior and realistic regressions, especially authorization and atomic mutations. Reuse existing fixtures; avoid trivial plumbing tests and redundant permutations.
- Use concise `Subject_Condition_Result` names and data-driven equivalent cases. No test XML prose.
- Ordinary changes run the affected build and the smallest relevant test set. Full-suite validation is a separate requested/release gate.
- `README.md` describes startup, current capabilities and integration gaps; it is not a second rules document.
- `docs/CODEMAP.md` indexes current owners, entry points, persistence and focused tests. Read the relevant section first, then inspect only those paths.
- Add a feature-local CODEMAP only when the root map is no longer sufficient, normally 30-60 lines. No map per folder, mirrored source tree or copied architecture prose.
- Specifications or ADRs are only for necessary behavior/decisions that source and contracts do not express clearly; create them only when the task warrants it.
- Keep links and ownership paths current with each affected change. Do not reread unchanged conventions within the same session.
