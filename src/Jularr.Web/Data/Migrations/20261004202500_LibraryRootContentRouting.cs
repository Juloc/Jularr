using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261004202500_LibraryRootContentRouting")]
public partial class LibraryRootContentRouting : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "PlacementPolicy", table: "LibraryRoots", type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddCheckConstraint(name: "CK_LibraryRoots_PlacementPolicy", table: "LibraryRoots", sql: "\"PlacementPolicy\" >= 0 AND \"PlacementPolicy\" <= 3");

        migrationBuilder.CreateTable(
            name: "LibraryRootContentAssignments",
            columns: table => new
            {
                LibraryRootId = table.Column<Guid>(type: "uuid", nullable: false),
                ContentType = table.Column<int>(type: "integer", nullable: false),
                IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LibraryRootContentAssignments", x => new { x.LibraryRootId, x.ContentType });
                table.CheckConstraint("CK_LibraryRootContentAssignments_ContentType", "\"ContentType\" >= 1 AND \"ContentType\" <= 8");
                table.ForeignKey(
                    name: "FK_LibraryRootContentAssignments_LibraryRoots_LibraryRootId",
                    column: x => x.LibraryRootId,
                    principalTable: "LibraryRoots",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.NoAction);
            });

        migrationBuilder.CreateIndex(
            name: "IX_LibraryRootContentAssignments_ContentType_IsDefault",
            table: "LibraryRootContentAssignments",
            columns: new[] { "ContentType", "IsDefault" });

        migrationBuilder.CreateIndex(
            name: "IX_LibraryRootContentAssignments_ContentType",
            table: "LibraryRootContentAssignments",
            column: "ContentType",
            unique: true,
            filter: "\"IsDefault\" = TRUE");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "LibraryRootContentAssignments");
        migrationBuilder.DropCheckConstraint(name: "CK_LibraryRoots_PlacementPolicy", table: "LibraryRoots");
        migrationBuilder.DropColumn(name: "PlacementPolicy", table: "LibraryRoots");
    }
}
