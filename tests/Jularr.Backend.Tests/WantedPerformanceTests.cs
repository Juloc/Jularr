using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
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
    public sealed class WantedPerformanceTests
    {
        public TestContext TestContext { get; set; } = null!;

        [TestMethod]
        [TestCategory("Performance")]
        public async Task Wanted_MixedInventory_ReportsWarmReadLatency()
        {
            if (Environment.GetEnvironmentVariable("JULARR_TEST_WANTED_PERFORMANCE") != "1")
            {
                Assert.Inconclusive("Opt in with JULARR_TEST_WANTED_PERFORMANCE=1 on the isolated test server.");
            }

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
            var mount = await LibraryTests.CreateMountAsync(host);
            var root = await LibraryTests.CreateRootAsync(host, mount, "Performance", MediaType.Series);
            await LibraryTests.CreateRootAsync(host, mount, "Movies", MediaType.Movie);

            // Include unassigned files and incomplete series, as in a partially matched library.
            await host.Fixture.RunSqlAsync($"""
                UPDATE "StorageMounts" SET "IsReadOnly" = FALSE;
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle")
                SELECT number, CASE WHEN number <= 300 THEN 1 ELSE 2 END, 'Title ' || number
                FROM generate_series(1, 411) AS number;
                INSERT INTO "WorkEditions" ("WorkId") SELECT "Id" FROM "Works";
                INSERT INTO "WorkVersions" ("WorkId", "WorkEditionId") SELECT "WorkId", "Id" FROM "WorkEditions";
                INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number")
                SELECT "Id", 1, 1 FROM "Works" WHERE "MediaTypeId" = 2;
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "ReleasedOn")
                SELECT season."WorkId", season."Id", 2, number, current_date - 1
                FROM "WorkUnits" AS season CROSS JOIN generate_series(1, 100) AS number;
                INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "WorkUnitId", "MediaAssetTypeId")
                SELECT version."WorkId", version."Id", unit."Id", 1
                FROM "WorkVersions" AS version
                    LEFT JOIN "WorkUnits" AS unit ON unit."WorkId" = version."WorkId" AND unit."WorkUnitTypeId" = 2;
                INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId", "MissingSince", "QualityKey")
                SELECT {root}, 'media/' || asset."Id" || '.mkv', 1000000, now(), now(), asset."Id",
                    CASE WHEN asset."Id" % 10 = 0 THEN now() END,
                    CASE WHEN asset."Id" % 2 = 0 THEN 'HDTV-720p' ELSE 'WEBDL-1080p' END
                FROM "MediaAssets" AS asset
                    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                WHERE asset."WorkId" <= 56 OR asset."WorkId" BETWEEN 301 AND 310 AND unit."Number" <= 9;
                INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt")
                SELECT {root}, 'unmatched/' || number || '.mkv', 1000000, now(), now()
                FROM generate_series(1, 3365) AS number;
                INSERT INTO "WantedItems" ("WorkId", "IsPaused", "WantedPriorityTypeId")
                SELECT "Id", "Id" % 7 = 0, CASE WHEN "Id" % 5 = 0 THEN 2 ELSE 1 END FROM "Works";
                """);
            if (Environment.GetEnvironmentVariable("JULARR_TEST_WANTED_ANALYZE") == "1") await host.Fixture.RunSqlAsync("ANALYZE");

            var samples = int.TryParse(Environment.GetEnvironmentVariable("JULARR_TEST_WANTED_SAMPLES"), out var configured) ? configured : 30;
            var budget = double.TryParse(Environment.GetEnvironmentVariable("JULARR_TEST_WANTED_MAX_MS"), System.Globalization.CultureInfo.InvariantCulture, out var maximum) ? maximum : double.PositiveInfinity;
            var failures = new List<string>();
            foreach (var view in new[] { -1, 0, 1, 2 })
            {
                var serviceName = view == -1 ? "Admin/ReadWantedSummaryV1" : "Admin/ReadWantedV1";
                var path = view == -1 ? "/api/v1/admin/wanted/summary" : "/api/v1/admin/wanted?pageSize=48&wantedViewTypeId=" + view;
                var durations = new List<double>();
                for (var iteration = 0; iteration < samples + 2; iteration++)
                {
                    commands.Last = null;
                    commands.JitDisabled = false;
                    var timings = host.Services.GetRequiredService<ServiceTimings>();
                    var before = timings.Read(100).SingleOrDefault(item => item.Service == serviceName)?.TotalMilliseconds ?? 0;
                    var started = Stopwatch.GetTimestamp();
                    using var response = await host.SendAsync(HttpMethod.Get, path);
                    var json = await response.Content.ReadAsStringAsync();
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, json);
                    var service = timings.Read(100).Single(item => item.Service == serviceName).TotalMilliseconds - before;
                    if (iteration >= 2) durations.Add(service);
                    if (iteration == 0)
                    {
                        TestContext.WriteLine($"View {view}: first HTTP {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms, service {service:F1} ms");
                        var resultPath = Path.Combine(TestContext.TestResultsDirectory!, $"wanted-{view}-response.json");
                        await File.WriteAllTextAsync(resultPath, json);
                        TestContext.AddResultFile(resultPath);
                        if (commands.Last is { } values)
                        {
                            await using var connection = await host.Fixture.Database.OpenConnectionAsync();
                            await using var transaction = await connection.BeginTransactionAsync();
                            if (commands.JitDisabled)
                            {
                                await using var configure = new NpgsqlCommand("SET LOCAL jit = off", connection, transaction);
                                await configure.ExecuteNonQueryAsync();
                            }
                            await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + values["CommandText"], connection, transaction);
                            if (view != -1)
                            {
                                explain.Parameters.AddWithValue((short)view);
                                explain.Parameters.AddWithValue(48);
                                explain.Parameters.AddWithValue(0);
                            }
                            var plan = (string)(await explain.ExecuteScalarAsync())!;
                            var planPath = Path.Combine(TestContext.TestResultsDirectory!, $"wanted-{view}-plan.json");
                            await File.WriteAllTextAsync(planPath, plan);
                            TestContext.AddResultFile(planPath);
                            using var parsed = JsonDocument.Parse(plan);
                            var rootPlan = parsed.RootElement[0];
                            TestContext.WriteLine($"SQL {values["DurationMs"]} ms; planning {rootPlan.GetProperty("Planning Time")} ms; execution {rootPlan.GetProperty("Execution Time")} ms");
                            if (rootPlan.TryGetProperty("JIT", out var jit)) TestContext.WriteLine("JIT " + jit.GetRawText());
                        }
                    }
                }
                durations.Sort();
                var p95 = durations[(int)Math.Ceiling(durations.Count * .95) - 1];
                TestContext.WriteLine($"View {view}: service median {durations[durations.Count / 2]:F1} ms, p95 {p95:F1} ms ({samples} samples)");
                if (p95 >= budget) failures.Add($"{view}: {p95:F1} ms");
            }
            // Measure the scheduler's selection separately from network searches and downloads.
            var now = DateTime.UtcNow;
            foreach (var selection in new[] { "scheduler", "units", "target" })
            {
                var dueDurations = new List<double>();
                for (var iteration = 0; iteration < samples + 2; iteration++)
                {
                    commands.Last = null;
                    commands.JitDisabled = false;
                    var started = Stopwatch.GetTimestamp();
                    await using var sql = new SqlContext(host.Services.GetRequiredService<SqlDatabase>().DataSource);
                    await sql.BeginAsync(SqlAccessMode.ReadOnly);
                    var ctx = new LogicContext(sql);
                    object result = selection switch
                    {
                        "scheduler" => await ctx.ReadDueWantedItemsAsync(new WantedDueData(now, 5), default),
                        "units" => await ctx.ReadUnitStatesAsync(301, default),
                        _ => await ctx.ReadWantedTargetAsync(301, default)
                    };
                    await sql.CommitAsync();
                    var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (iteration >= 2) dueDurations.Add(elapsed);
                    if (iteration == 0)
                    {
                        var responsePath = Path.Combine(TestContext.TestResultsDirectory!, $"wanted-{selection}-response.json");
                        var json = JsonSerializer.Serialize(result);
                        if (selection != "target")
                        {
                            using var parsed = JsonDocument.Parse(json);
                            Assert.AreEqual(selection == "scheduler" ? 5 : 100, parsed.RootElement.GetArrayLength());
                        }
                        await File.WriteAllTextAsync(responsePath, json);
                        TestContext.AddResultFile(responsePath);
                        Assert.IsNotNull(commands.Last);
                        await using var connection = await host.Fixture.Database.OpenConnectionAsync();
                        await using var transaction = await connection.BeginTransactionAsync();
                        if (commands.JitDisabled)
                        {
                            await using var configure = new NpgsqlCommand("SET LOCAL jit = off", connection, transaction);
                            await configure.ExecuteNonQueryAsync();
                        }
                        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + commands.Last["CommandText"], connection, transaction);
                        if (selection == "scheduler")
                        {
                            explain.Parameters.AddWithValue(now);
                            explain.Parameters.AddWithValue(5);
                        }
                        else explain.Parameters.AddWithValue(301L);
                        var plan = (string)(await explain.ExecuteScalarAsync())!;
                        var planPath = Path.Combine(TestContext.TestResultsDirectory!, $"wanted-{selection}-plan.json");
                        await File.WriteAllTextAsync(planPath, plan);
                        TestContext.AddResultFile(planPath);
                    }
                }
                dueDurations.Sort();
                var dueP95 = dueDurations[(int)Math.Ceiling(dueDurations.Count * .95) - 1];
                TestContext.WriteLine($"{selection}: transaction median {dueDurations[dueDurations.Count / 2]:F1} ms, p95 {dueP95:F1} ms ({samples} samples)");
                if (dueP95 >= budget) failures.Add($"{selection}: {dueP95:F1} ms");
            }
            Assert.IsEmpty(failures, "Read p95 exceeded the configured budget: " + string.Join("; ", failures));
        }

        private sealed class CommandLog : ILoggerProvider, ILogger
        {
            public Dictionary<string, object?>? Last { get; set; }
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
                    && values.Any(pair => pair.Key == "CommandText" && pair.Value is string sql && sql.Contains("\"WantedItems\" AS wanted", StringComparison.Ordinal)))
                {
                    Last = values.ToDictionary(pair => pair.Key, pair => pair.Value);
                }
            }
        }
    }
}
