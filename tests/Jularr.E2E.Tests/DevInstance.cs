using System.Diagnostics;
using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Jularr.E2E.Tests
{
    // A local Jularr to click through, with a catalog and a library in the live instance's proportions: films, series with seasons, anime, books, manga, light novels,
    // music and games, covers and backdrops of their own, progress, a watchlist and history. With JULARR_TEST_FFMPEG a film, three episodes and three tracks are real
    // files that play. Not a test: scripts/dev-instance.sh starts it (JULARR_DEV_INSTANCE=1) and it runs until that script stops it.
    [TestClass]
    public sealed class DevInstance
    {
        private static readonly string Home = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath(), ".cache", "jularr-dev");

        [TestMethod]
        [TestCategory("DevInstance")]
        [Timeout(int.MaxValue)]
        public async Task Run()
        {
            if (Environment.GetEnvironmentVariable("JULARR_DEV_INSTANCE") != "1")
            {
                Assert.Inconclusive("The local instance to click through runs only through scripts/dev-instance.sh.");
            }

            Directory.CreateDirectory(Home);
            var stop = Path.Combine(Home, "stop");
            File.Delete(stop);
            await using var covers = await CoverServer.StartAsync();
            // Open Library answers from the browser tests' fake, so the book lists of Discover are not empty and no provider on the internet is asked.
            var downloads = Directory.CreateDirectory(Path.Combine(Home, "fake-downloads")).FullName;
            await using var fakes = await FakeServices.StartAsync(downloads);
            var ffmpeg = Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG") is { Length: > 0 } path && File.Exists(path) ? path : null;
            // The server's warnings and errors go to host.log as they happen.
            var log = Path.Combine(Home, "host.log");
            File.Delete(log);
            Evidence.Mirror = log;
            var settings = new Dictionary<string, string>
            {
                // Every catalog address names the TMDB image host; the cover server answers for it.
                ["Providers:Images:image.tmdb.org"] = covers.Address.ToString(),
                ["Logging:LogLevel:Default"] = "Warning",
                ["Providers:OpenLibrary"] = fakes.OpenLibrary.ToString()
            };
            if (ffmpeg is not null)
            {
                settings["Media:FfmpegPath"] = ffmpeg;
                settings["Media:FfprobePath"] = Steps.FfprobeBeside(ffmpeg)!;
            }

            await using var jularr = await JularrInstance.StartAsync(settings);
            await using (var browser = await BrowserSession.StartAsync(jularr))
            {
                await Owner.SetUpAndSignInAsync(browser.Page);
                await SeedAsync(jularr);
                var real = await WriteFilesAsync(jularr, ffmpeg);
                if (real.Count > 0)
                {
                    // The roots with real files are scanned once, so the probe reads their tracks and length as for any import.
                    foreach (var root in real)
                    {
                        await browser.Page.EvaluateAsync("""
                            async root => {
                                const token = (await fetch('/api/v1/auth')).headers.get('RequestVerificationToken');
                                await fetch('/api/v1/admin/library/roots/' + root + '/scans', { method: 'POST', headers: { RequestVerificationToken: token } });
                            }
                            """, root);
                    }
                }
            }

            var info = $"""
                Jularr läuft: {jularr.Address}
                Anmeldung: {Owner.Username} / {Owner.Password}
                Echte Dateien: {(ffmpeg is null ? "keine (FFmpeg fehlt: eval \"$(scripts/local-ffmpeg.sh)\")" : "ein Film, drei Folgen, drei Lieder")}
                Server-Log (Warnungen, Fehler): {log}
                Beenden: Strg+C im Fenster von scripts/dev-instance.sh
                """;
            await File.WriteAllTextAsync(Path.Combine(Home, "instance.txt"), info);
            Console.WriteLine(info);
            Evidence.Log("[dev] " + info.Replace('\n', ' '));

            var hours = double.TryParse(Environment.GetEnvironmentVariable("JULARR_DEV_HOURS"), System.Globalization.CultureInfo.InvariantCulture, out var configured) ? configured : 12;
            var until = DateTime.UtcNow.AddHours(hours);
            while (!File.Exists(stop) && DateTime.UtcNow < until)
            {
                await Task.Delay(1000);
            }

            File.Delete(stop);
            File.Delete(Path.Combine(Home, "instance.txt"));
        }

        private static async Task SeedAsync(JularrInstance jularr)
        {
            await jularr.RunSqlAsync(Catalog);
            // A local storage is the folder itself.
            await jularr.RunSqlAsync(Library.Replace("{library}", Path.Combine(jularr.Storage, "dev-library").Replace("'", "''"), StringComparison.Ordinal));
            await jularr.RunSqlAsync(Activity);
        }

        // Every library file as a sparse file of its size and date, so a scan finds them unchanged; with FFmpeg the first film, episodes and tracks are real.
        private static async Task<List<long>> WriteFilesAsync(JularrInstance jularr, string? ffmpeg)
        {
            var library = Path.Combine(jularr.Storage, "dev-library");
            var date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var rows = new List<(long Id, long Root, string Path, long Size)>();
            await using (var connection = new NpgsqlConnection(jularr.ConnectionString))
            {
                await connection.OpenAsync();
                await using var read = new NpgsqlCommand("""
                    SELECT file."Id", root."Id", root."RelativePath" || '/' || file."RelativePath", file."SizeBytes"
                    FROM "StoredFiles" AS file JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    """, connection);
                await using var reader = await read.ExecuteReaderAsync();
                while (await reader.ReadAsync()) rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3)));
            }

            foreach (var row in rows)
            {
                var file = Path.Combine(library, row.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await using (var stream = File.Create(file))
                {
                    stream.SetLength(row.Size);
                }

                File.SetLastWriteTimeUtc(file, date);
            }

            if (ffmpeg is null) return [];

            // The real ones: a two-minute film and three episodes with German and English audio and German subtitles, three tracks of an album.
            var real = rows.Where(row => row.Path.Contains("/dev-real-", StringComparison.Ordinal)).ToList();
            foreach (var row in real)
            {
                var file = Path.Combine(library, row.Path);
                File.Delete(file);
                string[] arguments = row.Path.EndsWith(".flac", StringComparison.Ordinal)
                    ? ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=330:duration=90", "-metadata", "title=Dev track", "-y", file]
                    : ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=24:duration=120", "-f", "lavfi", "-i", "sine=frequency=440:duration=120",
                        "-f", "lavfi", "-i", "sine=frequency=550:duration=120", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-preset", "veryfast",
                        "-pix_fmt", "yuv420p", "-c:a", "aac", "-metadata:s:a:0", "language=ger", "-metadata:s:a:1", "language=eng", "-y", file];
                using var process = Process.Start(new ProcessStartInfo(ffmpeg, arguments) { RedirectStandardError = true })!;
                await process.WaitForExitAsync();
                Assert.AreEqual(0, process.ExitCode, await process.StandardError.ReadToEndAsync());
            }

            // The probe reads them on the scan: no stored facts, their real size.
            await using (var connection = new NpgsqlConnection(jularr.ConnectionString))
            {
                await connection.OpenAsync();
                foreach (var row in real)
                {
                    await using (var clear = new NpgsqlCommand("""DELETE FROM "MediaTracks" WHERE "StoredFileId" = $1""", connection))
                    {
                        clear.Parameters.AddWithValue(row.Id);
                        await clear.ExecuteNonQueryAsync();
                    }

                    await using var update = new NpgsqlCommand("""
                        UPDATE "StoredFiles" SET "ProbedAt" = NULL, "Container" = NULL, "DurationMs" = NULL, "SizeBytes" = $2, "ModifiedAt" = $3 WHERE "Id" = $1
                        """, connection);
                    var file = new FileInfo(Path.Combine(library, row.Path));
                    update.Parameters.AddWithValue(row.Id);
                    update.Parameters.AddWithValue(file.Length);
                    update.Parameters.AddWithValue(file.LastWriteTimeUtc);
                    await update.ExecuteNonQueryAsync();
                }
            }

            return [.. real.Select(row => row.Root).Distinct()];
        }

        // The catalog of every type in the live proportions, with titles from two word lists, years, genres, ratings and the TMDB image host for covers.
        private const string Catalog = """
            UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" IN ('LocalDiscovery', 'DiscoverCache');
            UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now(), "ListedOn" = current_date;

            INSERT INTO "Works" ("Id", "CatalogNamespace", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear", "ReleasedOn", "OriginalLanguage", "Overview",
                "Creator", "ImageUrl", "BackdropUrl", "Popularity", "Rating", "Votes", "TrendingRank", "GenreIds", "CatalogFormatId", "Season", "SeasonYear")
            SELECT
                kind.base + i,
                kind.ns,
                kind.media_type,
                kind.subtype,
                initcap(words.a[1 + (i * 7) % 30] || ' ' || words.n[1 + (i * 11 + i / 30) % 30]) || CASE WHEN i > 300 THEN ' ' || (i / 30)::text ELSE '' END,
                (1975 + (i * 13) % 51)::smallint,
                make_date(1975 + (i * 13) % 51, 1 + i % 12, 1 + i % 28),
                kind.lang,
                'A story about ' || words.n[1 + i % 30] || ' and ' || words.n[1 + (i * 3) % 30] || ', told across ' || (2 + i % 5) || ' parts.',
                CASE WHEN kind.media_type IN (3, 6) THEN initcap(words.a[1 + (i * 5) % 30]) || ' ' || initcap(words.n[1 + (i * 17) % 30]) END,
                'https://image.tmdb.org/t/p/w500/poster-' || (kind.base + i) || '.png',
                'https://image.tmdb.org/t/p/w1280/backdrop-' || (kind.base + i) || '.png',
                (1000 - i)::real,
                round((5.2 + ((i * 37) % 40) / 10.0)::numeric, 1),
                50 + (i * 97) % 5000,
                i,
                ARRAY[genres.g[1 + i % 10], genres.g[1 + (i * 3 + 1) % 10]]::smallint[],
                CASE WHEN kind.subtype = 1 THEN 1 END,
                CASE WHEN kind.subtype = 1 THEN (1 + i % 4)::smallint END,
                CASE WHEN kind.subtype = 1 THEN (2010 + i % 16)::smallint END
            FROM
                (VALUES (100000, 'tmdb_movie', 1, NULL::smallint, 400, 'en'), (200000, 'tmdb_tv', 2, NULL, 150, 'en'), (300000, 'anilist', 2, 1, 160, 'ja'),
                        (400000, 'openlibrary', 3, NULL, 250, 'en'), (500000, 'anilist', 3, 2, 70, 'ja'), (600000, 'anilist', 3, 3, 45, 'ja'),
                        (700000, 'musicbrainz', 6, NULL, 90, 'en'), (800000, 'igdb', 7, NULL, 60, 'en'))
                    AS kind (base, ns, media_type, subtype, total, lang)
                CROSS JOIN LATERAL generate_series(1, kind.total) AS i
                CROSS JOIN (SELECT
                    ARRAY['silent', 'golden', 'broken', 'hidden', 'last', 'crimson', 'frozen', 'wild', 'lost', 'bright', 'dark', 'endless', 'iron', 'paper', 'neon',
                          'quiet', 'burning', 'hollow', 'northern', 'electric', 'velvet', 'stolen', 'distant', 'secret', 'glass', 'midnight', 'savage', 'gentle',
                          'restless', 'emerald'] AS a,
                    ARRAY['river', 'empire', 'garden', 'signal', 'harbor', 'kingdom', 'station', 'orchard', 'mirror', 'frontier', 'summer', 'forest', 'machine',
                          'island', 'crown', 'canyon', 'letter', 'tide', 'lantern', 'city', 'storm', 'valley', 'engine', 'winter', 'voyage', 'echo', 'temple', 'road',
                          'planet', 'dream'] AS n) AS words
                CROSS JOIN (SELECT ARRAY[1, 3, 4, 6, 8, 10, 12, 14, 15, 19] AS g) AS genres;

            -- Identifiers in each catalog's own form: Open Library's work keys, MusicBrainz's UUIDs, numbers for the others.
            INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
            SELECT "Id", "CatalogNamespace", CASE "CatalogNamespace" WHEN 'openlibrary' THEN 'OL' || "Id" || 'W' WHEN 'musicbrainz' THEN md5("Id"::text)::uuid::text ELSE "Id"::text END
            FROM "Works" WHERE "Id" >= 100000;
            """;

        // The library: films, series with seasons and episodes, anime, books, manga and light novels in volumes, albums with tracks; files with tracks of their languages.
        private const string Library = """
            -- Every video file has a picture, German (Japanese for anime) and English audio and German subtitles.
            CREATE FUNCTION pg_temp.tracks(file_id bigint, work_id bigint) RETURNS void LANGUAGE sql AS $f$
                INSERT INTO "MediaTracks" ("StoredFileId", "StreamIndex", "MediaTrackTypeId", "Codec", "Language", "Width", "Height", "Channels", "IsDefault", "IsForced")
                VALUES (file_id, 0, 1, 'h264', NULL, 1920, 1080, NULL, TRUE, FALSE),
                    (file_id, 1, 2, 'eac3', CASE WHEN work_id BETWEEN 300000 AND 399999 THEN 'jpn' ELSE 'ger' END, NULL, NULL, 6, TRUE, FALSE),
                    (file_id, 2, 2, 'aac', 'eng', NULL, NULL, 2, FALSE, FALSE),
                    (file_id, 3, 3, 'subrip', 'ger', NULL, NULL, NULL, FALSE, FALSE);
            $f$;

            DO $$
            DECLARE mount bigint; root_id bigint; work record; version_id bigint; season_id bigint; unit_id bigint; asset_id bigint; file_id bigint;
                seasons int; episodes int; folder text;
            BEGIN
                INSERT INTO "StorageMounts" ("Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly") VALUES ('Dev library', 3, '{library}', FALSE) RETURNING "Id" INTO mount;
                INSERT INTO "LibraryRoots" ("Name", "MediaTypeId", "MediaSubtypeId", "StorageMountId", "RelativePath") VALUES
                    ('Filme', 1, NULL, mount, 'movies'), ('Serien', 2, NULL, mount, 'tv'), ('Anime', 2, 1, mount, 'anime'), ('Bücher', 3, NULL, mount, 'books'),
                    ('Manga', 3, 2, mount, 'manga'), ('Light Novels', 3, 3, mount, 'lightnovels'), ('Musik', 6, NULL, mount, 'music');

                FOR work IN SELECT * FROM "Works" WHERE "Id" BETWEEN 100001 AND 100090 OR "Id" BETWEEN 200001 AND 200014 OR "Id" BETWEEN 300001 AND 300036
                    OR "Id" BETWEEN 400001 AND 400030 OR "Id" BETWEEN 500001 AND 500004 OR "Id" BETWEEN 600001 AND 600003 OR "Id" BETWEEN 700001 AND 700008 ORDER BY "Id"
                LOOP
                    INSERT INTO "WorkEditions" ("WorkId") VALUES (work."Id") RETURNING "Id" INTO version_id;
                    INSERT INTO "WorkVersions" ("WorkId", "WorkEditionId") VALUES (work."Id", version_id) RETURNING "Id" INTO version_id;
                    folder := work."CanonicalTitle" || ' (' || work."ReleaseYear" || ')';
                    SELECT "Id" INTO root_id FROM "LibraryRoots" WHERE "MediaTypeId" = work."MediaTypeId" AND "MediaSubtypeId" IS NOT DISTINCT FROM work."MediaSubtypeId";

                    IF work."MediaTypeId" = 1 THEN
                        INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "MediaAssetTypeId") VALUES (work."Id", version_id, 1) RETURNING "Id" INTO asset_id;
                        INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "ProbedAt", "Container", "DurationMs", "MediaAssetId")
                        VALUES (root_id, folder || '/' || CASE WHEN work."Id" = 100001 THEN 'dev-real-' ELSE '' END || folder || '.mkv',
                            1400000000 + (work."Id" % 50) * 130000000, '2026-01-01T00:00:00Z', now(), now(), 'matroska', 5400000 + (work."Id" % 40) * 60000, asset_id)
                        RETURNING "Id" INTO file_id;
                        PERFORM pg_temp.tracks(file_id, work."Id");
                    ELSIF work."MediaTypeId" = 2 THEN
                        seasons := CASE WHEN work."Id" = 200001 THEN 10 WHEN work."MediaSubtypeId" = 1 THEN 1 + work."Id" % 2 ELSE 1 + work."Id" % 5 END;
                        episodes := CASE WHEN work."MediaSubtypeId" = 1 THEN 12 + (work."Id" % 2) * 12 ELSE 8 + work."Id" % 17 END;
                        FOR season IN 1..seasons LOOP
                            INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number", "Title", "ReleasedOn")
                            VALUES (work."Id", 1, season, 'Staffel ' || season, make_date((2000 + work."Id" % 20 + season)::int, 3, 1)) RETURNING "Id" INTO season_id;
                            FOR episode IN 1..episodes LOOP
                                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "Title", "ReleasedOn", "RuntimeMinutes")
                                VALUES (work."Id", season_id, 2, episode, initcap((ARRAY['the', 'a', 'no', 'one'])[1 + episode % 4] || ' ' ||
                                    (ARRAY['visit', 'return', 'promise', 'secret', 'storm', 'letter', 'night', 'game'])[1 + (episode * 3 + season) % 8]),
                                    make_date((2000 + work."Id" % 20 + season)::int, 3, 1) + episode * 7, 24 + work."Id" % 30) RETURNING "Id" INTO unit_id;
                                INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "WorkUnitId", "MediaAssetTypeId") VALUES (work."Id", version_id, unit_id, 1) RETURNING "Id" INTO asset_id;
                                INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "ProbedAt", "Container", "DurationMs", "MediaAssetId")
                                VALUES (root_id, work."CanonicalTitle" || '/Season ' || lpad(season::text, 2, '0') || '/' ||
                                    CASE WHEN work."Id" = 200001 AND season = 1 AND episode <= 3 THEN 'dev-real-' ELSE '' END ||
                                    work."CanonicalTitle" || ' - S' || lpad(season::text, 2, '0') || 'E' || lpad(episode::text, 2, '0') || '.mkv',
                                    400000000 + (episode % 9) * 110000000, '2026-01-01T00:00:00Z', now(), now(), 'matroska', 1440000 + (episode % 7) * 60000, asset_id)
                                RETURNING "Id" INTO file_id;
                                PERFORM pg_temp.tracks(file_id, work."Id");
                            END LOOP;
                        END LOOP;
                    ELSIF work."MediaTypeId" = 3 AND work."MediaSubtypeId" IS NULL THEN
                        INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "MediaAssetTypeId") VALUES (work."Id", version_id, 3) RETURNING "Id" INTO asset_id;
                        INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId")
                        VALUES (root_id, work."Creator" || '/' || work."CanonicalTitle" || '.epub', 1500000 + work."Id" % 90 * 40000, '2026-01-01T00:00:00Z', now(), asset_id);
                    ELSE
                        -- Volumes of manga and light novels, tracks of an album.
                        FOR part IN 1..CASE WHEN work."MediaTypeId" = 6 THEN 10 ELSE 3 + work."Id" % 8 END LOOP
                            INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number", "Title")
                            VALUES (work."Id", CASE WHEN work."MediaTypeId" = 6 THEN 5 ELSE 3 END, part,
                                CASE WHEN work."MediaTypeId" = 6 THEN initcap((ARRAY['blue', 'slow', 'far', 'home', 'light'])[1 + part % 5] || ' ' ||
                                    (ARRAY['song', 'waves', 'lights', 'again', 'away'])[1 + (part * 2) % 5]) ELSE 'Band ' || part END)
                            RETURNING "Id" INTO unit_id;
                            INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "WorkUnitId", "MediaAssetTypeId")
                            VALUES (work."Id", version_id, unit_id, CASE WHEN work."MediaTypeId" = 6 THEN 2 WHEN work."MediaSubtypeId" = 2 THEN 4 ELSE 3 END) RETURNING "Id" INTO asset_id;
                            INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "ProbedAt", "Container", "DurationMs", "MediaAssetId")
                            VALUES (root_id, coalesce(work."Creator" || '/', '') || work."CanonicalTitle" || '/' ||
                                CASE WHEN work."Id" = 700001 AND part <= 3 THEN 'dev-real-' ELSE '' END || lpad(part::text, 2, '0') ||
                                CASE WHEN work."MediaTypeId" = 6 THEN '.flac' WHEN work."MediaSubtypeId" = 2 THEN '.cbz' ELSE '.epub' END,
                                CASE WHEN work."MediaTypeId" = 6 THEN 30000000 ELSE 60000000 END, '2026-01-01T00:00:00Z', now(),
                                CASE WHEN work."MediaTypeId" = 6 THEN now() END, CASE WHEN work."MediaTypeId" = 6 THEN 'flac' END,
                                CASE WHEN work."MediaTypeId" = 6 THEN 180000 + part * 7000 END, asset_id);
                        END LOOP;
                    END IF;
                END LOOP;
            END $$;
            """;

        // Who watched what: films finished, a series and an anime in progress, a watchlist; the taste rows and Continue read from it.
        private const string Activity = """
            INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "PositionMs", "DurationMs", "IsCompleted", "CompletedAt", "UpdatedAt")
            SELECT profile."Id", work_id, NULL, 1, 6000000, 6000000, TRUE, now() - (work_id % 30) * interval '1 day', now() - (work_id % 30) * interval '1 day'
            FROM "Profiles" AS profile CROSS JOIN generate_series(100010, 100025) AS work_id;
            INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "ProgressPositionTypeId", "PositionMs", "DurationMs", "UpdatedAt")
            SELECT profile."Id", 100002, 1, 2400000, 6000000, now() - interval '2 hours' FROM "Profiles" AS profile;
            INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "PositionMs", "DurationMs", "IsCompleted", "CompletedAt", "UpdatedAt")
            SELECT profile."Id", unit."WorkId", unit."Id", 1, CASE WHEN unit.place < 3 THEN 1440000 ELSE 600000 END, 1440000, unit.place < 3,
                CASE WHEN unit.place < 3 THEN now() - interval '1 day' END, now() - interval '1 day' + unit.place * interval '1 hour'
            FROM "Profiles" AS profile
                CROSS JOIN (SELECT episode."WorkId", episode."Id", row_number() OVER (PARTITION BY episode."WorkId" ORDER BY season."Number", episode."Number") AS place
                    FROM "WorkUnits" AS episode JOIN "WorkUnits" AS season ON season."Id" = episode."ParentWorkUnitId"
                    WHERE episode."WorkId" IN (200001, 300001) AND episode."WorkUnitTypeId" = 2) AS unit
            WHERE unit.place <= 3;
            INSERT INTO "WatchlistEntries" ("ProfileId", "WorkId")
            SELECT profile."Id", work_id FROM "Profiles" AS profile CROSS JOIN (VALUES (100030), (100031), (100032), (100150), (200005), (200040), (300010), (300090), (400100)) AS work (work_id);
            """;

        // Covers of their own for every catalog address: a gradient from the address, 2:3 for posters, 16:9 for backdrops, as PNG.
        private sealed class CoverServer : IAsyncDisposable
        {
            private readonly WebApplication _app;

            private CoverServer(WebApplication app, Uri address)
            {
                _app = app;
                Address = address;
            }

            public Uri Address { get; }

            public static async Task<CoverServer> StartAsync()
            {
                var builder = WebApplication.CreateSlimBuilder();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                var app = builder.Build();
                app.MapGet("/{**path}", (string path) =>
                {
                    var backdrop = path.Contains("backdrop", StringComparison.Ordinal);
                    return Results.Bytes(Gradient(backdrop ? 640 : 342, backdrop ? 360 : 513, path), "image/png");
                });
                await app.StartAsync();
                var address = new Uri(app.Urls.First().TrimEnd('/') + "/");
                return new CoverServer(app, address);
            }

            public async ValueTask DisposeAsync() => await _app.DisposeAsync();

            private static byte[] Gradient(int width, int height, string seed)
            {
                var hash = (uint)seed.Aggregate(17, (value, character) => value * 31 + character);
                var (r1, g1, b1) = Hue(hash % 360);
                var (r2, g2, b2) = Hue((hash / 360 + 140) % 360);
                var raw = new byte[height * (width * 3 + 1)];
                for (var y = 0; y < height; y++)
                {
                    var row = y * (width * 3 + 1);
                    for (var x = 0; x < width; x++)
                    {
                        var t = (x / (double)width + y / (double)height) / 2;
                        raw[row + 1 + x * 3] = (byte)(r1 + (r2 - r1) * t);
                        raw[row + 2 + x * 3] = (byte)(g1 + (g2 - g1) * t);
                        raw[row + 3 + x * 3] = (byte)(b1 + (b2 - b1) * t);
                    }
                }

                using var output = new MemoryStream();
                output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
                var header = new byte[13];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
                header[8] = 8;
                header[9] = 2;
                Chunk(output, "IHDR", header);
                using (var compressed = new MemoryStream())
                {
                    using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) zlib.Write(raw);
                    Chunk(output, "IDAT", compressed.ToArray());
                }

                Chunk(output, "IEND", []);
                return output.ToArray();
            }

            private static (int R, int G, int B) Hue(uint degrees)
            {
                double h = degrees / 60.0, c = 0.55, x = c * (1 - Math.Abs(h % 2 - 1)), m = 0.2;
                var (r, g, b) = h switch { < 1 => (c, x, 0d), < 2 => (x, c, 0d), < 3 => (0d, c, x), < 4 => (0d, x, c), < 5 => (x, 0d, c), _ => (c, 0d, x) };
                return ((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
            }

            private static void Chunk(Stream output, string type, byte[] data)
            {
                Span<byte> length = stackalloc byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
                output.Write(length);
                var named = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
                output.Write(named);
                var crc = 0xFFFFFFFFu;
                foreach (var value in named)
                {
                    crc ^= value;
                    for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
                }

                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, crc ^ 0xFFFFFFFFu);
                output.Write(length);
            }
        }
    }
}
