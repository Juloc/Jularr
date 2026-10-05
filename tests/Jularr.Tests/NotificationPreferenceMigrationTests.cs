using Jularr.Tests.Infrastructure;
using Jularr.Web.Data;
using Jularr.Web.Data.SqliteImport;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;

namespace Jularr.Tests;

[TestClass]
public sealed class NotificationPreferenceMigrationTests
{
    private const string PreviousMigration = "20261004060000_CanonicalVideoProgress";
    private const string TargetMigration = "20261004140000_NotificationPreferencesTargetModel";

    [TestMethod]
    public async Task PostgreSqlMigrationMapsEveryLegacyNotificationModeDeterministically()
    {
        await using var db = await CreateDbAsync();
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "NotificationSubscriptions" ("ProfileId", "Category", "Mode", "UpdatedAtUtc") VALUES
                ('off-profile', 5, 0, '2026-10-04T12:00:00.0000000Z'),
                ('inapp-profile', 5, 1, '2026-10-04T12:00:00.0000000Z'),
                ('push-profile', 5, 2, '2026-10-04T12:00:00.0000000Z'),
                ('digest-profile', 5, 3, '2026-10-04T12:00:00.0000000Z');
            """);

        await migrator.MigrateAsync(TargetMigration);

        await db.Database.OpenConnectionAsync();
        try
        {
            await using var typeCommand = db.Database.GetDbConnection().CreateCommand();
            typeCommand.CommandText = """SELECT pg_typeof("UpdatedAtUtc")::text FROM "NotificationSubscriptions" LIMIT 1;""";
            Assert.AreEqual("timestamp with time zone", (string?)await typeCommand.ExecuteScalarAsync());
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        var store = new NotificationSubscriptionStore(db);

        var off = await store.GetEventPreferenceAsync("off-profile", JularrEventCategory.ReleaseAvailable);
        Assert.IsFalse(off.Enabled);
        Assert.AreEqual(NotificationDeliveryTiming.Immediate, off.Timing);
        CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp }, off.Channels.ToArray());

        var inApp = await store.GetEventPreferenceAsync("inapp-profile", JularrEventCategory.ReleaseAvailable);
        Assert.IsTrue(inApp.Enabled);
        Assert.AreEqual(NotificationDeliveryTiming.Immediate, inApp.Timing);
        CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp }, inApp.Channels.ToArray());

        var push = await store.GetEventPreferenceAsync("push-profile", JularrEventCategory.ReleaseAvailable);
        Assert.IsTrue(push.Enabled);
        Assert.AreEqual(NotificationDeliveryTiming.Immediate, push.Timing);
        CollectionAssert.AreEquivalent(new[] { NotificationChannel.Push }, push.Channels.ToArray());

        var profileChannels = await store.GetProfileChannelPreferencesAsync("push-profile");
        Assert.IsTrue(profileChannels[NotificationChannel.Push].Enabled);
        Assert.IsTrue(profileChannels[NotificationChannel.Push].IsExplicit);

        var digest = await store.GetEventPreferenceAsync("digest-profile", JularrEventCategory.ReleaseAvailable);
        Assert.IsFalse(digest.Enabled);
        Assert.AreEqual(NotificationDeliveryTiming.Digest, digest.Timing);
        CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp }, digest.Channels.ToArray());
    }

    [TestMethod]
    public async Task EpochThreeSqliteImportTranslatesLegacyModesIntoCurrentPreferenceSchema()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jularr-notification-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sqlitePath = Path.Combine(directory, "jularr.db");

        try
        {
            await CreateLegacySqliteAsync(sqlitePath);
            await using var db = await CreateDbAsync();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Import:LegacySqlitePath"] = sqlitePath })
                .Build();

            var imported = await SqliteToPostgresImporter.RunIfNeededAsync(db, configuration);
            Assert.AreEqual(4, imported);

            var store = new NotificationSubscriptionStore(db);

            var off = await store.GetEventPreferenceAsync("off-profile", JularrEventCategory.ReleaseAvailable);
            Assert.IsFalse(off.Enabled);
            CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp }, off.Channels.ToArray());

            var inApp = await store.GetEventPreferenceAsync("inapp-profile", JularrEventCategory.ReleaseAvailable);
            Assert.IsTrue(inApp.Enabled);
            CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp }, inApp.Channels.ToArray());

            var push = await store.GetEventPreferenceAsync("push-profile", JularrEventCategory.ReleaseAvailable);
            Assert.IsTrue(push.Enabled);
            CollectionAssert.AreEquivalent(new[] { NotificationChannel.Push }, push.Channels.ToArray());
            var profileChannels = await store.GetProfileChannelPreferencesAsync("push-profile");
            Assert.IsTrue(profileChannels[NotificationChannel.Push].Enabled);

            var digest = await store.GetEventPreferenceAsync("digest-profile", JularrEventCategory.ReleaseAvailable);
            Assert.IsFalse(digest.Enabled);
            Assert.AreEqual(NotificationDeliveryTiming.Digest, digest.Timing);
            CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp }, digest.Channels.ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<AppDbContext> CreateDbAsync()
    {
        var connection = TestPostgres.ResolveConnectionString($"Data Source=notification-preference-migration-{Guid.NewGuid():N}.db");
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }

    private static async Task CreateLegacySqliteAsync(string path)
    {
        await using var sqlite = new SqliteConnection($"Data Source={path}");
        await sqlite.OpenAsync();

        await using var command = sqlite.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE "NotificationSubscriptions" (
                "ProfileId" TEXT NOT NULL,
                "Category" INTEGER NOT NULL,
                "Mode" INTEGER NOT NULL,
                "UpdatedAtUtc" TEXT NOT NULL,
                CONSTRAINT "PK_NotificationSubscriptions" PRIMARY KEY ("ProfileId", "Category")
            );

            INSERT INTO "NotificationSubscriptions" ("ProfileId", "Category", "Mode", "UpdatedAtUtc") VALUES
                ('off-profile', 5, 0, '2026-10-04T12:00:00.0000000Z'),
                ('inapp-profile', 5, 1, '2026-10-04T12:00:00.0000000Z'),
                ('push-profile', 5, 2, '2026-10-04T12:00:00.0000000Z'),
                ('digest-profile', 5, 3, '2026-10-04T12:00:00.0000000Z');
            """;
        await command.ExecuteNonQueryAsync();
    }
}
