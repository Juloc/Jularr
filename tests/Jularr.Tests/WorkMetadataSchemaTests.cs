using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jularr.Tests;

/// <summary>
/// The Work metadata schema (#820): the migration and the hand-maintained model snapshot agree with the model, every table has its own
/// bigint identity key next to the legacy Guid Work reference, and no reference cascades.
/// </summary>
[TestClass]
public sealed class WorkMetadataSchemaTests
{
    private static readonly string[] Tables = ["WorkMetadataFacts", "WorkLocalizedValues", "WorkCredits", "WorkArtwork", "WorkMetadataRefreshes"];

    [TestMethod]
    public async Task TheModelSnapshotAndMigrationCoverTheWorkMetadataTables()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();

        Assert.IsFalse(db.Database.HasPendingModelChanges(), "The model snapshot must match the model (add the entities to AppDbContextModelSnapshot).");
        Assert.AreEqual(0, (await db.Database.GetPendingMigrationsAsync()).Count());
        foreach (var type in new[] { typeof(WorkMetadataFacts), typeof(WorkLocalizedValue), typeof(WorkCredit), typeof(WorkArtwork), typeof(WorkMetadataRefresh) })
        {
            Assert.IsNotNull(db.Model.FindEntityType(type), type.Name);
        }
    }

    [TestMethod]
    public async Task EveryMetadataTableHasAnIdentityKeyAndRestrictsItsWorkReference()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        await db.Database.OpenConnectionAsync();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();

        await using var keys = new NpgsqlCommand(
            """
            SELECT columns.table_name, columns.data_type, columns.is_identity, columns.identity_generation
            FROM information_schema.columns AS columns
            WHERE columns.table_schema = 'public' AND columns.column_name = 'Id' AND columns.table_name = ANY(@tables)
            """,
            connection);
        keys.Parameters.AddWithValue("tables", Tables);
        var seen = 0;
        await using (var reader = await keys.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                seen++;
                Assert.AreEqual("bigint", reader.GetString(1), reader.GetString(0));
                Assert.AreEqual("YES", reader.GetString(2), reader.GetString(0));
                Assert.AreEqual("BY DEFAULT", reader.GetString(3), reader.GetString(0));
            }
        }

        Assert.AreEqual(Tables.Length, seen);

        await using var references = new NpgsqlCommand(
            """
            SELECT constraints.table_name, rules.delete_rule
            FROM information_schema.referential_constraints AS rules
            INNER JOIN information_schema.table_constraints AS constraints ON constraints.constraint_name = rules.constraint_name
            WHERE constraints.table_name = ANY(@tables)
            """,
            connection);
        references.Parameters.AddWithValue("tables", Tables);
        await using var rulesReader = await references.ExecuteReaderAsync();
        var foreignKeys = 0;
        while (await rulesReader.ReadAsync())
        {
            foreignKeys++;
            Assert.AreNotEqual("CASCADE", rulesReader.GetString(1), rulesReader.GetString(0));
            Assert.AreNotEqual("SET NULL", rulesReader.GetString(1), rulesReader.GetString(0));
        }

        Assert.AreEqual(Tables.Length, foreignKeys, "Each table references its Work exactly once.");
    }
}
