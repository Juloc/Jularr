using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class LibraryPerformanceTests
    {
        public TestContext TestContext { get; set; } = null!;

        [TestMethod]
        [TestCategory("Performance")]
        public async Task Library_MixedInventory_ReportsWarmReadLatency()
        {
            if (Environment.GetEnvironmentVariable("JULARR_TEST_LIBRARY_PERFORMANCE") != "1")
            {
                Assert.Inconclusive("Opt in with JULARR_TEST_LIBRARY_PERFORMANCE=1 on the isolated test server.");
            }

            var stress = Environment.GetEnvironmentVariable("JULARR_TEST_LIBRARY_STRESS") == "1";
            var movies = stress ? 2000 : 300;
            var works = stress ? 2120 : 411;
            var query = stress ? "Title 020" : "Title 003";
            var commands = new CommandLog();
            using var logs = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Information).AddProvider(commands));
            await using var host = await Host.CreateAsync(media: true, services: services =>
            {
                services.Replace(ServiceDescriptor.Singleton(provider =>
                {
                    var configuration = provider.GetRequiredService<IConfiguration>();
                    var connection = new NpgsqlConnectionStringBuilder(configuration["JULARR_DB"]!) { Pooling = true };
                    return new SqlDatabase(new NpgsqlDataSourceBuilder(connection.ConnectionString)
                        .UseLoggerFactory(logs).Build());
                }));
            });
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
            await SeedInventoryAsync(host, movies, works, stress);
            if (Environment.GetEnvironmentVariable("JULARR_TEST_LIBRARY_ANALYZE") != "0")
            {
                await host.Fixture.RunSqlAsync("ANALYZE");
            }

            var samples = int.TryParse(Environment.GetEnvironmentVariable("JULARR_TEST_LIBRARY_SAMPLES"), out var configuredSamples) ? configuredSamples : 30;
            var budget = double.TryParse(Environment.GetEnvironmentVariable("JULARR_TEST_LIBRARY_MAX_MS"), System.Globalization.CultureInfo.InvariantCulture, out var maximum) ? maximum : double.PositiveInfinity;
            var overBudget = new List<string>();
            TestContext.WriteLine($"Inventory: {works} works, {(works - movies) * 100} episodes, pool enabled; samples {samples}.");
            foreach (var filter in new[] { "", "librarySortTypeId=2", "librarySortTypeId=3", "librarySortTypeId=4", "mediaTypeIds=2", "page=2", "query=" + Uri.EscapeDataString(query) })
            {
                var durations = new List<double>();
                var httpDurations = new List<double>();
                for (var iteration = 0; iteration < samples + 2; iteration++)
                {
                    commands.Last = null;
                    commands.JitDisabled = false;
                    commands.Executions.Clear();
                    var timings = host.Services.GetRequiredService<ServiceTimings>();
                    var before = timings.Read(100).SingleOrDefault(item => item.Service == "User/ReadLibraryV1")?.TotalMilliseconds ?? 0;
                    var started = Stopwatch.GetTimestamp();
                    using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/library?pageSize=48&" + filter);
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                    var page = await response.Content.ReadFromJsonAsync<PageResult<LibraryItemV1>>();
                    Assert.IsNotNull(page);
                    Assert.AreEqual(48, page.Items.Count);
                    var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var service = timings.Read(100).Single(item => item.Service == "User/ReadLibraryV1").TotalMilliseconds - before;
                    if (iteration >= 2)
                    {
                        durations.Add(service);
                        httpDurations.Add(elapsed);
                    }
                    if (iteration == 0)
                    {
                        TestContext.WriteLine($"{filter}: first HTTP {elapsed:F1} ms, service {service:F1} ms");
                        var cardsPath = Path.Combine(TestContext.TestResultsDirectory!, "cards-" + (filter.Length == 0 ? "default" : Uri.EscapeDataString(filter)) + ".json");
                        await File.WriteAllTextAsync(cardsPath, await response.Content.ReadAsStringAsync());
                        TestContext.AddResultFile(cardsPath);
                        if (commands.Last is { } command)
                        {
                            var values = command.ToDictionary(pair => pair.Key, pair => pair.Value);
                            TestContext.WriteLine($"Library SQL {values["DurationMs"]} ms");
                            await using var connection = await host.Fixture.Database.OpenConnectionAsync();
                            await using var transaction = await connection.BeginTransactionAsync();
                            if (commands.JitDisabled)
                            {
                                await using var configure = new NpgsqlCommand("SET LOCAL jit = off", connection, transaction);
                                await configure.ExecuteNonQueryAsync();
                            }
                            await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + values["CommandText"], connection, transaction);
                            explain.Parameters.AddWithValue(101L);
                            explain.Parameters.AddWithValue(filter == "mediaTypeIds=2" ? new short[] { 2 } : Array.Empty<short>());
                            explain.Parameters.AddWithValue(Array.Empty<short>());
                            explain.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer, Value = DBNull.Value });
                            explain.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer, Value = DBNull.Value });
                            explain.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = filter.StartsWith("query=", StringComparison.Ordinal) ? query : DBNull.Value });
                            explain.Parameters.AddWithValue(48);
                            explain.Parameters.AddWithValue(filter == "page=2" ? 48 : 0);
                            var plan = (string)(await explain.ExecuteScalarAsync())!;
                            var planPath = Path.Combine(TestContext.TestResultsDirectory!, "library-" + (filter.Length == 0 ? "default" : Uri.EscapeDataString(filter)) + ".json");
                            await File.WriteAllTextAsync(planPath, plan);
                            TestContext.AddResultFile(planPath);
                            using var parsed = JsonDocument.Parse(plan);
                            var rootPlan = parsed.RootElement[0];
                            TestContext.WriteLine($"Planning {rootPlan.GetProperty("Planning Time")} ms; execution {rootPlan.GetProperty("Execution Time")} ms");
                            if (rootPlan.TryGetProperty("JIT", out var jit)) TestContext.WriteLine("JIT " + jit.GetRawText());
                        }
                    }
                }

                durations.Sort();
                httpDurations.Sort();
                var p95 = durations[(int)Math.Ceiling(durations.Count * .95) - 1];
                TestContext.WriteLine($"{filter}: warm service median {durations[durations.Count / 2]:F1} ms, p95 {p95:F1} ms");
                TestContext.WriteLine($"{filter}: warm HTTP median {httpDurations[httpDurations.Count / 2]:F1} ms, p95 {httpDurations[(int)Math.Ceiling(httpDurations.Count * .95) - 1]:F1} ms");
                if (p95 >= budget) overBudget.Add($"{filter}: {p95:F1} ms");
            }

            Assert.IsEmpty(overBudget, "Service p95 exceeded the configured budget: " + string.Join("; ", overBudget));
        }

        [TestMethod]
        [TestCategory("Performance")]
        public async Task DiscoverAndWork_MixedInventory_ReportsWarmReadLatency()
        {
            if (Environment.GetEnvironmentVariable("JULARR_TEST_CATALOG_PERFORMANCE") != "1")
                Assert.Inconclusive("Opt in with JULARR_TEST_CATALOG_PERFORMANCE=1 on the isolated test server.");

            var commands = new CommandLog();
            using var logs = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Information).AddProvider(commands));
            await using var host = await Host.CreateAsync(media: true, services: services =>
            {
                services.Replace(ServiceDescriptor.Singleton(provider =>
                {
                    var configuration = provider.GetRequiredService<IConfiguration>();
                    var connection = new NpgsqlConnectionStringBuilder(configuration["JULARR_DB"]!) { Pooling = true };
                    return new SqlDatabase(new NpgsqlDataSourceBuilder(connection.ConnectionString).UseLoggerFactory(logs).Build());
                }));
            });
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
            await SeedInventoryAsync(host, 300, 411, false);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "InstanceModules" ("Key", "IsEnabled") VALUES ('DiscoverCache', TRUE), ('LocalDiscovery', TRUE)
                    ON CONFLICT ("Key") DO UPDATE SET "IsEnabled" = TRUE;
                INSERT INTO "ExternalWorkSyncs" ("Namespace", "RequestsPerMinute") VALUES ('openlibrary', 20);
                INSERT INTO "ExternalWorks" ("Namespace", "Value", "MediaTypeId", "Title", "ReleaseYear", "ReleasedOn", "ImageUrl", "Rating", "Votes", "TrendingRank", "Popularity")
                SELECT 'tmdb_movie', n::text, 1, 'Catalog ' || lpad(n::text, 5, '0'), 2026,
                    current_date + (n % 100) - 50, 'https://image.tmdb.org/t/p/w500/' || n || '.jpg',
                    (n % 100) / 10.0, 1000 + (n % 10000), n, n % 1000
                FROM generate_series(1, 50000) n;
                INSERT INTO "ExternalWorks" ("Namespace", "Value", "MediaTypeId", "MediaSubtypeId", "Title", "ReleaseYear", "ReleasedOn", "ImageUrl", "Rating", "Votes", "TrendingRank", "Popularity")
                SELECT 'anilist', "Value", 2, 1, "Title", "ReleaseYear", "ReleasedOn", "ImageUrl", "Rating", "Votes", "TrendingRank", "Popularity" FROM "ExternalWorks" WHERE "Namespace" = 'tmdb_movie';
                INSERT INTO "ExternalWorks" ("Namespace", "Value", "MediaTypeId", "Title", "ImageUrl", "TrendingRank")
                SELECT 'openlibrary', n::text, 3, 'Book ' || n, 'https://covers.openlibrary.org/b/id/' || n || '.jpg', n FROM generate_series(1, 10000) n;
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                SELECT "Id", 'tmdb_movie', "Id"::text FROM "Works" WHERE "MediaTypeId" = 1;
                UPDATE "Works" SET "MediaSubtypeId" = 1 WHERE "MediaTypeId" = 2;
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") SELECT "Id", 'anilist', ("Id" - 300)::text FROM "Works" WHERE "MediaTypeId" = 2;
                UPDATE "StoredFiles" f SET "MissingSince" = NULL FROM "MediaAssets" a WHERE a."Id" = f."MediaAssetId" AND a."WorkId" = 1;
                """);
            // Publish the catalog through its canonical completion owner, including its one-time statistics preparation.
            await using (var completion = new SqlContext(host.Services.GetRequiredService<SqlDatabase>().DataSource))
            {
                await completion.BeginAsync(SqlAccessMode.ReadWrite);
                var context = new LogicContext(completion);
                foreach (var source in new[] { "tmdb_movie", "anilist", "openlibrary" }) await context.CompleteExternalWorkSyncAsync(source, default);
                await completion.CommitAsync();
            }
            if (Environment.GetEnvironmentVariable("JULARR_TEST_LIBRARY_ANALYZE") != "0") await host.Fixture.RunSqlAsync("ANALYZE");
            var samples = int.TryParse(Environment.GetEnvironmentVariable("JULARR_TEST_LIBRARY_SAMPLES"), out var count) ? count : 30;
            var budget = double.TryParse(Environment.GetEnvironmentVariable("JULARR_TEST_LIBRARY_MAX_MS"), System.Globalization.CultureInfo.InvariantCulture, out var maximum) ? maximum : double.PositiveInfinity;
            var cases = new List<(string Name, string Service, string Path, object? Input)>
            {
                ("film", "User/ReadWorkV1", "/api/v1/works/1", null),
                ("series-present", "User/ReadWorkV1", "/api/v1/works/301", null),
                ("series-empty", "User/ReadWorkV1", "/api/v1/works/380", null)
            };
            foreach (var feed in Enum.GetValues<DiscoverFeedType>())
            {
                cases.Add(("feed-" + feed, "User/ReadDiscoverFeedV1", "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, feed)));
                if (feed != DiscoverFeedType.Upcoming) cases.Add(("catalog-" + feed, "User/ReadDiscoverCatalogFeedV1", "/api/v1/discover/catalog/feed", new ReadDiscoverCatalogFeedDataV1(MediaType.Series, feed, MediaSubtype.Anime)));
            }
            cases.Add(("catalog-books", "User/ReadDiscoverCatalogFeedV1", "/api/v1/discover/catalog/feed", new ReadDiscoverCatalogFeedDataV1(MediaType.Book, DiscoverFeedType.Trending)));
            cases.Add(("feed-page2", "User/ReadDiscoverFeedV1", "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Trending, 2)));
            cases.Add(("shelves", "User/ReadDiscoverShelvesV1", "/api/v1/discover/shelves", new ReadDiscoverShelvesDataV1()));
            if (Environment.GetEnvironmentVariable("JULARR_TEST_CATALOG_CASE") is { } selected) cases.RemoveAll(item => item.Name != selected);
            Assert.IsNotEmpty(cases);
            var failures = new List<string>();
            foreach (var item in cases)
            {
                var durations = new List<double>();
                var httpDurations = new List<double>();
                for (var iteration = 0; iteration < samples + 2; iteration++)
                {
                    commands.Last = null;
                    commands.Executions.Clear();
                    var timings = host.Services.GetRequiredService<ServiceTimings>();
                    var before = timings.Read(100).SingleOrDefault(row => row.Service == item.Service)?.TotalMilliseconds ?? 0;
                    var started = Stopwatch.GetTimestamp();
                    using var response = await host.SendAsync(item.Input is null ? HttpMethod.Get : HttpMethod.Post, item.Path, item.Input);
                    var body = await response.Content.ReadAsStringAsync();
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
                    var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var service = timings.Read(100).Single(row => row.Service == item.Service).TotalMilliseconds - before;
                    if (iteration >= 2) { durations.Add(service); httpDurations.Add(elapsed); }
                    if (iteration == 0)
                    {
                        TestContext.WriteLine($"{item.Name}: {commands.Executions.Count} SQL commands, {commands.Executions.Sum(command => command.Milliseconds):F1} ms SQL total");
                        foreach (var command in commands.Executions.OrderByDescending(command => command.Milliseconds).Take(5))
                            TestContext.WriteLine($"SQL {command.Milliseconds:F1} ms: " + command.Sql.Replace('\n', ' ')[..Math.Min(180, command.Sql.Length)]);
                        var path = Path.Combine(TestContext.TestResultsDirectory!, "catalog-" + item.Name + ".json");
                        await File.WriteAllTextAsync(path, body);
                        TestContext.AddResultFile(path);
                        using var json = JsonDocument.Parse(body);
                        if (item.Input is ReadDiscoverShelvesDataV1)
                            Assert.AreEqual(160, json.RootElement.EnumerateArray().Sum(shelf => shelf.GetProperty("items").GetArrayLength() + shelf.GetProperty("catalogItems").GetArrayLength()));
                        else if (item.Input is not null) Assert.AreEqual(20, json.RootElement.GetArrayLength());
                        if (item.Name is "feed-Trending" or "catalog-Top" or "catalog-books" && Environment.GetEnvironmentVariable("JULARR_TEST_CATALOG_PLANS") == "1")
                        {
                            var catalog = item.Input as ReadDiscoverCatalogFeedDataV1;
                            var shelf = new ExternalWorkShelfData(0, catalog?.MediaTypeId == MediaType.Book ? "openlibrary" : catalog is not null ? "anilist" : "tmdb_movie",
                                catalog?.MediaTypeId ?? MediaType.Movie, catalog?.MediaSubtypeId, catalog?.DiscoverFeedTypeId ?? DiscoverFeedType.Trending);
                            await ExplainShelfAsync(host, commands, shelf, item.Name);
                        }
                    }
                }
                durations.Sort(); httpDurations.Sort();
                var p95 = durations[(int)Math.Ceiling(samples * .95) - 1];
                TestContext.WriteLine($"{item.Name}: service median {durations[samples / 2]:F1} ms, p95 {p95:F1} ms; HTTP p95 {httpDurations[(int)Math.Ceiling(samples * .95) - 1]:F1} ms");
                if (p95 >= budget) failures.Add($"{item.Name}: {p95:F1} ms");
            }
            Assert.IsEmpty(failures, "Service p95 exceeded budget: " + string.Join("; ", failures));
        }

        private async Task ExplainShelfAsync(Host host, CommandLog commands, ExternalWorkShelfData shelf, string name)
        {
            Assert.IsNotNull(commands.Last);
            var sql = (string)commands.Last.Single(pair => pair.Key == "CommandText").Value!;
            var today = DateOnly.FromDateTime(host.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);
            var query = new ExternalWorkShelfQueryData([shelf], 101, today);
            // Npgsql logs positional SQL. Recover the slots from their clauses so this explains the actual executed statement.
            var values = new Dictionary<int, object>();
            foreach (var (pattern, value) in new (string, object)[]
            {
                (@"jsonb_to_recordset\(\$(\d+)", query.Shelves), (@"LIMIT \$(\d+)", ExternalWorkLogic.ShelfSize),
                (@"OFFSET \$(\d+)", query.Offset), ("\"Votes\" >= \\$(\\d+)", ExternalWorkLogic.TopVotes),
                (@"BETWEEN \$(\d+)", today.AddDays(-ExternalWorkLogic.NewWithinDays)), (@"BETWEEN \$\d+ AND \$(\d+)", today),
                (@"<= \$(\d+)", today.AddDays(ExternalWorkLogic.UpcomingWithinDays)), ("\"ProfileId\" = \\$(\\d+)", query.ProfileId)
            })
            {
                var match = Regex.Match(sql, pattern);
                Assert.IsTrue(match.Success, pattern);
                values.Add(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), value);
            }
            await using var connection = await host.Fixture.Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var jit in new[] { "on", "off" })
            {
                await using var configure = new NpgsqlCommand("SET LOCAL jit = " + jit, connection, transaction);
                await configure.ExecuteNonQueryAsync();
                await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql, connection, transaction);
                foreach (var value in values.OrderBy(pair => pair.Key)) explain.Parameters.Add(new NpgsqlParameter { Value = value.Value });
                var plan = (string)(await explain.ExecuteScalarAsync())!;
                var path = Path.Combine(TestContext.TestResultsDirectory!, name + "-jit-" + jit + ".json");
                await File.WriteAllTextAsync(path, plan);
                TestContext.AddResultFile(path);
                using var parsed = JsonDocument.Parse(plan);
                var result = parsed.RootElement[0];
                TestContext.WriteLine($"{name} JIT {jit}: execution {result.GetProperty("Execution Time")} ms; " +
                    (result.TryGetProperty("JIT", out var timing) ? timing.GetRawText() : "no JIT compilation"));
            }
        }

        private static async Task SeedInventoryAsync(Host host, int movies, int works, bool stress)
        {
            var mount = await LibraryTests.CreateMountAsync(host);
            var root = await LibraryTests.CreateRootAsync(host, mount, "Performance", MediaType.Series);

            // A mix of films and series, with missing files, real tracks and per-episode progress.
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear")
                SELECT number, CASE WHEN number <= {movies} THEN 1 ELSE 2 END,
                    'Title ' || lpad(number::text, 5, '0'), 1980 + (number % 45)
                FROM generate_series(1, {works}) AS number;
                INSERT INTO "WorkTitles" ("WorkId", "UiLocaleId", "Title", "IsManual")
                SELECT "Id", 1, "CanonicalTitle", TRUE FROM "Works";
                INSERT INTO "WorkEditions" ("WorkId") SELECT "Id" FROM "Works";
                INSERT INTO "WorkVersions" ("WorkId", "WorkEditionId") SELECT "WorkId", "Id" FROM "WorkEditions";
                INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number")
                SELECT "Id", 1, 1 FROM "Works" WHERE "MediaTypeId" = 2;
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number")
                SELECT season."WorkId", season."Id", 2, number
                FROM "WorkUnits" AS season CROSS JOIN generate_series(1, 100) AS number;
                INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "WorkUnitId", "MediaAssetTypeId")
                SELECT version."WorkId", version."Id", unit."Id", 1
                FROM "WorkVersions" AS version
                    LEFT JOIN "WorkUnits" AS unit ON unit."WorkId" = version."WorkId" AND unit."WorkUnitTypeId" = 2;
                INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId", "CreatedAt", "MissingSince")
                SELECT {root}, 'media/' || asset."Id" || '.mkv', 1000000, now(), now(), asset."Id",
                    timestamptz '2026-01-01' + asset."Id" * interval '1 second', CASE WHEN asset."Id" % 10 = 0 THEN now() END
                FROM "MediaAssets" AS asset
                    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                WHERE {(stress ? "TRUE" : $"asset.\"WorkId\" <= 56 OR asset.\"WorkId\" BETWEEN {movies + 1} AND {movies + 10} AND unit.\"Number\" <= 9")};
                INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt")
                SELECT {root}, 'unmatched/' || number || '.mkv', 1000000, now(), now()
                FROM generate_series(1, {(stress ? 0 : 3365)}) AS number;
                INSERT INTO "MediaTracks" ("StoredFileId", "StreamIndex", "MediaTrackTypeId", "Language", "IsDefault", "IsForced")
                SELECT file."Id", track.number, track.number + 1, CASE WHEN track.number = 1 THEN 'en' ELSE 'de' END, TRUE, FALSE
                FROM "StoredFiles" AS file CROSS JOIN generate_series(1, 2) AS track(number);
                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "PositionMs", "DurationMs", "IsCompleted", "CompletedAt")
                SELECT 101, "WorkId", "Id", 1, 500, 1000, "Number" % 3 = 0, CASE WHEN "Number" % 3 = 0 THEN now() END
                FROM "WorkUnits" WHERE "WorkUnitTypeId" = 2;
                INSERT INTO "WantedItems" ("WorkId") SELECT "Id" FROM "Works";
                """);
        }

        private sealed class CommandLog : ILoggerProvider, ILogger
        {
            public ConcurrentQueue<(string Sql, double Milliseconds)> Executions { get; } = new();
            public IReadOnlyList<KeyValuePair<string, object?>>? Last { get; set; }
            public bool JitDisabled { get; set; }
            public ILogger CreateLogger(string categoryName) => this;
            public void Dispose() { }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (state is IReadOnlyList<KeyValuePair<string, object?>> command
                    && command.FirstOrDefault(pair => pair.Key == "CommandText").Value is string text
                    && command.FirstOrDefault(pair => pair.Key == "DurationMs").Value is { } milliseconds)
                    Executions.Enqueue((text, Convert.ToDouble(milliseconds, System.Globalization.CultureInfo.InvariantCulture)));
                if (state is IReadOnlyList<KeyValuePair<string, object?>> settings
                    && settings.Any(pair => pair.Key == "CommandText" && pair.Value is "SET LOCAL jit = off")) JitDisabled = true;
                if (state is IReadOnlyList<KeyValuePair<string, object?>> values
                    && values.Any(pair => pair.Key == "CommandText" && pair.Value is string sql && sql.Contains("WITH ORDINALITY", StringComparison.Ordinal)))
                {
                    Last = values;
                }
            }
        }
    }
}
