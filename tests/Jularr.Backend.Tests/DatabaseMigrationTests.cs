using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Tests.Infrastructure;
using Jularr.Frontend.Framework;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Jularr.Backend.Tests.DatabaseBaselineTests;

namespace Jularr.Backend.Tests
{
    // The migration mechanism after the release freeze: explicit baseline-only test fixtures use probe migrations,
    // and a script stands in for pg_dump so the backup-first rule is tested without the PostgreSQL client.
    [TestClass]
    public sealed class DatabaseMigrationTests
    {
        private const string AddColumnAndTable =
            """
            ALTER TABLE "Works" ADD COLUMN "UpgradeNote" text NOT NULL DEFAULT 'migrated';
            CREATE TABLE "UpgradeNotes" ("Id" bigint PRIMARY KEY, "WorkId" bigint NOT NULL REFERENCES "Works" ("Id"));
            """;

        // Writes the custom-format signature to the --file target, or fails like a broken pg_dump.
        private static string FakeDump(string folder, bool succeeds)
        {
            if (OperatingSystem.IsWindows())
            {
                var cmd = Path.Combine(folder, "fake-pg-dump.cmd");
                File.WriteAllText(cmd, succeeds
                    ? "@echo off\r\n:loop\r\nif \"%~1\"==\"\" exit /b 1\r\nif \"%~1\"==\"--file\" goto found\r\nshift\r\ngoto loop\r\n:found\r\n<nul set /p \"=PGDMP\" > \"%~2\"\r\nexit /b 0\r\n"
                    : "@echo off\r\necho pg_dump: error: simulated failure 1>&2\r\nexit /b 1\r\n");
                return cmd;
            }

            var script = Path.Combine(folder, "fake-pg-dump.sh");
            File.WriteAllText(script, succeeds
                ? "#!/bin/sh\nfor a in \"$@\"; do case \"$a\" in --file=*) printf 'PGDMP' > \"${a#--file=}\";; esac; done\n"
                : "#!/bin/sh\necho 'pg_dump: error: simulated failure' >&2\nexit 1\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return script;
        }

        private static BackupOptions Backup(string folder, out string backups)
        {
            backups = Directory.CreateDirectory(Path.Combine(folder, "backups")).FullName;
            var keys = Directory.CreateDirectory(Path.Combine(folder, "keys")).FullName;
            File.WriteAllText(Path.Combine(keys, "key-1.xml"), "<key/>");
            return new BackupOptions { Path = backups, KeyDirectory = keys };
        }

        private static Task InsertWorkAsync(string cs) => ExecuteAsync(cs,
            """
            INSERT INTO "Accounts" ("Id", "Username", "Email", "DisplayName", "AccountRoleTypeId") VALUES (41, 'migrate_owner', 'migrate@example.test', 'Migrate Owner', 1);
            INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (501, 1, 'Kept Film');
            """);

        [TestMethod]
        public async Task WorkCatalogColumns_UpgradeKeepsWorksMarksThemDetailedAndAddsDefaults()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(WorkCatalogColumns_UpgradeKeepsWorksMarksThemDetailedAndAddsDefaults));
            var options = Backup(folder.Path, out _);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);
            await new DatabaseBaseline(cs, migrations: DatabaseBaseline.Migrations.Where(migration => migration.Version < 19).ToArray()).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);
            await new DatabaseBaseline(cs, options, tools).StartAsync(CancellationToken.None);
            // Existing Works count as detailed and just opened, so neither the cleanup nor the metadata fill takes them up at once.
            Assert.AreEqual(1L, await ScalarAsync(cs, """
                SELECT count(*) FROM "Works" WHERE "Id" = 501 AND "CanonicalTitle" = 'Kept Film' AND "DetailedAt" IS NOT NULL AND "ViewedAt" IS NOT NULL
                    AND NOT "DetailedAt" < "ViewedAt" - interval '1 day' AND "MetadataTriedAt" IS NULL
                """));
            Assert.AreEqual(1L, await ScalarAsync(cs, """SELECT count(*) FROM "Works" WHERE "Id" = 501 AND "IsAdult" = FALSE AND "CatalogNamespace" IS NULL AND "FetchedAt" IS NULL AND "OriginalTitle" IS NULL AND "Overview" IS NULL AND "Creator" IS NULL AND "ImageUrl" IS NULL AND "BackdropUrl" IS NULL AND "Popularity" IS NULL AND "Rating" IS NULL AND "Votes" IS NULL AND "TrendingRank" IS NULL"""));
            await ExecuteAsync(cs, """UPDATE "Works" SET "OriginalTitle" = 'Gehaltener Film' WHERE "Id" = 501""");
            Assert.AreEqual(1L, await ScalarAsync(cs, """SELECT count(*) FROM "Works" WHERE "Id" = 501 AND "SearchText" @@ to_tsquery('simple', 'gehaltener') AND "SearchText" @@ to_tsquery('simple', 'kept')"""));
            Assert.AreEqual((short)14, await ScalarAsync(cs, """SELECT "CatalogDetailKeepDays" FROM "InstanceSettings" WHERE "Id" = 1"""));
            Assert.AreEqual(19, await ScalarAsync(cs, """SELECT "Version" FROM "DatabaseSchemaVersions" WHERE "Name" = '0019_work_catalog_columns'"""));
            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\""));
        }

        [TestMethod]
        [DataRow(true, true)]
        [DataRow(true, false)]
        [DataRow(false, false)]
        public async Task CachingModule_UpgradeKeepsTheModuleStates_AndDropsTheChoicesOfEachCatalog(bool caching, bool fullCatalog)
        {
            // The Caching module (DiscoverCache) and its sub-option LocalDiscovery decide for the whole instance; both keep their state.
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(CachingModule_UpgradeKeepsTheModuleStates_AndDropsTheChoicesOfEachCatalog) + caching + fullCatalog);
            var options = Backup(folder.Path, out _);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);
            await new DatabaseBaseline(cs, migrations: DatabaseBaseline.Migrations.Where(migration => migration.Version < 22).ToArray()).StartAsync(CancellationToken.None);
            await ExecuteAsync(cs, $"""
                UPDATE "ExternalWorkSyncs" SET "IsEnabled" = "Namespace" = 'tmdb_movie', "DetailsTrending" = TRUE, "CacheTier" = 3, "RequestsPerMinute" = 30;
                UPDATE "InstanceModules" SET "IsEnabled" = {(caching ? "TRUE" : "FALSE")} WHERE "Key" = 'DiscoverCache';
                UPDATE "InstanceModules" SET "IsEnabled" = {(fullCatalog ? "TRUE" : "FALSE")} WHERE "Key" = 'LocalDiscovery';
                INSERT INTO "ExternalWorkSyncWindows" ("Namespace", "StartsAt", "EndsAt") VALUES ('anilist', '02:00', '06:00');
                """);
            await new DatabaseBaseline(cs, options, tools).StartAsync(CancellationToken.None);

            Assert.AreEqual((caching ? "true" : "false") + ":" + (fullCatalog ? "true" : "false"), await ScalarAsync(cs,
                """SELECT string_agg("IsEnabled"::text, ':' ORDER BY "Key") FROM "InstanceModules" WHERE "Key" IN ('DiscoverCache', 'LocalDiscovery')"""));
            Assert.AreEqual(1L, await ScalarAsync(cs, """SELECT count(*) FROM "InstanceModuleDependencies" WHERE "ModuleKey" = 'LocalDiscovery' AND "RequiredKey" = 'DiscoverCache'"""),
                "The full catalog stays a sub-option of the Caching module.");
            Assert.AreEqual(0L, await ScalarAsync(cs, """SELECT count(*) FROM information_schema.columns WHERE table_name = 'ExternalWorkSyncs' AND column_name IN ('IsEnabled', 'DetailsTrending', 'CacheTier')"""));
            Assert.AreEqual("anilist:30,tmdb_movie:30,tvdb:120", await ScalarAsync(cs, """SELECT string_agg("Namespace" || ':' || "RequestsPerMinute", ',' ORDER BY "Namespace") FROM "ExternalWorkSyncs" """),
                "Each catalog keeps its pace; the TMDB series catalog went with 0023, TheTVDB's card came with 0024.");
            Assert.AreEqual(1L, await ScalarAsync(cs, """SELECT count(*) FROM "ExternalWorkSyncWindows" WHERE "Namespace" = 'anilist'"""), "The Owner's windows stay.");
            Assert.AreEqual("false:14", await ScalarAsync(cs, """SELECT "DetailsDiscoverAhead" || ':' || "CatalogDetailKeepDays" FROM "InstanceSettings" WHERE "Id" = 1"""));
            Assert.AreEqual(22, await ScalarAsync(cs, """SELECT "Version" FROM "DatabaseSchemaVersions" WHERE "Name" = '0022_catalog_cache_tiers'"""));
            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\""));
        }

        [TestMethod]
        public async Task SeriesFromTvdb_UpgradeDropsTheTmdbSeriesCatalog_DeletesItsCatalogTitles_AndMakesLibrarySeriesDue()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(SeriesFromTvdb_UpgradeDropsTheTmdbSeriesCatalog_DeletesItsCatalogTitles_AndMakesLibrarySeriesDue));
            var options = Backup(folder.Path, out _);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);
            await new DatabaseBaseline(cs, migrations: DatabaseBaseline.Migrations.Where(migration => migration.Version < 23).ToArray()).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);
            await ExecuteAsync(cs,
                """
                INSERT INTO "ExternalWorkSyncWindows" ("Namespace", "StartsAt", "EndsAt") VALUES ('tmdb_tv', '02:00', '06:00'), ('tmdb_movie', '02:00', '06:00');
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "CatalogNamespace", "DetailedAt") VALUES
                    (601, 2, 'Catalog Series', 'tmdb_tv', NULL), (602, 2, 'Opened Series', 'tmdb_tv', now()), (603, 2, 'Library Series', 'tmdb_tv', now()),
                    (604, 2, 'Plex Series', NULL, now()), (605, 1, 'Catalog Film', 'tmdb_movie', NULL), (606, 2, 'Series Nobody Holds', NULL, now());
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES
                    (601, 'tmdb_tv', '1'), (602, 'tmdb_tv', '2'), (603, 'tmdb_tv', '3'), (604, 'plex', '4'), (605, 'tmdb_movie', '5'), (606, 'tmdb_tv', '6');
                INSERT INTO "WorkTitles" ("WorkId", "Title", "IsManual") VALUES (603, 'Meine Serie', TRUE);
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "CatalogNamespace", "DetailedAt") VALUES
                    (607, 2, 'Related Series', 'tmdb_tv', NULL), (608, 2, 'TheTVDB Series', 'tmdb_tv', NULL), (609, 2, 'Library TheTVDB Series', NULL, now()), (610, 2, 'Anime', 'anilist', NULL);
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (607, 'tmdb_tv', '7'), (608, 'tvdb', '8'), (609, 'tvdb', '9'), (610, 'tvdb', '10');
                INSERT INTO "WorkRelations" ("WorkId", "RelatedWorkId", "WorkRelationTypeId", "SourceKey") VALUES (604, 607, 1, 'tmdb');
                INSERT INTO "ExternalWorkFeeds" ("Id", "Key", "Namespace", "MediaTypeId", "DiscoverFeedTypeId", "Page", "RefreshedAt", "RetryAfter")
                VALUES (70, 'tmdb_tv:trending', 'tmdb_tv', 2, 1, 1, now(), now() + interval '6 hours');
                INSERT INTO "ExternalWorkFeedItems" ("FeedId", "Position", "WorkId") VALUES (70, 1, 601), (70, 2, 603);
                """);
            await new DatabaseBaseline(cs, options, tools).StartAsync(CancellationToken.None);

            Assert.AreEqual("anilist,tmdb_movie,tvdb", await ScalarAsync(cs, """SELECT string_agg("Namespace", ',' ORDER BY "Namespace") FROM "ExternalWorkSyncs" """));
            Assert.AreEqual("tmdb_movie", await ScalarAsync(cs, """SELECT string_agg("Namespace", ',') FROM "ExternalWorkSyncWindows" """));
            CollectionAssert.AreEqual(new long[] { 501, 603, 604, 605, 606, 607, 608, 609, 610 }, (long[])(await ScalarAsync(cs, """SELECT array_agg("Id" ORDER BY "Id") FROM "Works" """))!,
                "Catalog series of TMDB go, opened or not; a series in the library, one not from that catalog, a relation target of a library series, TheTVDB's series and the films stay.");
            Assert.AreEqual("603:false,604:false,606:true,607:false,608:false,609:true,610:false", await ScalarAsync(cs,
                """SELECT string_agg("Id" || ':' || ("DetailedAt" IS NOT NULL)::text, ',' ORDER BY "Id") FROM "Works" WHERE "MediaTypeId" = 2"""),
                "Library series wait for their details from TheTVDB.");
            Assert.AreEqual("603:tmdb_tv,604:,606:,607:tmdb_tv,608:tvdb,609:tvdb,610:anilist", await ScalarAsync(cs,
                """SELECT string_agg("Id" || ':' || coalesce("CatalogNamespace", ''), ',' ORDER BY "Id") FROM "Works" WHERE "MediaTypeId" = 2"""),
                "A series with a TheTVDB id is TheTVDB's catalog title; AniList's anime keep theirs.");
            Assert.AreEqual(1L, await ScalarAsync(cs, """SELECT count(*) FROM "ExternalWorkFeedItems" WHERE "FeedId" = 70 AND "WorkId" = 603"""));
            Assert.AreEqual(true, await ScalarAsync(cs, """SELECT "RetryAfter" <= now() FROM "ExternalWorkFeeds" WHERE "Id" = 70"""), "The series list is renewed soon.");
            await ExecuteAsync(cs, """
                INSERT INTO "People" ("Id", "Name") VALUES (900, 'Bryan Cranston');
                INSERT INTO "PersonExternalIdentities" ("PersonId", "Namespace", "Value") VALUES (900, 'tvdb', '300'), (900, 'imdb', 'nm0186505');
                """);
            Assert.AreEqual(23, await ScalarAsync(cs, """SELECT "Version" FROM "DatabaseSchemaVersions" WHERE "Name" = '0023_series_from_tvdb'"""));
            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\""));
        }

        [TestMethod]
        public async Task AnimeMappings_UpgradeAddsTheEmptyMappingsTheirSource_AndTheTvdbCatalogCard()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(AnimeMappings_UpgradeAddsTheEmptyMappingsTheirSource_AndTheTvdbCatalogCard));
            var options = Backup(folder.Path, out _);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);
            await new DatabaseBaseline(cs, migrations: DatabaseBaseline.Migrations.Where(migration => migration.Version < 24).ToArray()).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);
            await ExecuteAsync(cs, """UPDATE "ExternalWorkSyncs" SET "RequestsPerMinute" = 30, "PausesWhileUsed" = TRUE WHERE "Namespace" = 'tmdb_movie'""");
            await new DatabaseBaseline(cs, options, tools).StartAsync(CancellationToken.None);

            // The other catalogs keep the Owner's choices; TheTVDB's card has TMDB's pace and no full sync state.
            Assert.AreEqual("anilist:20:true,tmdb_movie:30:true,tvdb:120:false", await ScalarAsync(cs,
                """SELECT string_agg("Namespace" || ':' || "RequestsPerMinute" || ':' || "PausesWhileUsed", ',' ORDER BY "Namespace") FROM "ExternalWorkSyncs" """));
            Assert.AreEqual(true, await ScalarAsync(cs, """SELECT "ListedOn" IS NULL AND "Cursor" IS NULL AND "CompletedAt" IS NULL FROM "ExternalWorkSyncs" WHERE "Namespace" = 'tvdb'"""));
            Assert.AreEqual(0L, await ScalarAsync(cs, """SELECT count(*) FROM "AnimeMappings" """));
            Assert.AreEqual(true, await ScalarAsync(cs, """SELECT "ETag" IS NULL AND "LoadedAt" IS NULL FROM "AnimeMappingSources" """));
            Assert.AreEqual(1L, await ScalarAsync(cs, """SELECT count(*) FROM "Works" WHERE "Id" = 501"""));

            // A row per entry and season, ranges within themselves; one source row only.
            await ExecuteAsync(cs, """INSERT INTO "AnimeMappings" ("AniListId", "TvdbId", "SeasonNumber", "AniListEpisode", "FirstEpisode", "LastEpisode") VALUES (104578, 267440, 3, 1, 13, 22), (21, 81797, 23, 1156, 1, NULL)""");
            await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(cs, """INSERT INTO "AnimeMappings" ("AniListId", "TvdbId", "SeasonNumber", "AniListEpisode", "FirstEpisode", "LastEpisode") VALUES (1, 1, 1, 1, 5, 4)"""));
            await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(cs, """INSERT INTO "AnimeMappingSources" ("Id") VALUES (FALSE)"""));
            Assert.AreEqual(24, await ScalarAsync(cs, """SELECT "Version" FROM "DatabaseSchemaVersions" WHERE "Name" = '0024_anime_mappings_tvdb_budget'"""));
            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\""));
        }

        [TestMethod]
        public async Task BookVolumeSizes_UpgradeAddsTheLimitsPerVolumeOfTheBookKinds()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(BookVolumeSizes_UpgradeAddsTheLimitsPerVolumeOfTheBookKinds));
            var options = Backup(folder.Path, out _);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);
            await new DatabaseBaseline(cs, migrations: DatabaseBaseline.Migrations.Where(migration => migration.Version < 25).ToArray()).StartAsync(CancellationToken.None);
            await new DatabaseBaseline(cs, options, tools).StartAsync(CancellationToken.None);

            Assert.AreEqual("Ebook:0-150,Illustrated:0-300,Manga:0-700", await ScalarAsync(cs,
                """SELECT string_agg("Quality" || ':' || "MinimumMbPerMinute"::integer || '-' || "MaximumMbPerMinute"::integer, ',' ORDER BY "Quality") FROM "QualityDefinitions" WHERE "MediaTypeId" = 3"""));
            Assert.AreEqual(25, await ScalarAsync(cs, """SELECT "Version" FROM "DatabaseSchemaVersions" WHERE "Name" = '0025_book_volume_sizes'"""));
        }

        [TestMethod]
        public async Task ReleaseGroups_UpgradeAddsTheEmptyListWithOneNamePerMediaTypeInAnyCase()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(ReleaseGroups_UpgradeAddsTheEmptyListWithOneNamePerMediaTypeInAnyCase));
            var options = Backup(folder.Path, out _);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);
            await new DatabaseBaseline(cs, migrations: DatabaseBaseline.Migrations.Where(migration => migration.Version < 27).ToArray()).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);
            await new DatabaseBaseline(cs, options, tools).StartAsync(CancellationToken.None);

            Assert.AreEqual(0L, await ScalarAsync(cs, """SELECT count(*) FROM "ReleaseGroups" """));
            Assert.AreEqual(1L, await ScalarAsync(cs, """SELECT count(*) FROM "Works" WHERE "Id" = 501"""));

            // A name is kept once per media type in any case, trimmed; a rating is Good, Bad or Blocked.
            await ExecuteAsync(cs, """INSERT INTO "ReleaseGroups" ("MediaTypeId", "Name", "ReleaseGroupRatingId") VALUES (1, 'GRP', 1), (3, 'grp', NULL)""");
            await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(cs, """INSERT INTO "ReleaseGroups" ("MediaTypeId", "Name") VALUES (1, 'Grp')"""));
            await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(cs, """INSERT INTO "ReleaseGroups" ("MediaTypeId", "Name") VALUES (1, ' FLUX')"""));
            await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(cs, """INSERT INTO "ReleaseGroups" ("MediaTypeId", "Name", "ReleaseGroupRatingId") VALUES (1, 'FLUX', 4)"""));
            Assert.AreEqual(28, await ScalarAsync(cs, """SELECT "Version" FROM "DatabaseSchemaVersions" WHERE "Name" = '0028_release_groups'"""));
            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\""));
        }

        [TestMethod]
        public async Task LibraryTieIndexes_UpgradeKeepsTiesAndIndexesTheirMarkedRows()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(LibraryTieIndexes_UpgradeKeepsTiesAndIndexesTheirMarkedRows));
            var options = Backup(folder.Path, out _);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);
            await new DatabaseBaseline(cs, migrations: DatabaseBaseline.Migrations.Where(migration => migration.Version < 21).ToArray()).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);
            await ExecuteAsync(cs,
                """
                UPDATE "Works" SET "MediaSubtypeId" = 1, "IsMediaSubtypeManual" = TRUE WHERE "Id" = 501;
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "CatalogNamespace") VALUES (502, 1, 'Plex Film', NULL), (503, 1, 'Catalog Film', 'tmdb_movie'),
                    (504, 1, 'Film on an offline NAS', NULL), (505, 2, 'Hidden relation', NULL), (506, 2, 'Hidden target', 'tvdb'), (507, 2, 'Episode groups', NULL),
                    (508, 2, 'Mapped by hand', NULL), (509, 2, 'Mapped automatically', NULL), (510, 1, 'Requested', NULL);
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (502, 'plex', '42'), (503, 'tmdb_movie', '603');
                WITH edition AS (INSERT INTO "WorkEditions" ("WorkId") VALUES (504) RETURNING "Id", "WorkId"),
                version AS (INSERT INTO "WorkVersions" ("WorkId", "WorkEditionId") SELECT "WorkId", "Id" FROM edition RETURNING "Id", "WorkId")
                INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "MediaAssetTypeId") SELECT "WorkId", "Id", 1 FROM version;
                INSERT INTO "WorkRelations" ("WorkId", "RelatedWorkId", "WorkRelationTypeId", "SourceKey", "IsSuppressed") VALUES (505, 506, 1, 'tvdb', TRUE);
                INSERT INTO "WorkUnitGroups" ("WorkId", "Name", "FirstNumber", "LastNumber") VALUES (507, 'Arc', 1, 5);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (5081, 508, 1, 1), (5091, 509, 1, 1);
                INSERT INTO "WorkUnitExternalIdentities" ("WorkUnitId", "Namespace", "Value", "IsManual") VALUES (5081, 'anilist', '1', TRUE), (5091, 'anilist', '2', FALSE);
                INSERT INTO "AcquisitionRequests" ("WorkId", "RequestedByAccountId", "UiLocaleId") SELECT 510, 41, (SELECT min("Id") FROM "UiLocales");
                """);
            await new DatabaseBaseline(cs, options, tools).StartAsync(CancellationToken.None);
            Assert.AreEqual(8L, await ScalarAsync(cs, """
                SELECT count(*) FROM pg_indexes
                WHERE indexname IN ('IX_WorkExternalIdentities_LibraryTie', 'IX_Works_ManualClass', 'IX_AcquisitionRequests_Work', 'IX_MediaProgress_Work', 'IX_Works_Detailed',
                    'IX_WorkMetadataFacts_Manual', 'IX_WorkRelations_PersonsEdit', 'IX_WorkUnitExternalIdentities_Manual')
                """));

            // A file record ties its Work also while its storage is offline; a hidden relation, episode groups and a season mapped by hand are a person's edits.
            CollectionAssert.AreEqual(new long[] { 501, 502, 504, 505, 506, 507, 508, 510 }, (long[])(await ScalarAsync(cs, """SELECT array_agg(DISTINCT "WorkId" ORDER BY "WorkId") FROM "WorkLibraryRelations" """))!);
            Assert.AreEqual(21, await ScalarAsync(cs, """SELECT "Version" FROM "DatabaseSchemaVersions" WHERE "Name" = '0021_library_tie_indexes'"""));
            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\""));
        }

        [TestMethod]
        public async Task MergeExternalWorks_UpgradeKeepsWhatPeopleMadeAsWorksAndDropsTheCache()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(MergeExternalWorks_UpgradeKeepsWhatPeopleMadeAsWorksAndDropsTheCache));
            var options = Backup(folder.Path, out _);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);
            await new DatabaseBaseline(cs, migrations: DatabaseBaseline.Migrations.Where(migration => migration.Version < 20).ToArray()).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);
            await ExecuteAsync(cs,
                """
                UPDATE "Works" SET "Overview" = 'Own overview' WHERE "Id" = 501;
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (501, 'tmdb_movie', '603'), (501, 'imdb', 'tt0133093');
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle") VALUES (502, 2, 1, 'Frieren'), (503, 1, NULL, 'Library Remake');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (503, 'tmdb_movie', '605');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (5021, 502, 1, 1), (5022, 502, 1, 2);
                INSERT INTO "WorkUnitExternalIdentities" ("WorkUnitId", "Namespace", "Value") VALUES (5021, 'anilist', '154587'), (5022, 'anilist', '154588');
                INSERT INTO "WantedItems" ("WorkId") VALUES (503);
                INSERT INTO "ExternalWorks" ("Id", "Namespace", "Value", "MediaTypeId", "MediaSubtypeId", "Title", "OriginalTitle", "ReleaseYear", "Overview", "ImageUrl", "ImdbId", "Rating", "Votes",
                    "TrendingRank", "DetailedAt")
                VALUES
                    (1, 'tmdb_movie', '603', 1, NULL, 'Matrix', 'The Matrix', 1999, 'Catalog overview', 'https://image.test/603.jpg', 'tt0133093', 8.2, 500, 1, now()),
                    (2, 'tmdb_movie', '604', 1, NULL, 'Matrix Reloaded', 'The Matrix Reloaded', 2003, NULL, 'https://image.test/604.jpg', 'tt0234215', 7.0, 300, 2, now()),
                    (3, 'tmdb_movie', '605', 1, NULL, 'Remake Catalog', NULL, 2020, NULL, NULL, NULL, NULL, NULL, NULL, NULL),
                    (4, 'tmdb_movie', '606', 1, NULL, 'Same Imdb', NULL, 2001, NULL, NULL, 'tt0133093', NULL, NULL, NULL, NULL),
                    (5, 'anilist', '154587', 2, 1, 'Frieren Season 1', NULL, 2023, NULL, NULL, NULL, NULL, NULL, NULL, NULL),
                    (6, 'anilist', '154588', 2, 1, 'Frieren Season 2', NULL, 2026, NULL, NULL, NULL, NULL, NULL, NULL, NULL),
                    (7, 'anilist', '999001', 2, 1, 'Unmapped Anime', NULL, 2024, NULL, 'https://image.test/999001.jpg', NULL, 8.0, 100, 3, NULL),
                    (8, 'tmdb_movie', '700', 1, NULL, 'Unrelated', NULL, 2010, NULL, NULL, NULL, NULL, NULL, NULL, now());
                INSERT INTO "WorkRelations" ("Id", "WorkId", "RelatedWorkId", "ExternalWorkId", "WorkRelationTypeId", "SourceKey") VALUES
                    (1, 501, NULL, 2, 1, 'manual'),
                    (2, 501, 503, NULL, 9, 'tmdb'),
                    (3, 501, NULL, 3, 9, 'manual'),
                    (4, 503, NULL, 4, 11, 'tmdb'),
                    (5, 502, NULL, 5, 1, 'anilist'),
                    (6, 501, NULL, 6, 6, 'anilist'),
                    (7, 501, NULL, 7, 11, 'anilist');
                INSERT INTO "ExternalWorkFeeds" ("Id", "Key", "Namespace", "MediaTypeId", "DiscoverFeedTypeId", "Page", "RefreshedAt", "RetryAfter")
                VALUES (1, 'tmdb_movie/1/0/1/1//en', 'tmdb_movie', 1, 1, 1, now(), now() + interval '1 hour');
                INSERT INTO "ExternalWorkFeedItems" ("FeedId", "Position", "ExternalWorkId") VALUES (1, 1, 1), (1, 2, 8);
                UPDATE "ExternalWorkSyncs" SET "ListedOn" = '2026-10-01', "Cursor" = 5, "ChangesReadAt" = now(), "CompletedAt" = now();
                """);
            await new DatabaseBaseline(cs, options, tools).StartAsync(CancellationToken.None);

            // A Work keeps its own fields and takes only the empty catalog fields of its entry; the trending rank is list state.
            Assert.AreEqual(1L, await ScalarAsync(cs, """
                SELECT count(*) FROM "Works" WHERE "Id" = 501 AND "CanonicalTitle" = 'Kept Film' AND "Overview" = 'Own overview' AND "OriginalTitle" = 'The Matrix'
                    AND "ImageUrl" = 'https://image.test/603.jpg' AND "Rating" = 8.2 AND "Votes" = 500 AND "CatalogNamespace" = 'tmdb_movie' AND "TrendingRank" IS NULL AND "FetchedAt" IS NOT NULL
                """));
            Assert.AreEqual(1L, await ScalarAsync(cs, """SELECT count(*) FROM "Works" WHERE "Id" = 503 AND "CanonicalTitle" = 'Library Remake' AND "CatalogNamespace" = 'tmdb_movie' AND "FetchedAt" IS NULL"""));
            Assert.AreEqual(1L, await ScalarAsync(cs, """SELECT count(*) FROM "Works" WHERE "Id" = 502 AND "CatalogNamespace" IS NULL"""), "A season's entry does not describe its series.");

            // Relation targets became minimal Works with their identity, and the relations name them.
            var reloaded = (long)(await ScalarAsync(cs, """SELECT "WorkId" FROM "WorkExternalIdentities" WHERE "Namespace" = 'tmdb_movie' AND "Value" = '604'"""))!;
            Assert.AreEqual(1L, await ScalarAsync(cs, $"""
                SELECT count(*) FROM "Works" WHERE "Id" = {reloaded} AND "MediaTypeId" = 1 AND "CanonicalTitle" = 'Matrix Reloaded' AND "OriginalTitle" = 'The Matrix Reloaded' AND "ReleaseYear" = 2003
                    AND "CatalogNamespace" = 'tmdb_movie' AND "DetailedAt" IS NULL AND "FetchedAt" IS NOT NULL AND "Votes" = 300 AND "TrendingRank" IS NULL
                """));
            Assert.AreEqual("tt0234215", await ScalarAsync(cs, $"""SELECT "Value" FROM "WorkExternalIdentities" WHERE "WorkId" = {reloaded} AND "Namespace" = 'imdb'"""));
            Assert.AreEqual(reloaded, await ScalarAsync(cs, """SELECT "RelatedWorkId" FROM "WorkRelations" WHERE "Id" = 1 AND "SourceKey" = 'manual'"""));
            var unmapped = (long)(await ScalarAsync(cs, """SELECT "WorkId" FROM "WorkExternalIdentities" WHERE "Namespace" = 'anilist' AND "Value" = '999001'"""))!;
            Assert.AreEqual(1L, await ScalarAsync(cs, $"""
                SELECT count(*) FROM "Works" WHERE "Id" = {unmapped} AND "MediaTypeId" = 2 AND "MediaSubtypeId" = 1 AND "CatalogNamespace" = 'anilist' AND "DetailedAt" IS NULL AND "FetchedAt" IS NULL
                """));
            Assert.AreEqual(unmapped, await ScalarAsync(cs, """SELECT "RelatedWorkId" FROM "WorkRelations" WHERE "Id" = 7"""));
            Assert.AreEqual(502L, await ScalarAsync(cs, """SELECT "RelatedWorkId" FROM "WorkRelations" WHERE "Id" = 6"""), "A mapped anime season is its series.");
            Assert.AreEqual(0L, await ScalarAsync(cs, """SELECT count(*) FROM "WorkRelations" WHERE "Id" = 5"""), "A relation to the Work's own season says nothing.");
            Assert.AreEqual("manual", await ScalarAsync(cs, """SELECT string_agg("SourceKey", ',') FROM "WorkRelations" WHERE "WorkId" = 501 AND "RelatedWorkId" = 503 AND "WorkRelationTypeId" = 9"""),
                "Of two relations that now say the same, a person's stays.");

            // An IMDb id another Work holds stays there; nothing is merged.
            var sameImdb = (long)(await ScalarAsync(cs, """SELECT "WorkId" FROM "WorkExternalIdentities" WHERE "Namespace" = 'tmdb_movie' AND "Value" = '606'"""))!;
            Assert.AreEqual(0L, await ScalarAsync(cs, $"""SELECT count(*) FROM "WorkExternalIdentities" WHERE "WorkId" = {sameImdb} AND "Namespace" = 'imdb'"""));
            Assert.AreEqual(501L, await ScalarAsync(cs, """SELECT "WorkId" FROM "WorkExternalIdentities" WHERE "Namespace" = 'imdb' AND "Value" = 'tt0133093'"""));
            Assert.AreEqual(sameImdb, await ScalarAsync(cs, """SELECT "RelatedWorkId" FROM "WorkRelations" WHERE "Id" = 4"""));

            // The rest of the cache is gone; feeds load again at once and the full sync starts over.
            Assert.AreEqual(0L, await ScalarAsync(cs, """SELECT count(*) FROM "WorkExternalIdentities" WHERE "Value" IN ('700', '154587', '154588')"""));
            Assert.AreEqual(6L, await ScalarAsync(cs, """SELECT count(*) FROM "Works" """));
            Assert.AreEqual(0L, await ScalarAsync(cs, """SELECT count(*) FROM "ExternalWorkFeedItems" """));
            Assert.AreEqual(1L, await ScalarAsync(cs, """SELECT count(*) FROM "ExternalWorkFeeds" WHERE "RefreshedAt" IS NULL AND "RetryAfter" <= now()"""));
            Assert.AreEqual(3L, await ScalarAsync(cs, """
                SELECT count(*) FROM "ExternalWorkSyncs" WHERE "ListedOn" IS NULL AND "Cursor" IS NULL AND "ChangesReadAt" IS NULL AND "CompletedAt" IS NULL AND "RequestsPerMinute" > 0
                """), "TMDB films, AniList and TheTVDB's card of 0024; the TMDB series catalog went with 0023.");
            Assert.AreEqual(DBNull.Value, await ScalarAsync(cs, """SELECT to_regclass('"ExternalWorks"')::text"""));
            Assert.AreEqual(0L, await ScalarAsync(cs, """
                SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND column_name = 'ExternalWorkId' AND table_name IN ('WorkRelations', 'ExternalWorkFeedItems')
                """));
            await ExecuteAsync(cs, $"""INSERT INTO "ExternalWorkFeedItems" ("FeedId", "Position", "WorkId", "SeasonNumber") VALUES (1, 1, 502, 1), (1, 2, 502, 2), (1, 3, {unmapped}, NULL)""");
            await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(cs, """INSERT INTO "ExternalWorkFeedItems" ("FeedId", "Position", "WorkId", "SeasonNumber") VALUES (1, 4, 502, 1)"""));

            // The library tie: a wanted Work and a person's relation are tied, a catalog title is not.
            Assert.AreEqual(1L, await ScalarAsync(cs, """SELECT count(DISTINCT "WorkId") FROM "WorkLibraryRelations" WHERE "WorkId" = 503"""));
            Assert.AreEqual(1L, await ScalarAsync(cs, $"""SELECT count(DISTINCT "WorkId") FROM "WorkLibraryRelations" WHERE "WorkId" = {reloaded}"""));
            Assert.AreEqual(0L, await ScalarAsync(cs, $"""SELECT count(*) FROM "WorkLibraryRelations" WHERE "WorkId" = {unmapped}"""));
            Assert.AreEqual(20, await ScalarAsync(cs, """SELECT "Version" FROM "DatabaseSchemaVersions" WHERE "Name" = '0020_merge_external_works'"""));
            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\""));
        }

        [TestMethod]
        public async Task ContentLanguages_UpgradePreservesExistingProfilesWithoutInferringTheirPreferences()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(ContentLanguages_UpgradePreservesExistingProfilesWithoutInferringTheirPreferences));
            var options = Backup(folder.Path, out var backups);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);
            await new DatabaseBaseline(cs, migrations: DatabaseBaseline.Migrations.Where(migration => migration.Version < 11).ToArray()).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);
            await ExecuteAsync(cs, """
                BEGIN;
                INSERT INTO "Profiles" ("Id", "OwnerAccountId", "DisplayName", "UiLocaleId")
                SELECT 501, 41, 'Kept profile', "Id" FROM "UiLocales" ORDER BY "Id" LIMIT 1;
                INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (41, 501);
                COMMIT;
                """);
            await new DatabaseBaseline(cs, options, tools).StartAsync(CancellationToken.None);
            Assert.AreEqual("Kept profile", await ScalarAsync(cs, """SELECT "DisplayName" FROM "Profiles" WHERE "Id" = 501"""));
            Assert.AreEqual(1L, await ScalarAsync(cs, """SELECT count(*) FROM "Profiles" WHERE "Id" = 501 AND "FirstContentLanguage" IS NULL AND "SecondContentLanguage" IS NULL"""));
            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\""));
            Assert.AreEqual(1, Directory.GetDirectories(backups).Length);
        }

        [TestMethod]
        public async Task ProductionCompanionMigration_UpgradesTheFrozenBaselineWithBackup()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(ProductionCompanionMigration_UpgradesTheFrozenBaselineWithBackup));
            var options = Backup(folder.Path, out var backups);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);

            await new DatabaseBaseline(cs, options, tools, []).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);
            await new DatabaseBaseline(cs, options, tools).StartAsync(CancellationToken.None);

            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
            Assert.AreEqual("Kept Film", await ScalarAsync(cs, "SELECT \"CanonicalTitle\" FROM \"Works\" WHERE \"Id\" = 501;"));
            Assert.AreEqual(1L, await ScalarAsync(cs, "SELECT count(*) FROM pg_tables WHERE tablename = 'PlaybackCompanionStates';"));
            Assert.AreEqual(1, Directory.GetDirectories(backups).Length);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task WantedProjection_Upgrade_PreservesDataAndRecordsTheReleaseMigration(bool fromFeedSchema)
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(WantedProjection_Upgrade_PreservesDataAndRecordsTheReleaseMigration) + fromFeedSchema);
            var options = Backup(folder.Path, out var backups);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);
            await new DatabaseBaseline(cs, migrations: fromFeedSchema ? DatabaseBaseline.Migrations.Take(1).ToArray() : []).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);
            await ExecuteAsync(cs, "INSERT INTO \"WantedItems\" (\"WorkId\") VALUES (501);");
            if (fromFeedSchema)
                await ExecuteAsync(cs, """INSERT INTO "ExternalWorkFeeds" ("Key", "Namespace", "MediaTypeId", "DiscoverFeedTypeId", "Page") VALUES ('kept-feed', 'tmdb_movie', 1, 1, 1);""");

            await new DatabaseBaseline(cs, options, tools).StartAsync(CancellationToken.None);

            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
            Assert.AreEqual(DatabaseBaseline.CurrentFingerprint, await ScalarAsync(cs, "SELECT \"Fingerprint\" FROM \"DatabaseSchemaVersions\" ORDER BY \"Version\" DESC LIMIT 1;"));
            Assert.AreEqual("Kept Film", await ScalarAsync(cs, "SELECT \"CanonicalTitle\" FROM \"Works\" WHERE \"Id\" = 501;"));
            Assert.AreEqual(1L, await ScalarAsync(cs, "SELECT count(*) FROM \"WantedItems\" WHERE \"WorkId\" = 501;"));
            Assert.AreEqual(0L, await ScalarAsync(cs, "SELECT count(*) FROM \"WantedBelowCutoff\";"));
            if (fromFeedSchema)
                Assert.AreEqual(1L, await ScalarAsync(cs, """SELECT count(*) FROM "ExternalWorkFeeds" WHERE "Key" = 'kept-feed';"""));
            Assert.HasCount(1, Directory.GetDirectories(backups));
        }

        [TestMethod]
        public async Task Upgrade_NamingFromVersionEight_PreservesEditedVideoPatternsAndSeedsOtherMediaOnce()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(Upgrade_NamingFromVersionEight_PreservesEditedVideoPatternsAndSeedsOtherMediaOnce));
            var options = Backup(folder.Path, out var backups);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);
            await new DatabaseBaseline(cs, options, tools, migrations: [.. DatabaseBaseline.Migrations.Where(migration => migration.Version <= 8)]).StartAsync(CancellationToken.None);
            await ExecuteAsync(cs, """
                UPDATE "NamingPatterns" SET "Pattern" = '{Title}/S{Season:00}/{Title} - S{Season:00}E{Episode:00} {ImdbId}', "Version" = 7 WHERE "MediaTypeId" = 2;
                UPDATE "NamingSettings" SET "ColonReplacementId" = 6, "CustomColonReplacement" = '~', "SpecialsFolder" = 'Extras - {ImdbId}', "Version" = 4 WHERE "MediaTypeId" = 2;
                """);
            var current = new DatabaseBaseline(cs, options, tools);
            await current.StartAsync(CancellationToken.None);
            await current.StartAsync(CancellationToken.None);
            Assert.AreEqual(2L, await ScalarAsync(cs, """SELECT count(*) FROM "NamingPatterns" WHERE "MediaTypeId" = 2 AND "Version" = 7 AND "Pattern" = '{Title}/S{Season:00}/{Title} - S{Season:00}E{Episode:00} {ImdbId}'"""));
            Assert.AreEqual(1L, await ScalarAsync(cs, """SELECT count(*) FROM "NamingSettings" WHERE "MediaTypeId" = 2 AND "Version" = 4 AND "SpecialsFolder" = 'Extras - {ImdbId}' AND "CustomColonReplacement" = '~' AND "RenamesFiles" = TRUE"""));
            Assert.AreEqual(5L, await ScalarAsync(cs, """SELECT count(*) FROM "NamingPatterns" WHERE "MediaTypeId" IN (3,6,7)"""));
            Assert.AreEqual(3L, await ScalarAsync(cs, """SELECT count(*) FROM "NamingSettings" WHERE "MediaTypeId" IN (3,6,7)"""));
            Assert.AreEqual(1, Directory.GetDirectories(backups).Length);
        }

        [TestMethod]
        public async Task Upgrade_ProductionFeedMigration_PreservesVersionOneDataAndBacksUpOnce()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(Upgrade_ProductionFeedMigration_PreservesVersionOneDataAndBacksUpOnce));
            await new DatabaseBaseline(cs, migrations: []).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);
            var options = Backup(folder.Path, out var backups);
            var current = new DatabaseBaseline(cs, options, Tools(cs, FakeDump(folder.Path, true), null));
            await current.StartAsync(CancellationToken.None);
            await current.StartAsync(CancellationToken.None);
            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\""));
            Assert.AreEqual("Kept Film", await ScalarAsync(cs, "SELECT \"CanonicalTitle\" FROM \"Works\" WHERE \"Id\" = 501"));
            Assert.AreEqual(2L, await ScalarAsync(cs, "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name IN ('ExternalWorkFeeds', 'ExternalWorkFeedItems')"));
            Assert.AreEqual(1, Directory.GetDirectories(backups).Length);
        }

        [TestMethod]
        public async Task Upgrade_FeedVersionTwoToCompanionVersionFour_PreservesFeedsAndCreatesCompanionSchema()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(Upgrade_FeedVersionTwoToCompanionVersionFour_PreservesFeedsAndCreatesCompanionSchema));
            var options = Backup(folder.Path, out var backups);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);

            CollectionAssert.AreEqual(Enumerable.Range(2, DatabaseBaseline.Migrations.Count).ToArray(), DatabaseBaseline.Migrations.Select(migration => migration.Version).ToArray(), "All production migrations must have unique contiguous numbers.");
            await new DatabaseBaseline(cs, options, tools, migrations: [DatabaseBaseline.Migrations[0]]).StartAsync(CancellationToken.None);
            Assert.AreEqual("0002_external_work_feeds", await ScalarAsync(cs,
                "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 2"));
            Assert.AreEqual(0L, await ScalarAsync(cs,
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'PlaybackCompanionStates'"));
            await new DatabaseBaseline(cs, options, tools).StartAsync(CancellationToken.None);
            Assert.AreEqual("0003_wanted_present_asset_projection", await ScalarAsync(cs,
                "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 3"));
            Assert.AreEqual(DatabaseBaseline.CurrentVersion, await ScalarAsync(cs,
                "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\""));
            Assert.AreEqual(2L, await ScalarAsync(cs, "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name IN ('AcquisitionSearchRuns', 'AcquisitionSearchTargets')"));
            Assert.AreEqual("0004_playback_companion", await ScalarAsync(cs,
                "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 4"));
            Assert.AreEqual("0005_sabnzbd_incomplete_paths", await ScalarAsync(cs,
                "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 5"));
            Assert.AreEqual("0006_torrent_sources", await ScalarAsync(cs,
                "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 6"));
            Assert.AreEqual("0007_download_cleanup_source_inventory", await ScalarAsync(cs,
                "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 7"));
            Assert.AreEqual("0008_browser_module", await ScalarAsync(cs,
                "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 8"));
            Assert.AreEqual("0009_pwa_main_profile_accent", await ScalarAsync(cs,
                "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 9"));
            Assert.AreEqual("0010_media_naming", await ScalarAsync(cs,
                "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 10"));
            Assert.AreEqual("0012_acquisition_search_jobs", await ScalarAsync(cs,
                "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 12"));
            Assert.AreEqual("0013_media_import_jobs", await ScalarAsync(cs,
                "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 13"));
            Assert.AreEqual("0014_keep_screen_on", await ScalarAsync(cs,
                "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 14"));
            Assert.AreEqual(1L, await ScalarAsync(cs,
                "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'ProfilePlaybackPreferences' AND column_name = 'KeepScreenOn'"));
            Assert.AreEqual("0015_second_subtitle_language", await ScalarAsync(cs,
                "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 15"));
            Assert.AreEqual(1L, await ScalarAsync(cs,
                "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'ProfilePlaybackPreferences' AND column_name = 'SecondSubtitleLanguage'"));
            Assert.AreEqual("0016_narou_source", await ScalarAsync(cs,
                "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 16"));
            Assert.AreEqual("Narou:syosetu", await ScalarAsync(cs,
                "SELECT type.\"Key\" || ':' || provider.\"Key\" FROM \"AcquisitionConnectionTypes\" AS type, \"Providers\" AS provider WHERE type.\"Id\" = 11 AND provider.\"Key\" = 'syosetu'"));
            Assert.AreEqual("0017_vpn_profiles", await ScalarAsync(cs,
                "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 17"));
            Assert.AreEqual(1L, await ScalarAsync(cs,
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'VpnProfiles'"));
            Assert.AreEqual("0018_native_torrent", await ScalarAsync(cs,
                "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 18"));
            Assert.AreEqual("Torrent", await ScalarAsync(cs,
                "SELECT \"Key\" FROM \"AcquisitionConnectionTypes\" WHERE \"Id\" = 12"));
            Assert.AreEqual("0027_smart_pages_view", await ScalarAsync(cs,
                "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 27"));
            Assert.AreEqual("SmartPages", await ScalarAsync(cs,
                "SELECT \"Key\" FROM \"ReadingViewTypes\" WHERE \"Id\" = 3"));
            Assert.AreEqual(2L, await ScalarAsync(cs,
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name IN ('MediaImportJobs','MediaImportJobRows')"));
            Assert.AreEqual(1L, await ScalarAsync(cs,
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'PlaybackCompanionStates'"));
            Assert.AreEqual(2L, await ScalarAsync(cs,
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name IN ('ExternalWorkFeeds','ExternalWorkFeedItems')"));
            await new DatabaseBaseline(cs, options, tools).StartAsync(CancellationToken.None);
            Assert.AreEqual(1, Directory.GetDirectories(backups).Length);
        }

        [TestMethod]
        public async Task Upgrade_AppliesNumberedMigrations_AfterABackup_KeepsData_AndIsConcurrencySafe()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(Upgrade_AppliesNumberedMigrations_AfterABackup_KeepsData_AndIsConcurrencySafe));
            var options = Backup(folder.Path, out var backups);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);

            // Version 1 with data and nothing pending: no backup.
            await new DatabaseBaseline(cs, options, tools, []).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);
            Assert.AreEqual(0, Directory.GetDirectories(backups).Length);

            DatabaseMigration[] migrations = [Probe(2, AddColumnAndTable)];
            await Task.WhenAll(new DatabaseBaseline(cs, options, tools, migrations).StartAsync(CancellationToken.None), new DatabaseBaseline(cs, options, tools, migrations).StartAsync(CancellationToken.None));

            Assert.AreEqual("Kept Film", await ScalarAsync(cs, "SELECT \"CanonicalTitle\" FROM \"Works\" WHERE \"Id\" = 501;"));
            Assert.AreEqual("migrated", await ScalarAsync(cs, "SELECT \"UpgradeNote\" FROM \"Works\" WHERE \"Id\" = 501;"));
            Assert.AreEqual(1L, await ScalarAsync(cs, "SELECT count(*) FROM \"Accounts\";"));
            Assert.AreEqual(1L, await ScalarAsync(cs, "SELECT count(*) FROM pg_tables WHERE tablename = 'UpgradeNotes';"));
            Assert.AreEqual(2, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
            Assert.AreEqual("0002_probe", await ScalarAsync(cs, "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 2;"));
            Assert.AreEqual(DatabaseBaseline.Fingerprint(2, migrations), await ScalarAsync(cs, "SELECT \"Fingerprint\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 2;"));

            // One backup, made before the migration (schema version 1); the second concurrent start found nothing pending.
            var backup = Assert.ContainsSingle(Directory.GetDirectories(backups));
            StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(backup, "backup.json")), "\"SchemaVersion\":1");
            Assert.IsTrue(File.Exists(Path.Combine(backup, BackupFiles.DumpName)));

            await new DatabaseBaseline(cs, options, tools, migrations).StartAsync(CancellationToken.None);
            Assert.AreEqual(1, Directory.GetDirectories(backups).Length);
        }

        [TestMethod]
        public async Task Upgrade_FailingMigration_RollsEverythingBack_KeepsTheBackup_AndRetries()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(Upgrade_FailingMigration_RollsEverythingBack_KeepsTheBackup_AndRetries));
            var options = Backup(folder.Path, out var backups);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);
            await new DatabaseBaseline(cs, options, tools, []).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);

            var broken = new DatabaseBaseline(cs, options, tools, [Probe(2, AddColumnAndTable), Probe(3, "ALTER TABLE \"Works\" ADD COLUMN \"Broken\" int NOT NULL;")]);
            var failure = await Assert.ThrowsExactlyAsync<DatabaseUpgradeException>(() => broken.StartAsync(CancellationToken.None));
            Assert.AreEqual("0003_probe", failure.Migration);

            // Migration 2 ran inside the same transaction, yet none of it remains.
            Assert.AreEqual(1, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
            Assert.AreEqual(0L, await ScalarAsync(cs, "SELECT count(*) FROM pg_tables WHERE tablename = 'UpgradeNotes';"));
            Assert.AreEqual(0L, await ScalarAsync(cs, "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Works' AND column_name = 'UpgradeNote';"));
            Assert.AreEqual("Kept Film", await ScalarAsync(cs, "SELECT \"CanonicalTitle\" FROM \"Works\" WHERE \"Id\" = 501;"));
            Assert.AreEqual(1, Directory.GetDirectories(backups).Length);

            await new DatabaseBaseline(cs, options, tools, [Probe(2, AddColumnAndTable)]).StartAsync(CancellationToken.None);
            Assert.AreEqual(2, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
        }

        [TestMethod]
        public async Task Upgrade_FailedBackup_AbortsBeforeAnyMigration()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(Upgrade_FailedBackup_AbortsBeforeAnyMigration));
            var options = Backup(folder.Path, out var backups);
            await new DatabaseBaseline(cs, migrations: []).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);

            var failing = new DatabaseBaseline(cs, options, Tools(cs, FakeDump(folder.Path, false), null), [Probe(2, AddColumnAndTable)]);
            var failure = await Assert.ThrowsExactlyAsync<DatabaseUpgradeException>(() => failing.StartAsync(CancellationToken.None));
            Assert.AreEqual("backup", failure.Migration);
            Assert.AreEqual(1, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
            Assert.AreEqual(0L, await ScalarAsync(cs, "SELECT count(*) FROM pg_tables WHERE tablename = 'UpgradeNotes';"));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(backups).Length);
        }

        [TestMethod]
        public async Task Upgrade_ChangedChecksumOfAnAppliedMigration_RefusesToStart()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(Upgrade_ChangedChecksumOfAnAppliedMigration_RefusesToStart));
            var options = Backup(folder.Path, out var backups);
            var tools = Tools(cs, FakeDump(folder.Path, true), null);
            await new DatabaseBaseline(cs, options, tools, []).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);
            await new DatabaseBaseline(cs, options, tools, [Probe(2, AddColumnAndTable)]).StartAsync(CancellationToken.None);

            var edited = new DatabaseBaseline(cs, options, tools, [Probe(2, AddColumnAndTable + "\n-- edited after it was applied")]);
            var failure = await Assert.ThrowsExactlyAsync<DatabaseUpgradeException>(() => edited.StartAsync(CancellationToken.None));
            Assert.AreEqual("0002_probe", failure.Migration);
            StringAssert.Contains(failure.Message, "checksum");
            Assert.AreEqual("Kept Film", await ScalarAsync(cs, "SELECT \"CanonicalTitle\" FROM \"Works\" WHERE \"Id\" = 501;"));
            Assert.AreEqual(1, Directory.GetDirectories(backups).Length);
        }

        // A database of the baseline from before the version table existed: complete, so it is marked version 1 and keeps its data; no backup (nothing to migrate).
        [TestMethod]
        public async Task LegacyBaselineDatabase_WithoutVersionTable_IsMarkedVersionOne_WithoutABackup()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(LegacyBaselineDatabase_WithoutVersionTable_IsMarkedVersionOne_WithoutABackup));
            await new DatabaseBaseline(cs, migrations: []).StartAsync(CancellationToken.None);
            await ExecuteAsync(cs, "UPDATE \"InstanceSettings\" SET \"Name\" = 'legacy'; DROP TABLE \"DatabaseSchemaVersions\";");

            var legacy = new DatabaseBaseline(cs, new BackupOptions { Path = folder.Path }, migrations: []);
            await legacy.StartAsync(CancellationToken.None);
            Assert.AreEqual("legacy", await ScalarAsync(cs, "SELECT \"Name\" FROM \"InstanceSettings\";"));
            Assert.AreEqual(1, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));
            Assert.AreEqual("baseline", await ScalarAsync(cs, "SELECT \"Name\" FROM \"DatabaseSchemaVersions\" WHERE \"Version\" = 1;"));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(folder.Path).Length);
            Assert.AreEqual(0L, await ScalarAsync(cs, "SELECT count(*) FROM pg_namespace WHERE nspname = 'jularr_baseline_check';"));

            await legacy.StartAsync(CancellationToken.None);
            Assert.AreEqual(1L, await ScalarAsync(cs, "SELECT count(*) FROM \"DatabaseSchemaVersions\";"));
        }

        [TestMethod]
        public async Task LegacyBaselineDatabase_WithAMissingColumn_IsRefusedAndNothingIsRecorded()
        {
            var cs = TestPostgres.ResolveConnectionString(nameof(LegacyBaselineDatabase_WithAMissingColumn_IsRefusedAndNothingIsRecorded));
            await new DatabaseBaseline(cs, migrations: []).StartAsync(CancellationToken.None);
            await ExecuteAsync(cs, "DROP TABLE \"DatabaseSchemaVersions\"; ALTER TABLE \"InstanceSettings\" DROP COLUMN \"Name\" CASCADE;");
            var error = await Assert.ThrowsExactlyAsync<DatabaseUpgradeException>(() => new DatabaseBaseline(cs, migrations: []).StartAsync(CancellationToken.None));
            StringAssert.Contains(error.Message, "InstanceSettings.Name");
            Assert.AreEqual(0L, await ScalarAsync(cs, "SELECT count(*) FROM pg_tables WHERE tablename = 'DatabaseSchemaVersions';"));
        }

        [TestMethod]
        public async Task LegacyBaselineDatabase_WithAPendingMigration_BacksUpThenMarksVersionOneAndMigrates()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(LegacyBaselineDatabase_WithAPendingMigration_BacksUpThenMarksVersionOneAndMigrates));
            var options = Backup(folder.Path, out var backups);
            await new DatabaseBaseline(cs, migrations: []).StartAsync(CancellationToken.None);
            await InsertWorkAsync(cs);
            await ExecuteAsync(cs, "DROP TABLE \"DatabaseSchemaVersions\";");

            await new DatabaseBaseline(cs, options, Tools(cs, FakeDump(folder.Path, true), null), [Probe(2, AddColumnAndTable)]).StartAsync(CancellationToken.None);
            Assert.AreEqual(2L, await ScalarAsync(cs, "SELECT count(*) FROM \"DatabaseSchemaVersions\";"));
            Assert.AreEqual("migrated", await ScalarAsync(cs, "SELECT \"UpgradeNote\" FROM \"Works\" WHERE \"Id\" = 501;"));
            Assert.AreEqual(1, Directory.GetDirectories(backups).Length);
        }

        [TestMethod]
        public async Task DatabaseUpgrade_FailedUpdate_IsReturnedForTheMaintenancePage_AndACurrentDatabaseReturnsNull()
        {
            var cs = TestPostgres.ResolveConnectionString(nameof(DatabaseUpgrade_FailedUpdate_IsReturnedForTheMaintenancePage_AndACurrentDatabaseReturnsNull));
            await new DatabaseBaseline(cs, migrations: []).StartAsync(CancellationToken.None);

            // Without a backup tool the pending migration cannot start.
            using var failing = new ServiceCollection().AddLogging()
                .AddSingleton(new DatabaseBaseline(cs, new BackupOptions(), null, [Probe(2, "SELECT 1;")])).BuildServiceProvider();
            var failure = await DatabaseUpgrade.RunAsync(failing);
            Assert.AreEqual("backup", failure!.Migration);
            Assert.AreEqual(1, await ScalarAsync(cs, "SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\";"));

            using var current = new ServiceCollection().AddLogging().AddSingleton(new DatabaseBaseline(cs, migrations: [])).BuildServiceProvider();
            Assert.IsNull(await DatabaseUpgrade.RunAsync(current));
        }

        // The maintenance page names the migration in German, never the reason (SQL, paths) that only the log holds.
        [TestMethod]
        public void MaintenancePage_NamesTheMigration_InGerman_WithoutTheReason()
        {
            var page = DatabaseMaintenance.Page(new DatabaseUpgradeFailure("0002_<script>", "relation \"Works\" secret detail"));
            StringAssert.Contains(page, "Datenbank-Update fehlgeschlagen");
            StringAssert.Contains(page, "vorherige Version kann wieder gestartet werden");
            StringAssert.Contains(page, "0002_&lt;script&gt;");
            Assert.DoesNotContain("secret detail", page);
            Assert.DoesNotContain("<script>", page);
            Assert.DoesNotContain("style=", page);
        }

        [TestMethod]
        public async Task MaintenanceHost_AnswersEveryRequestWith503AndThePage()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            using var stop = new CancellationTokenSource();
            var host = DatabaseMaintenance.RunAsync(["--urls", "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture)], new DatabaseUpgradeFailure("0002_probe", "reason"), stop.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture)) };
                HttpResponseMessage? response = null;
                for (var attempt = 0; attempt < 50 && response is null; attempt++)
                {
                    try
                    {
                        response = await client.GetAsync("/Admin/Works");
                    }
                    catch (HttpRequestException)
                    {
                        await Task.Delay(100);
                    }
                }

                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response!.StatusCode);
                StringAssert.Contains(await response.Content.ReadAsStringAsync(), "0002_probe");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/api/anything", new StringContent(""))).StatusCode);
            }
            finally
            {
                await stop.CancelAsync();
                await host;
            }
        }

        // The frozen baseline (schema version 1): after the release freeze a changed database/*.sql fails here; schema changes are new migrations.
        [TestMethod]
        public void Baseline_MatchesTheFrozenHash()
        {
            var database = Path.Combine(RepositoryRoot(), "database");
            var content = new StringBuilder();
            foreach (var name in DatabaseBaseline.Baselines)
            {
                content.Append(name).Append('\n').Append(File.ReadAllText(Path.Combine(database, name + ".sql")).Replace("\r\n", "\n", StringComparison.Ordinal));
            }

            var actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString())));
            var frozen = File.ReadAllText(Path.Combine(database, "baseline.sha256")).Trim();
            Assert.AreEqual(frozen, actual, "The baseline database/*.sql changed after the release freeze. Add a numbered migration database/migrations/NNNN_short_name.sql instead (docs/DATABASE_MIGRATIONS.md); node scripts/freeze-baseline.mjs is for releases only.");
            Assert.AreEqual(frozen, DatabaseBaseline.Fingerprint(1, []), "The embedded baselines differ from database/*.sql.");

            // Every baseline file is part of the hash.
            CollectionAssert.AreEquivalent(DatabaseBaseline.Baselines.Select(name => name + ".sql").ToArray(), Directory.GetFiles(database, "*.sql").Select(file => Path.GetFileName(file)).ToArray());
        }

        // A migration file that is not embedded (or misnamed) would silently never run.
        [TestMethod]
        public void Migrations_AreEmbeddedFromTheMigrationsFolder_ContiguousFromTwo()
        {
            var folder = Path.Combine(RepositoryRoot(), "database", "migrations");
            var files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.sql").Select(file => Path.GetFileNameWithoutExtension(file)).Order(StringComparer.Ordinal).ToArray() : [];
            CollectionAssert.AreEqual(files, DatabaseBaseline.Migrations.Select(migration => migration.Name).ToArray());
            for (var index = 0; index < files.Length; index++)
            {
                StringAssert.StartsWith(files[index], (index + 2).ToString("0000", CultureInfo.InvariantCulture) + "_");
            }
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found.");
        }
    }
}
