using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReadingUnitBindingsCoverMany : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkUnitBindings_LocalKind_LocalId",
                table: "WorkUnitBindings");

            migrationBuilder.CreateIndex(
                name: "IX_WorkUnitBindings_LocalKind_LocalId",
                table: "WorkUnitBindings",
                columns: new[] { "LocalKind", "LocalId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkUnitBindings_LocalKind_LocalId_WorkChapterId",
                table: "WorkUnitBindings",
                columns: new[] { "LocalKind", "LocalId", "WorkChapterId" },
                unique: true,
                filter: "\"WorkChapterId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkUnitBindings_LocalKind_LocalId_WorkVolumeId",
                table: "WorkUnitBindings",
                columns: new[] { "LocalKind", "LocalId", "WorkVolumeId" },
                unique: true,
                filter: "\"WorkVolumeId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkUnitBindings_LocalKind_LocalId",
                table: "WorkUnitBindings");

            migrationBuilder.DropIndex(
                name: "IX_WorkUnitBindings_LocalKind_LocalId_WorkChapterId",
                table: "WorkUnitBindings");

            migrationBuilder.DropIndex(
                name: "IX_WorkUnitBindings_LocalKind_LocalId_WorkVolumeId",
                table: "WorkUnitBindings");

            migrationBuilder.CreateIndex(
                name: "IX_WorkUnitBindings_LocalKind_LocalId",
                table: "WorkUnitBindings",
                columns: new[] { "LocalKind", "LocalId" },
                unique: true);
        }
    }
}
