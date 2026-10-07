using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

/// <summary>Lets the raw acquisition tables hold the Music kind: their CHECK constraint on "Kind" is widened by one value and narrowed again on the way down.</summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261007122000_MusicAcquisitionKind")]
public partial class MusicAcquisitionKind : Migration
{
    private const string WithoutMusic = "'anime', 'manga', 'lightNovel', 'book', 'movie', 'tv', 'audiobook'";
    private const string WithMusic = "'anime', 'manga', 'lightNovel', 'book', 'movie', 'tv', 'audiobook', 'music'";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in Tables)
        {
            migrationBuilder.Sql(DropKindCheckSql(table));
            migrationBuilder.Sql(AddKindCheckSql(table, WithMusic));
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in Tables)
        {
            migrationBuilder.Sql($"DELETE FROM \"{table}\" WHERE \"Kind\" = 'music';");
            migrationBuilder.Sql(DropKindCheckSql(table));
            migrationBuilder.Sql(AddKindCheckSql(table, WithoutMusic));
        }
    }

    private static readonly string[] Tables = ["AcquisitionAccessPolicies", "AcquisitionRequests"];

    // Drops whatever CHECK constraint currently guards the raw table's "Kind" column, whatever its name, so the change is safe on any existing database.
    private static string DropKindCheckSql(string table) =>
        $@"DO $$
DECLARE constraint_name text;
BEGIN
    FOR constraint_name IN
        SELECT con.conname
        FROM pg_constraint con
        JOIN pg_class rel ON rel.oid = con.conrelid
        WHERE rel.relname = '{table}'
          AND con.contype = 'c'
          AND pg_get_constraintdef(con.oid) ILIKE '%""Kind""%'
    LOOP
        EXECUTE format('ALTER TABLE ""{table}"" DROP CONSTRAINT %I', constraint_name);
    END LOOP;
END $$;";

    private static string AddKindCheckSql(string table, string kinds) =>
        $@"ALTER TABLE ""{table}"" ADD CONSTRAINT ""CK_{table}_Kind"" CHECK (""Kind"" IN ({kinds}));";
}
