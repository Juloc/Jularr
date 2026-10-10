using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jularr.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReadingUnitIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "WorkVolumes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "WorkVolumes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "WorkChapters",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "WorkChapters",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkUnitBindings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalKind = table.Column<short>(type: "smallint", nullable: false),
                    LocalId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WorkVolumeId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkChapterId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsOwnerMapping = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkUnitBindings", x => x.Id);
                    table.CheckConstraint("CK_WorkUnitBindings_OneUnit", "(\"WorkVolumeId\" IS NULL) <> (\"WorkChapterId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_WorkUnitBindings_WorkChapters_WorkChapterId",
                        column: x => x.WorkChapterId,
                        principalTable: "WorkChapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkUnitBindings_WorkVolumes_WorkVolumeId",
                        column: x => x.WorkVolumeId,
                        principalTable: "WorkVolumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkUnitBindings_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkVolumes_WorkId_Provider_ExternalId",
                table: "WorkVolumes",
                columns: new[] { "WorkId", "Provider", "ExternalId" },
                unique: true,
                filter: "\"ExternalId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkVolumes_Identity",
                table: "WorkVolumes",
                sql: "(\"Provider\" IS NULL) = (\"ExternalId\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_WorkChapters_WorkId_Provider_ExternalId",
                table: "WorkChapters",
                columns: new[] { "WorkId", "Provider", "ExternalId" },
                unique: true,
                filter: "\"ExternalId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkChapters_Identity",
                table: "WorkChapters",
                sql: "(\"Provider\" IS NULL) = (\"ExternalId\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_WorkUnitBindings_LocalKind_LocalId",
                table: "WorkUnitBindings",
                columns: new[] { "LocalKind", "LocalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkUnitBindings_WorkChapterId",
                table: "WorkUnitBindings",
                column: "WorkChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkUnitBindings_WorkId",
                table: "WorkUnitBindings",
                column: "WorkId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkUnitBindings_WorkVolumeId",
                table: "WorkUnitBindings",
                column: "WorkVolumeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkUnitBindings");

            migrationBuilder.DropIndex(
                name: "IX_WorkVolumes_WorkId_Provider_ExternalId",
                table: "WorkVolumes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkVolumes_Identity",
                table: "WorkVolumes");

            migrationBuilder.DropIndex(
                name: "IX_WorkChapters_WorkId_Provider_ExternalId",
                table: "WorkChapters");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkChapters_Identity",
                table: "WorkChapters");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "WorkVolumes");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "WorkVolumes");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "WorkChapters");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "WorkChapters");
        }
    }
}
