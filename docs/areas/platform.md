# Platform: host, services, SQL and contracts

Service lifecycle, API adapter, errors, SQL execution, data contracts and the database baseline.
Index: [CODEMAP](../CODEMAP.md).
Specs: [DATABASE_MIGRATIONS](../DATABASE_MIGRATIONS.md), [SCHEMA_SNAPSHOT_ALIGNMENT](../SCHEMA_SNAPSHOT_ALIGNMENT.md), [CI](../CI.md).

## Backend

- [Backend project](../../src/Jularr.Backend/Jularr.Backend.csproj): class library; [Workers project](../../src/Jularr.Workers/Jularr.Workers.csproj):
  hosted background work that only runs System services ([WorkerRegistration.cs](../../src/Jularr.Workers/WorkerRegistration.cs); timer workers share
  [PeriodicWorker.cs](../../src/Jularr.Workers/PeriodicWorker.cs)); [Frontend Program](../../src/Jularr.Frontend/Program.cs): the only web host.
  Backend host registration: [README](../../README.md#backend-im-gemeinsamen-host).
- [ServiceApi.cs](../../src/Jularr.Backend/Service/Core/ServiceApi.cs): configuration-based host registration (metadata provider addresses in
  [ProviderOptions.cs](../../src/Jularr.Backend/Service/Core/ProviderOptions.cs), section `Providers`, for mirrors), Backend-owned data source,
  service DI, endpoint discovery, parameter/body binding and antiforgery;
  [DeviceBearerHandler.cs](../../src/Jularr.Backend/Service/Core/DeviceBearerHandler.cs) signs apps in with their device token and renews it past half
  its lifetime.
- [ServiceEndpointAttribute.cs](../../src/Jularr.Backend/Service/Core/ServiceEndpointAttribute.cs): class-level API opt-in; unmarked services stay
  internal, System services cannot be exposed.
- [ServiceExceptionHandler.cs](../../src/Jularr.Backend/Service/Core/ServiceExceptionHandler.cs): central sanitized ProblemDetails, including binding
  and data validation failures.
- [ServiceError.cs](../../src/Jularr.Backend/Service/Core/ServiceError.cs): shared API/Razor error codes and resource keys; Frontend `JularrPage` owns
  form feedback.
- [DatabaseBaseline.cs](../../src/Jularr.Backend/Service/Core/DatabaseBaseline.cs), [Schema.sql](../../database/Schema.sql): serialized fresh
  installation, legacy adoption and backup-first numbered migrations from [database/migrations](../../database/migrations) (frozen baseline hash
  [baseline.sha256](../../database/baseline.sha256), [freeze script](../../scripts/freeze-baseline.mjs));
  [DatabaseUpgrade.cs](../../src/Jularr.Backend/Service/Core/DatabaseUpgrade.cs) reports its phases to
  [StartupStatus.cs](../../src/Jularr.Frontend/Framework/StartupStatus.cs) (the status page answered on the configured addresses during backup and
  migrations, `StartupHandover` to the application's server) and [DatabaseMaintenance.cs](../../src/Jularr.Frontend/Framework/DatabaseMaintenance.cs)
  serves the maintenance page on a failed update; [migration contract](../DATABASE_MIGRATIONS.md), focused `DatabaseBaselineTests.cs`,
  `DatabaseMigrationTests.cs` (fake pg_dump), `StartupStatusTests.cs`, `DumpCompressionTests.cs`, E2E `StartupStatusPageTests.cs` and real PostgreSQL
  dump/restore.
- [Data/Logic](../../src/Jularr.Backend/Data/Logic): neutral entity lookups/mutations and session authentication data.
  [AccountProfileData.cs](../../src/Jularr.Backend/Data/Logic/Account/AccountProfileData.cs) is the shared Account/Profile association;
  [AccountParameters.cs](../../src/Jularr.Backend/Data/Logic/Account/AccountParameters.cs) scopes account reads.
- [Data/Enums](../../src/Jularr.Backend/Data/Enums): enums grouped by Account, Service and Sql owner.
- [BaseData.cs](../../src/Jularr.Backend/Data/Common/BaseData.cs): cached field validation, same-name property merging, Id-based list merging and
  partial/list JSON binding.
- [DataField.cs](../../src/Jularr.Backend/Data/Fields/DataField.cs): C#-named field classes and their PostgreSQL types;
  [DataFieldAttribute.cs](../../src/Jularr.Backend/Data/Common/DataFieldAttribute.cs): required/optional and length overrides.
- [Service.cs](../../src/Jularr.Backend/Service/Core/Service.cs): invocation lifecycle, operation hooks and shared transaction.
- [ServiceRuntime.cs](../../src/Jularr.Backend/Service/Core/ServiceRuntime.cs): internal gate, SqlContext factory and central runtime binding.
  Logic, SQL infrastructure and the Plex client are internal; consumers use services and Data contracts.
- [SqlContext.cs](../../src/Jularr.Backend/Infrastructure/Sql/SqlContext.cs): one connection/transaction, shared ordinary reads and Logic execution;
  [SqlExecutor.cs](../../src/Jularr.Backend/Infrastructure/Sql/SqlExecutor.cs): explicit typed binding/mapping, no injected auth IDs;
  [LogicContext.cs](../../src/Jularr.Backend/Infrastructure/Sql/LogicContext.cs): direct Logic calls and guarded short batches.
- [SqlDatabase.cs](../../src/Jularr.Backend/Infrastructure/Sql/SqlDatabase.cs): internal shared data-source owner and host disposal.
- [SqlParams.cs](../../src/Jularr.Backend/Data/SqlParams.cs): typed object binding with cached field metadata and compiled getters.
- [Pagination.cs](../../src/Jularr.Backend/Data/Pagination.cs), [SqlStatement.cs](../../src/Jularr.Backend/Infrastructure/Sql/SqlStatement.cs): shared
  PageRequest and bounded page results, including direct JSON deserialization of items/page/pageSize through the same bounds;
  [ListService.cs](../../src/Jularr.Backend/Service/Core/ListService.cs): once-per-invocation page hook.

## Frontend

- [StartupStatus.cs](../../src/Jularr.Frontend/Framework/StartupStatus.cs),
  [DatabaseMaintenance.cs](../../src/Jularr.Frontend/Framework/DatabaseMaintenance.cs): the self-contained 503 status page on the configured addresses
  while the backup and migrations run (phases from `DatabaseUpgrade`), the hand-over to the application's server, and the failed-update page;
  `StartupStatusTests`, E2E `StartupStatusPageTests`.

## Tests

- Behavior and integration tests are in [Jularr.Backend.Tests](../../tests/Jularr.Backend.Tests).
  Browser tests are in [Jularr.E2E.Tests](../../tests/Jularr.E2E.Tests): [JularrInstance.cs](../../tests/Jularr.E2E.Tests/JularrInstance.cs) starts
  the built host on loopback with its own database from the baselines, Playwright drives Chromium; `FirstRunTests` covers setup, sign-in and every
  menu page, `DevicePairingTests` TV and app pairing, `FilmPathTests` a film and a series episode from setup through storage, library, indexer and
  download client, Discover, request, Wanted search, release choice, download and import to playing and resuming after a new sign-in, the player's
  Quality menu and marking watched or starting over on the details page, `MusicPathTests` an album and `ReadingPathTests` a novel, a manga volume and
  a light novel added on the Works page through to reading on, `FailureTests` a restart during a download, lost database connections, a failed
  download with Retry and an offline library, `ProfilesTests` registration with Owner approval, a user's request and closed administration,
  `LibraryChangesTests` a doubtful release, an upgrade into the recycle bin and a file missing after a scan, `TracksTests` choosing the second audio
  track, a subtitle file with its text and a broken file failing visibly in the player (shared steps in
  [Steps.cs](../../tests/Jularr.E2E.Tests/Steps.cs)), against [FakeServices.cs](../../tests/Jularr.E2E.Tests/FakeServices.cs) (TMDB, MusicBrainz, Open
  Library, AniList, Newznab, SABnzbd on loopback, reached through the `Providers` addresses; other hosts are blocked in the browser); run with
  `dotnet test tests/Jularr.E2E.Tests`, `JULARR_TEST_DB` and, for playback, `JULARR_TEST_FFMPEG`; a failure leaves a screenshot and the page source in
  `TestResults/e2e`.
  CI runs them in their own job with FFmpeg.
- Invocation, internal visibility and PostgreSQL-free consumer signatures: `ServiceFoundationTests.cs`, `ServiceArchitectureTests.cs`.
- Backend-owned data-source lifecycle, direct/API calls, API opt-in, public binding and common errors: `ServiceApiTests.cs` (no database).
- Connection/transaction and SQL binding: `SqlContextTests.cs`, `SqlExecutorTests.cs`; metadata: `DataFieldTests.cs`.
- `Infrastructure/TestPostgres.cs`: explicit isolated PostgreSQL fixture; never use the application database.
