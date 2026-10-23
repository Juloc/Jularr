using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Library.V1;
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
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") SELECT "Id", 'Genres', 'Drama', TRUE FROM "Works";
                """);
            if (Environment.GetEnvironmentVariable("JULARR_TEST_LIBRARY_ANALYZE") != "0")
            {
                await host.Fixture.RunSqlAsync("ANALYZE");
            }

            var samples = int.TryParse(Environment.GetEnvironmentVariable("JULARR_TEST_LIBRARY_SAMPLES"), out var configuredSamples) ? configuredSamples : 30;
            var budget = double.TryParse(Environment.GetEnvironmentVariable("JULARR_TEST_LIBRARY_MAX_MS"), System.Globalization.CultureInfo.InvariantCulture, out var maximum) ? maximum : double.PositiveInfinity;
            var overBudget = new List<string>();
            var availableWorks = (long)(await host.ScalarAsync("""SELECT count(DISTINCT asset."WorkId") FROM "MediaAssets" AS asset JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" WHERE file."MissingSince" IS NULL AND NOT file."IsSubtitle";"""))!;
            TestContext.WriteLine($"Inventory: {works} works, {(works - movies) * 100} episodes, pool enabled; samples {samples}.");
            foreach (var filter in new[] { "", "librarySortTypeId=2", "librarySortTypeId=3", "librarySortTypeId=4", "mediaTypeIds=2", "page=2", "genres=Drama&audioLanguages=en&subtitleLanguages=de", "progressStateId=InProgress", "query=" + Uri.EscapeDataString(query) })
            {
                var durations = new List<double>();
                var httpDurations = new List<double>();
                for (var iteration = 0; iteration < samples + 2; iteration++)
                {
                    commands.Last = null;
                    commands.JitDisabled = false;
                    var timings = host.Services.GetRequiredService<ServiceTimings>();
                    var before = timings.Read(100).SingleOrDefault(item => item.Service == "User/ReadLibraryV1")?.TotalMilliseconds ?? 0;
                    var started = Stopwatch.GetTimestamp();
                    using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/library?pageSize=48&" + filter);
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                    var page = await response.Content.ReadFromJsonAsync<PageResult<LibraryItemV1>>();
                    Assert.IsNotNull(page);
                    var expected = stress ? 48 : filter switch { "mediaTypeIds=2" or "progressStateId=InProgress" => 10, "page=2" => Math.Max((int)availableWorks - 48, 0), _ when filter.StartsWith("query=", StringComparison.Ordinal) => 10, _ => 48 };
                    Assert.AreEqual(expected, page.Items.Count, filter);
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
                            explain.Parameters.AddWithValue(new short[] { 1 });
                            explain.Parameters.AddWithValue(filter == "mediaTypeIds=2" ? new short[] { 2 } : Array.Empty<short>());
                            explain.Parameters.AddWithValue(Array.Empty<short>());
                            explain.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer, Value = DBNull.Value });
                            explain.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer, Value = DBNull.Value });
                            explain.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = filter.StartsWith("query=", StringComparison.Ordinal) ? query : DBNull.Value });
                            if (filter.Contains("progressStateId", StringComparison.Ordinal)) explain.Parameters.AddWithValue((short)2);
                            if (filter.Contains("genres=", StringComparison.Ordinal)) explain.Parameters.AddWithValue(new[] { "Drama" });
                            if (filter.Contains("audioLanguages=", StringComparison.Ordinal)) explain.Parameters.AddWithValue(new[] { "en" });
                            if (filter.Contains("subtitleLanguages=", StringComparison.Ordinal)) explain.Parameters.AddWithValue(new[] { "de" });
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

            var optionDurations = new List<double>();
            for (var iteration = 0; iteration < samples + 2; iteration++)
            {
                var started = Stopwatch.GetTimestamp();
                using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/library/filter-options");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                var options = (await response.Content.ReadFromJsonAsync<LibraryFilterOptionsV1>())!;
                CollectionAssert.AreEqual(new[] { "Drama" }, options.Genres);
                CollectionAssert.AreEqual(new[] { "en" }, options.AudioLanguages);
                CollectionAssert.AreEqual(new[] { "de" }, options.SubtitleLanguages);
                if (iteration >= 2) optionDurations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            optionDurations.Sort();
            var optionsP95 = optionDurations[(int)Math.Ceiling(optionDurations.Count * .95) - 1];
            TestContext.WriteLine($"Filter options: warm HTTP p95 {optionsP95:F1} ms");
            if (optionsP95 >= budget) overBudget.Add($"filter options HTTP: {optionsP95:F1} ms");
            Assert.IsEmpty(overBudget, "Service p95 exceeded the configured budget: " + string.Join("; ", overBudget));
        }

        private sealed class CommandLog : ILoggerProvider, ILogger
        {
            public IReadOnlyList<KeyValuePair<string, object?>>? Last { get; set; }
            public bool JitDisabled { get; set; }
            public ILogger CreateLogger(string categoryName) => this;
            public void Dispose() { }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
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
