using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.System.Media.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    // The slow reads at catalog scale, cold and warm, with the plans PostgreSQL chose for them. Opt-in on the isolated test server: the seed takes minutes and
    // some hundred megabytes. JULARR_TEST_CATALOG_SCALE sets the catalog titles (default 250000; the live instance has about 1400000), JULARR_TEST_CATALOG_LIBRARY
    // the library titles (default 5000), JULARR_TEST_CATALOG_MAX_MS a warm median that fails the run and JULARR_TEST_CATALOG_READS the names of the reads to measure
    // (comma-separated, all by default).
    [TestClass]
    public sealed partial class CatalogScalePerformanceTests
    {
        public TestContext TestContext { get; set; } = null!;

        [TestMethod]
        [TestCategory("Performance")]
        public async Task Catalog_SyntheticScale_ReportsColdAndWarmReadsWithPlans()
        {
            if (Environment.GetEnvironmentVariable("JULARR_TEST_CATALOG_PERFORMANCE") != "1")
            {
                Assert.Inconclusive("Opt in with JULARR_TEST_CATALOG_PERFORMANCE=1 on the isolated test server.");
            }

            var scale = Setting("JULARR_TEST_CATALOG_SCALE", 250_000);
            var libraryTitles = Setting("JULARR_TEST_CATALOG_LIBRARY", 5_000);
            var samples = Setting("JULARR_TEST_CATALOG_SAMPLES", 10);
            var budget = double.TryParse(Environment.GetEnvironmentVariable("JULARR_TEST_CATALOG_MAX_MS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var maximum) ? maximum : double.PositiveInfinity;

            // Plans arrive as notices of the session's auto_explain; the data source is the host's own, so a cleared pool starts new sessions.
            var plans = new PlanNotices();
            using var logs = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(plans));
            NpgsqlDataSource? dataSource = null;
            using var storage = new LibraryTests.TemporaryDirectory();

            // The console logger of an information-level host costs more per request than many reads; production logs warnings.
            var quiet = new Dictionary<string, string> { ["Logging:LogLevel:Default"] = "Warning" };
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, settings: quiet, services: services => services.Replace(ServiceDescriptor.Singleton(provider =>
            {
                var connection = new NpgsqlConnectionStringBuilder(provider.GetRequiredService<IConfiguration>()["JULARR_DB"]!) { Pooling = true };
                dataSource = new NpgsqlDataSourceBuilder(connection.ConnectionString).UseLoggerFactory(logs).Build();
                return new SqlDatabase(dataSource);
            })));
            var seeding = Stopwatch.GetTimestamp();
            await SeedAsync(host, scale, libraryTitles);
            TestContext.WriteLine($"Seed: {scale} catalog titles, {libraryTitles} library titles in {Stopwatch.GetElapsedTime(seeding).TotalSeconds:F0} s.");

            // The catalog and system pages read the worker's count, as after its first catalog pass.
            var counting = Stopwatch.GetTimestamp();
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsTrue((await scope.ServiceProvider.GetRequiredService<CountCatalogTitlesV1>().ExecuteAsync<UpdateResult>(new SyncExternalWorksParametersV1(), default)).Success);
            }
            var countMilliseconds = Stopwatch.GetElapsedTime(counting).TotalMilliseconds;
            TestContext.WriteLine($"Catalog count (CountCatalogTitlesV1): {countMilliseconds:F0} ms.");

            Access[] accesses =
            [
                new("catalog-syncs", HttpMethod.Get, "/api/v1/admin/catalog/syncs"),
                new("system-health", HttpMethod.Get, "/api/v1/admin/system"),
                new("dashboard", HttpMethod.Get, "/api/v1/admin/system/dashboard"),
                new("statistics", HttpMethod.Get, "/api/v1/statistics?days=30"),
                new("works-list", HttpMethod.Get, "/api/v1/admin/works?pageSize=25"),
                new("works-summary", HttpMethod.Get, "/api/v1/admin/works/summary"),
                new("wanted-list", HttpMethod.Get, "/api/v1/admin/wanted?pageSize=48"),
                new("wanted-summary", HttpMethod.Get, "/api/v1/admin/wanted/summary"),
                new("wanted-page", HttpMethod.Get, "/api/v1/admin/wanted/page?pageSize=48&wantedViewTypeId=1"),
                new("rename-plan", HttpMethod.Get, "/api/v1/admin/library/renames?pageSize=100"),
                new("rename-plan-work", HttpMethod.Get, "/api/v1/admin/library/renames?pageSize=100&workId=1"),
                new("translation-progress", HttpMethod.Get, "/api/v1/admin/translation/progress"),
                new("discover-page-1", HttpMethod.Post, "/api/v1/discover/filtered", new ReadDiscoverFilteredDataV1("movies", 1)),
                new("discover-page-400", HttpMethod.Post, "/api/v1/discover/filtered", new ReadDiscoverFilteredDataV1("movies", 400)),
                new("anime-popular-page-1", HttpMethod.Post, "/api/v1/discover/catalog/feed", new ReadDiscoverCatalogFeedDataV1(MediaType.Series, DiscoverFeedType.Popular, MediaSubtype.Anime)),
                new("search-latin", HttpMethod.Post, "/api/v1/discover/suggestions", new SuggestDiscoverDataV1("Sword Art")),
                new("search-romaji", HttpMethod.Post, "/api/v1/discover/suggestions", new SuggestDiscoverDataV1("Shingeki Kyojin")),
                new("search-japanese", HttpMethod.Post, "/api/v1/discover/suggestions", new SuggestDiscoverDataV1("進撃の巨人")),
                new("page-dashboard", HttpMethod.Get, "/Admin/Dashboard"),
                new("page-system", HttpMethod.Get, "/Admin/System"),
                new("page-performance", HttpMethod.Get, "/Admin/Performance"),
                new("page-statistics", HttpMethod.Get, "/Statistics?days=30"),
                new("page-catalog", HttpMethod.Get, "/Admin/Catalog"),
                new("page-works", HttpMethod.Get, "/Admin/Works"),
                new("page-wanted", HttpMethod.Get, "/Admin/Wanted"),
                new("page-rename", HttpMethod.Get, "/Admin/Library/Rename"),
                new("page-work", HttpMethod.Get, "/Admin/Works/1"),
                new("page-work-files", HttpMethod.Get, "/Admin/Works/1?Tab=Files"),
                new("page-discover-anime", HttpMethod.Get, "/?type=anime&view=Popular")
            ];
            if (Environment.GetEnvironmentVariable("JULARR_TEST_CATALOG_READS")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } only)
            {
                accesses = [.. accesses.Where(access => only.Contains(access.Name, StringComparer.Ordinal))];
            }

            // Shared code paths are compiled before the first measured read.
            using (var warmup = await host.SendAsync(HttpMethod.Get, "/api/v1/auth")) Assert.AreEqual(HttpStatusCode.OK, warmup.StatusCode);

            // Cold: the first call of each read in this process, on a fresh connection (PostgreSQL's buffers stay as the seed left them). Warm: the following calls.
            // A page also names its shell and data parts (Server-Timing); the rest of its time is rendering.
            var results = new List<(Access Access, double Cold, double Median, double P95, double? Shell, double? Data)>();
            foreach (var access in accesses)
            {
                dataSource!.Clear();
                var cold = (await TimeAsync(host, access)).Milliseconds;
                var warm = new List<Sample>();
                for (var sample = 0; sample < samples; sample++) warm.Add(await TimeAsync(host, access));
                var times = warm.Select(sample => sample.Milliseconds).Order().ToList();
                var shells = warm.Where(sample => sample.Shell is not null).Select(sample => sample.Shell!.Value).Order().ToList();
                var data = warm.Where(sample => sample.Data is not null).Select(sample => sample.Data!.Value).Order().ToList();
                results.Add((access, cold, times[times.Count / 2], times[(int)Math.Ceiling(times.Count * .95) - 1], shells.Count > 0 ? shells[shells.Count / 2] : null,
                    data.Count > 0 ? data[data.Count / 2] : null));
            }

            // One more call of each read with auto_explain in its sessions; the server keeps running, only this database's new sessions load it.
            var database = new NpgsqlConnectionStringBuilder(host.Fixture.ConnectionString).Database;
            await host.Fixture.RunSqlAsync($"""
                ALTER DATABASE "{database}" SET session_preload_libraries = 'auto_explain';
                ALTER DATABASE "{database}" SET auto_explain.log_min_duration = 0;
                ALTER DATABASE "{database}" SET auto_explain.log_analyze = on;
                ALTER DATABASE "{database}" SET auto_explain.log_buffers = on;
                ALTER DATABASE "{database}" SET auto_explain.log_format = 'json';
                ALTER DATABASE "{database}" SET auto_explain.log_level = 'notice';
                """);
            dataSource!.Clear();
            var summaries = new Dictionary<string, (int Statements, double Milliseconds, IReadOnlyList<PlanSummary> Slowest)>(StringComparer.Ordinal);
            plans.IsCapturing = true;
            foreach (var access in accesses)
            {
                plans.Notices.Clear();
                await TimeAsync(host, access);
                var captured = plans.Notices.ToArray();
                var path = Path.Combine(TestContext.TestResultsDirectory!, $"catalog-plans-{access.Name}.json");
                await File.WriteAllTextAsync(path, "[" + string.Join(",", captured.Select(notice => notice.Json)) + "]");
                TestContext.AddResultFile(path);
                summaries[access.Name] = (captured.Length, captured.Sum(notice => notice.Milliseconds),
                    [.. captured.Where(notice => notice.Milliseconds >= 0.5).OrderByDescending(notice => notice.Milliseconds).Take(3).Select(PlanSummary.Of)]);
            }

            plans.IsCapturing = false;
            await host.Fixture.RunSqlAsync($"ALTER DATABASE \"{database}\" RESET ALL;");
            dataSource!.Clear();

            // The report: every read cold and warm, then its statements and the three slowest of them (from half a millisecond) with their plans in short.
            var report = new StringBuilder();
            report.AppendLine(CultureInfo.InvariantCulture, $"Catalog scale {scale}, library {libraryTitles}, {samples} warm samples; milliseconds in the test server (in-process HTTP).");
            report.AppendLine(CultureInfo.InvariantCulture, $"The worker's catalog count (CountCatalogTitlesV1) took {countMilliseconds:F0} ms.");
            report.AppendLine("read                      cold   warm median   warm p95   shell    data");
            foreach (var (access, cold, median, p95, shell, data) in results)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"{access.Name,-22} {cold,8:F1} {median,12:F1} {p95,10:F1} {shell,7:F1} {data,7:F1}");
            }

            foreach (var (access, _, median, _, _, _) in results)
            {
                var (statements, milliseconds, slowest) = summaries[access.Name];
                report.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"{access.Name} (warm median {median:F1} ms): {statements} statements, {milliseconds:F1} ms in PostgreSQL with auto_explain");
                foreach (var summary in slowest) report.AppendLine("  " + summary);
            }

            TestContext.WriteLine(report.ToString());
            var reportPath = Path.Combine(TestContext.TestResultsDirectory!, "catalog-scale-report.txt");
            await File.WriteAllTextAsync(reportPath, report.ToString());
            TestContext.AddResultFile(reportPath);
            var over = results.Where(result => result.Median > budget).Select(result => $"{result.Access.Name}: {result.Median:F1} ms").ToList();
            Assert.IsEmpty(over, "Warm median above JULARR_TEST_CATALOG_MAX_MS: " + string.Join("; ", over));
        }

        private static int Setting(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

        private static async Task<Sample> TimeAsync(Host host, Access access)
        {
            var started = Stopwatch.GetTimestamp();
            using var response = await host.SendAsync(access.Method, access.Path, access.Body);
            var body = await response.Content.ReadAsStringAsync();
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, access.Name + ": " + body[..Math.Min(body.Length, 2000)]);
            var timing = response.Headers.TryGetValues("Server-Timing", out var values) ? string.Join(", ", values) : "";
            double? Part(string name) => Regex.Match(timing, name + @";dur=([0-9.]+)") is { Success: true } match ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
            return new Sample(elapsed, Part("shell"), Part("data"));
        }

        // Catalog titles in the proportions of the live instance (films, TheTVDB series, AniList anime and manga, Open Library books), a library of films, series with
        // episodes and Japanese books with files and wanted entries, and half of every book's sections translated.
        private static async Task SeedAsync(Host host, int scale, int libraryTitles)
        {
            var mount = await LibraryTests.CreateMountAsync(host);
            var movies = await LibraryTests.CreateRootAsync(host, mount, "Movies", MediaType.Movie);
            var series = await LibraryTests.CreateRootAsync(host, mount, "Series", MediaType.Series);
            var books = await LibraryTests.CreateRootAsync(host, mount, "Books", MediaType.Book);
            // One statement batch; at a million titles it runs for minutes, longer than the default command timeout.
            await using var connection = await host.Fixture.Database.OpenConnectionAsync();
            await using var seed = new NpgsqlCommand($$"""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" IN ('DiscoverCache', 'LocalDiscovery', 'Translation', 'Diagnostics', 'Statistics');
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now(), "ListedOn" = current_date;

                INSERT INTO "Works" ("Id", "CatalogNamespace", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "OriginalTitle", "OriginalLanguage", "ReleaseYear", "ReleasedOn", "ImageUrl",
                    "Popularity", "Rating", "Votes", "TrendingRank", "FetchedAt")
                SELECT
                    1000000 + n,
                    kind.namespace,
                    kind.type,
                    kind.subtype,
                    words[1 + n % 24] || ' ' || words[1 + (n / 24) % 24] || ' ' || words[1 + (n / 576) % 24] || ' ' || n,
                    CASE WHEN kind.namespace = 'anilist' THEN japanese[1 + n % 10] || ' ' || n END,
                    CASE WHEN kind.namespace = 'anilist' THEN 'ja' ELSE 'en' END,
                    1950 + n % 76,
                    make_date(1950 + n % 76, 1 + n % 12, 1 + n % 28),
                    'https://image.test/' || n || '.jpg',
                    (n::bigint * 7919 % 100000) / 100.0,
                    (n % 100) / 10.0,
                    n % 5000,
                    CASE WHEN n % 997 = 0 THEN n / 997 END,
                    CASE WHEN n % 10 = 0 THEN NULL ELSE now() END
                FROM
                    generate_series(1, {{scale}}) AS n
                    CROSS JOIN LATERAL (
                        SELECT
                            ARRAY['Shingeki', 'no', 'Kyojin', 'Kimetsu', 'Yaiba', 'Boku', 'Hero', 'Academia', 'Sword', 'Art', 'Online', 'Tokyo', 'Ghoul', 'Steins', 'Gate', 'Death', 'Note',
                                'Fullmetal', 'Alchemist', 'Spy', 'Family', 'Jujutsu', 'Kaisen', 'Chainsaw'] AS words,
                            ARRAY['進撃の巨人', '鬼滅の刃', '僕のヒーローアカデミア', 'ソードアート・オンライン', '東京喰種', 'シュタインズ・ゲート', 'デスノート', '鋼の錬金術師', '呪術廻戦', 'チェンソーマン'] AS japanese
                    ) AS vocabulary
                    CROSS JOIN LATERAL (
                        SELECT
                            CASE WHEN n % 20 < 12 THEN 'tmdb_movie' WHEN n % 20 < 16 THEN 'tvdb' WHEN n % 20 < 19 THEN 'anilist' ELSE 'openlibrary' END AS namespace,
                            CASE WHEN n % 20 < 12 THEN 1 WHEN n % 20 < 17 THEN 2 ELSE 3 END::smallint AS type,
                            CASE n % 20 WHEN 16 THEN 1 WHEN 17 THEN 2 WHEN 18 THEN 2 END::smallint AS subtype
                    ) AS kind;
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                SELECT "Id", "CatalogNamespace", ("Id" - 1000000)::text FROM "Works" WHERE "Id" > 1000000;

                INSERT INTO "Works" ("Id", "CatalogNamespace", "MediaTypeId", "CanonicalTitle", "OriginalLanguage", "ReleaseYear", "FetchedAt")
                SELECT
                    n,
                    CASE WHEN n % 20 < 12 THEN 'tmdb_movie' WHEN n % 20 < 17 THEN 'tvdb' ELSE 'openlibrary' END,
                    CASE WHEN n % 20 < 12 THEN 1 WHEN n % 20 < 17 THEN 2 ELSE 3 END,
                    'Library ' || n,
                    CASE WHEN n % 20 < 17 THEN 'en' ELSE 'ja' END,
                    1990 + n % 36,
                    now()
                FROM
                    generate_series(1, {{libraryTitles}}) AS n;
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                SELECT "Id", "CatalogNamespace", ({{scale}} + "Id")::text FROM "Works" WHERE "Id" <= {{libraryTitles}};
                INSERT INTO "WorkEditions" ("WorkId") SELECT "Id" FROM "Works" WHERE "Id" <= {{libraryTitles}};
                INSERT INTO "WorkVersions" ("WorkId", "WorkEditionId") SELECT "WorkId", "Id" FROM "WorkEditions";
                INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number") SELECT "Id", 1, 1 FROM "Works" WHERE "Id" <= {{libraryTitles}} AND "MediaTypeId" = 2;
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "ReleasedOn")
                SELECT season."WorkId", season."Id", 2, number, current_date - 1 FROM "WorkUnits" AS season CROSS JOIN generate_series(1, 10) AS number;
                INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "WorkUnitId", "MediaAssetTypeId")
                SELECT version."WorkId", version."Id", unit."Id", CASE WHEN work."MediaTypeId" = 3 THEN 3 ELSE 1 END
                FROM
                    "WorkVersions" AS version
                    JOIN "Works" AS work ON work."Id" = version."WorkId"
                    LEFT JOIN "WorkUnits" AS unit ON unit."WorkId" = version."WorkId" AND unit."WorkUnitTypeId" = 2;
                INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId", "MissingSince", "QualityKey")
                SELECT
                    CASE work."MediaTypeId" WHEN 1 THEN {{movies}} WHEN 2 THEN {{series}} ELSE {{books}} END,
                    work."CanonicalTitle" || '/' || asset."Id" || CASE WHEN work."MediaTypeId" = 3 THEN '.epub' ELSE '.mkv' END,
                    CASE WHEN work."MediaTypeId" = 3 THEN 2000000 ELSE 2000000000 END,
                    now(),
                    now(),
                    asset."Id",
                    CASE WHEN asset."Id" % 10 = 0 THEN now() END,
                    CASE WHEN work."MediaTypeId" = 3 THEN NULL WHEN asset."Id" % 2 = 0 THEN 'HDTV-720p' ELSE 'WEBDL-1080p' END
                FROM
                    "MediaAssets" AS asset
                    JOIN "Works" AS work ON work."Id" = asset."WorkId";
                INSERT INTO "WantedItems" ("WorkId", "IsPaused", "WantedPriorityTypeId")
                SELECT "Id", "Id" % 7 = 0, CASE WHEN "Id" % 5 = 0 THEN 2 ELSE 1 END FROM "Works" WHERE "Id" <= {{libraryTitles}};

                INSERT INTO "ReadingDocuments" ("StoredFileId", "SourceSizeBytes", "SourceModifiedAt", "ContentHash", "AnalyzerVersion", "ReadingFormatTypeId", "Title", "Language", "Structure", "TotalCharacters")
                SELECT
                    file."Id",
                    file."SizeBytes",
                    now(),
                    sha256(convert_to(file."Id"::text, 'UTF8')),
                    1,
                    1,
                    work."CanonicalTitle",
                    'ja',
                    jsonb_build_object('Sections', (SELECT jsonb_agg(jsonb_build_object('Blocks', 20 + section % 30)) FROM generate_series(1, 40) AS section)),
                    100000
                FROM
                    "StoredFiles" AS file
                    JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                    JOIN "Works" AS work ON work."Id" = asset."WorkId"
                WHERE
                    work."MediaTypeId" = 3
                    AND file."MissingSince" IS NULL;
                INSERT INTO "ReadingResources" ("ReadingDocumentId", "Path", "MediaType", "Bytes")
                SELECT
                    document."Id",
                    ':localtranslation/v4/model/en/' || lpad(section::text, 5, '0'),
                    'application/json',
                    convert_to(jsonb_build_object('Texts', (SELECT jsonb_agg('Text ' || block) FROM generate_series(1, 25) AS block), 'ModelDigest', 'digest')::text, 'UTF8')
                FROM
                    "ReadingDocuments" AS document
                    CROSS JOIN generate_series(0, 19) AS section;
                ANALYZE;
                """, connection) { CommandTimeout = 0 };
            await seed.ExecuteNonQueryAsync();
        }

        private sealed record Access(string Name, HttpMethod Method, string Path, object? Body = null);

        private sealed record Sample(double Milliseconds, double? Shell, double? Data);

        private sealed record PlanNotice(double Milliseconds, string Json);

        // A statement in short: its time, the first words of its text, the plan's top node, the blocks it touched and the larger sequential scans in it.
        private sealed record PlanSummary(double Milliseconds, string Query, string Top, long HitBlocks, long ReadBlocks, IReadOnlyList<string> SequentialScans)
        {
            public static PlanSummary Of(PlanNotice notice)
            {
                using var document = JsonDocument.Parse(notice.Json);
                var root = document.RootElement;
                var plan = root.GetProperty("Plan");
                var query = Regex.Replace(root.TryGetProperty("Query Text", out var text) ? text.GetString() ?? "" : "", @"\s+", " ").Trim();
                var scans = new List<string>();
                Walk(plan, scans);
                return new PlanSummary(notice.Milliseconds, query[..Math.Min(query.Length, 90)], plan.GetProperty("Node Type").GetString() ?? "",
                    plan.TryGetProperty("Shared Hit Blocks", out var hit) ? hit.GetInt64() : 0, plan.TryGetProperty("Shared Read Blocks", out var read) ? read.GetInt64() : 0, scans);
            }

            // Sequential scans that read or threw away at least a thousand rows.
            private static void Walk(JsonElement node, List<string> scans)
            {
                if (node.GetProperty("Node Type").GetString() == "Seq Scan")
                {
                    var rows = node.GetProperty("Actual Rows").GetDouble() * node.GetProperty("Actual Loops").GetDouble();
                    var removed = node.TryGetProperty("Rows Removed by Filter", out var filtered) ? filtered.GetDouble() : 0;
                    if (rows + removed >= 1000) scans.Add($"{node.GetProperty("Relation Name").GetString()} ({rows + removed:F0} rows)");
                }

                if (node.TryGetProperty("Plans", out var children))
                {
                    foreach (var child in children.EnumerateArray()) Walk(child, scans);
                }
            }

            public override string ToString() => string.Create(CultureInfo.InvariantCulture,
                $"{Milliseconds,8:F1} ms  {Top}, blocks hit {HitBlocks} read {ReadBlocks}{(SequentialScans.Count > 0 ? ", seq scan " + string.Join(", ", SequentialScans) : "")}  | {Query}");
        }

        // Npgsql logs each notice it receives; auto_explain sends a statement's plan as one ("duration: 1.2 ms  plan:" and the plan in JSON).
        private sealed partial class PlanNotices : ILoggerProvider, ILogger
        {
            public ConcurrentQueue<PlanNotice> Notices { get; } = new();
            public volatile bool IsCapturing;
            public ILogger CreateLogger(string categoryName) => this;
            public void Dispose() { }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => IsCapturing;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsCapturing || state is not IReadOnlyList<KeyValuePair<string, object?>> values) return;
                if (values.FirstOrDefault(pair => pair.Key == "NoticeText").Value is not string text || Plan().Match(text) is not { Success: true } match) return;
                Notices.Enqueue(new PlanNotice(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Value));
            }

            [GeneratedRegex(@"^duration: ([0-9.]+) ms\s+plan:\s*(\{.*\})\s*$", RegexOptions.Singleline)]
            private static partial Regex Plan();
        }
    }
}
