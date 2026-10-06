using Jularr.Web.Data;
using Jularr.Web.Data.SqliteImport;
using Jularr.Web.Features.Library;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Jularr.Tests;

[TestClass]
public sealed class SqliteToPostgresImporterTests
{
    [TestMethod]
    public async Task ImportsLegacyRowsAndConvertsTypes()
    {
        await using var db = NewContext();
        await DatabaseMigrationBridge.UpgradeAsync(db);

        var legacyPath = Path.Combine(Path.GetTempPath(), $"jularr-legacy-{Guid.NewGuid():N}.db");
        var rootId = Guid.NewGuid();
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        try
        {
            // A legacy SQLite database with SQLite-typed values: text GUID, ISO timestamp, 0/1 bool.
            await using (var sqlite = new SqliteConnection($"Data Source={legacyPath}"))
            {
                await sqlite.OpenAsync();
                await using var create = sqlite.CreateCommand();
                create.CommandText =
                    """
                    CREATE TABLE "LibraryRoots" (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "Name" TEXT NOT NULL,
                        "Path" TEXT NOT NULL,
                        "IsEnabled" INTEGER NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "WakeOnLanEnabled" INTEGER NOT NULL,
                        "ReconciliationIntervalMinutes" INTEGER NOT NULL);
                    """;
                await create.ExecuteNonQueryAsync();

                await using var insert = sqlite.CreateCommand();
                insert.CommandText =
                    """
                    INSERT INTO "LibraryRoots"
                        ("Id", "Name", "Path", "IsEnabled", "CreatedAt", "WakeOnLanEnabled", "ReconciliationIntervalMinutes")
                    VALUES ($id, 'Anime', '/mnt/anime', 1, $created, 0, 60);
                    """;
                insert.Parameters.AddWithValue("$id", rootId.ToString("D"));
                insert.Parameters.AddWithValue("$created", created.ToString("O"));
                await insert.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Import:LegacySqlitePath"] = legacyPath
                })
                .Build();

            var imported = await SqliteToPostgresImporter.RunIfNeededAsync(db, configuration);

            Assert.AreEqual(1, imported);
            var root = await db.LibraryRoots.AsNoTracking().SingleAsync();
            Assert.AreEqual(rootId, root.Id);
            Assert.AreEqual("Anime", root.Name);
            Assert.IsTrue(root.IsEnabled);
            Assert.AreEqual(created, root.CreatedAt);

            // The legacy file is renamed so a second run does nothing.
            Assert.IsFalse(File.Exists(legacyPath));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(
                         Path.GetDirectoryName(legacyPath)!,
                         Path.GetFileName(legacyPath) + "*"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [TestMethod]
    public async Task SkipsImportWhenTargetAlreadyHasData()
    {
        await using var db = NewContext();
        await DatabaseMigrationBridge.UpgradeAsync(db);
        db.LibraryRoots.Add(new LibraryRoot { Name = "Existing", Path = "/mnt/existing" });
        await db.SaveChangesAsync();

        var legacyPath = Path.Combine(Path.GetTempPath(), $"jularr-legacy-{Guid.NewGuid():N}.db");
        await using (var sqlite = new SqliteConnection($"Data Source={legacyPath}"))
        {
            await sqlite.OpenAsync();
            await using var create = sqlite.CreateCommand();
            create.CommandText =
                """CREATE TABLE "LibraryRoots" ("Id" TEXT NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "Path" TEXT NOT NULL, "IsEnabled" INTEGER NOT NULL, "CreatedAt" TEXT NOT NULL);""";
            await create.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Import:LegacySqlitePath"] = legacyPath
                })
                .Build();

            var imported = await SqliteToPostgresImporter.RunIfNeededAsync(db, configuration);

            Assert.AreEqual(0, imported);
            // The existing database was left untouched and the legacy file was not consumed.
            Assert.AreEqual(1, await db.LibraryRoots.CountAsync());
            Assert.IsTrue(File.Exists(legacyPath));
        }
        finally
        {
            try
            {
                File.Delete(legacyPath);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// The Work metadata tables (#820) exist only since the PostgreSQL epoch, so an Epoch 3 SQLite file never has them: the import
    /// skips them, leaves them empty, and their identity columns keep working for the spool afterwards.
    /// </summary>
    [TestMethod]
    public async Task PostgresOnlyWorkMetadataTablesSurviveAnImportEmptyAndUsable()
    {
        await using var db = NewContext();
        await DatabaseMigrationBridge.UpgradeAsync(db);
        var legacyPath = Path.Combine(Path.GetTempPath(), $"jularr-legacy-{Guid.NewGuid():N}.db");
        try
        {
            await using (var sqlite = new SqliteConnection($"Data Source={legacyPath}"))
            {
                await sqlite.OpenAsync();
                await using var create = sqlite.CreateCommand();
                create.CommandText =
                    """
                    CREATE TABLE "Works" ("Id" TEXT NOT NULL PRIMARY KEY, "MediaType" INTEGER NOT NULL, "CanonicalTitle" TEXT NOT NULL, "Year" INTEGER NULL, "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL);
                    INSERT INTO "Works" VALUES ('6f9619ff-8b86-d011-b42d-00c04fc964ff', 0, 'Fight Club', 1999, '2026-01-02T03:04:05.0000000Z', '2026-01-02T03:04:05.0000000Z');
                    """;
                await create.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Import:LegacySqlitePath"] = legacyPath }).Build();
            Assert.AreEqual(1, await SqliteToPostgresImporter.RunIfNeededAsync(db, configuration));

            var workId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
            var store = new Jularr.Web.Features.MediaCore.WorkMetadataStore(db);
            Assert.AreEqual(0, await db.Set<Jularr.Web.Features.MediaCore.WorkMetadataRefresh>().CountAsync());
            await store.EnqueueAsync(workId, "en", Jularr.Web.Features.MediaCore.WorkMetadataRefreshPriority.Library, DateTime.UtcNow, CancellationToken.None);
            await store.ReplaceLocalizedFieldAsync(workId, "en", Jularr.Web.Features.MediaCore.WorkLocalizedField.Title, ["Fight Club"], "tmdb", "550", 10, DateTime.UtcNow, CancellationToken.None);
            Assert.AreEqual(1, await db.Set<Jularr.Web.Features.MediaCore.WorkMetadataRefresh>().CountAsync(x => x.WorkId == workId && x.Id > 0));
            Assert.AreEqual(1, await db.Set<Jularr.Web.Features.MediaCore.WorkLocalizedValue>().CountAsync(x => x.WorkId == workId));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(legacyPath)!, Path.GetFileName(legacyPath) + "*"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"jularr-importer-{Guid.NewGuid():N}.db")}")
            .Options);
}
