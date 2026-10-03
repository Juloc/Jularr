using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261003190000_CanonicalVideoStorage")]
public partial class CanonicalVideoStorage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameTable(name: "MediaFiles", newName: "StoredFiles");
        migrationBuilder.RenameTable(name: "MediaAnalyses", newName: "MediaTechnicalAnalyses");
        migrationBuilder.RenameTable(name: "MediaAnalysisStreams", newName: "MediaTracks");

        migrationBuilder.RenameColumn(name: "MediaFileId", table: "MediaTechnicalAnalyses", newName: "StoredFileId");
        migrationBuilder.RenameColumn(name: "MediaFileId", table: "MediaTracks", newName: "StoredFileId");

        migrationBuilder.RenameIndex(
            name: "IX_MediaFiles_Path",
            table: "StoredFiles",
            newName: "IX_StoredFiles_Path");
        migrationBuilder.RenameIndex(
            name: "IX_MediaFiles_LibraryRootId_EpisodeId",
            table: "StoredFiles",
            newName: "IX_StoredFiles_LibraryRootId_EpisodeId");
        migrationBuilder.RenameIndex(
            name: "IX_MediaAnalyses_Status_ProbeVersion",
            table: "MediaTechnicalAnalyses",
            newName: "IX_MediaTechnicalAnalyses_Status_ProbeVersion");

        migrationBuilder.DropForeignKey(
            name: "FK_MediaFiles_Episodes_EpisodeId",
            table: "StoredFiles");
        migrationBuilder.DropForeignKey(
            name: "FK_MediaAnalyses_MediaFiles_MediaFileId",
            table: "MediaTechnicalAnalyses");
        migrationBuilder.DropForeignKey(
            name: "FK_MediaAnalysisStreams_MediaAnalyses_MediaFileId",
            table: "MediaTracks");

        migrationBuilder.AlterColumn<Guid>(
            name: "EpisodeId",
            table: "StoredFiles",
            type: "uuid",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "uuid");

        migrationBuilder.AddColumn<Guid>(
            name: "MediaAssetId",
            table: "StoredFiles",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "MediaAssets",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                WorkId = table.Column<Guid>(type: "uuid", nullable: false),
                WorkEpisodeId = table.Column<Guid>(type: "uuid", nullable: true),
                WorkVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MediaAssets", x => x.Id);
                table.CheckConstraint("CK_MediaAssets_Kind", "\"Kind\" >= 0 AND \"Kind\" <= 5");
                table.ForeignKey(
                    name: "FK_MediaAssets_WorkEpisodes_WorkEpisodeId",
                    column: x => x.WorkEpisodeId,
                    principalTable: "WorkEpisodes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_MediaAssets_WorkVersions_WorkVersionId",
                    column: x => x.WorkVersionId,
                    principalTable: "WorkVersions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_MediaAssets_Works_WorkId",
                    column: x => x.WorkId,
                    principalTable: "Works",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.AddCheckConstraint(
            name: "CK_StoredFiles_SizeBytes",
            table: "StoredFiles",
            sql: "\"SizeBytes\" >= 0");

        migrationBuilder.CreateIndex(
            name: "IX_MediaAssets_WorkEpisodeId",
            table: "MediaAssets",
            column: "WorkEpisodeId");
        migrationBuilder.CreateIndex(
            name: "IX_MediaAssets_WorkId_WorkEpisodeId_Kind",
            table: "MediaAssets",
            columns: new[] { "WorkId", "WorkEpisodeId", "Kind" });
        migrationBuilder.CreateIndex(
            name: "IX_MediaAssets_WorkVersionId_Kind",
            table: "MediaAssets",
            columns: new[] { "WorkVersionId", "Kind" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_StoredFiles_MediaAssetId",
            table: "StoredFiles",
            column: "MediaAssetId");

        migrationBuilder.AddForeignKey(
            name: "FK_StoredFiles_Episodes_EpisodeId",
            table: "StoredFiles",
            column: "EpisodeId",
            principalTable: "Episodes",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);
        migrationBuilder.AddForeignKey(
            name: "FK_StoredFiles_MediaAssets_MediaAssetId",
            table: "StoredFiles",
            column: "MediaAssetId",
            principalTable: "MediaAssets",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey(
            name: "FK_MediaTechnicalAnalyses_StoredFiles_StoredFileId",
            table: "MediaTechnicalAnalyses",
            column: "StoredFileId",
            principalTable: "StoredFiles",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
        migrationBuilder.AddForeignKey(
            name: "FK_MediaTracks_StoredFiles_StoredFileId",
            table: "MediaTracks",
            column: "StoredFileId",
            principalTable: "StoredFiles",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_StoredFiles_MediaAssets_MediaAssetId",
            table: "StoredFiles");
        migrationBuilder.DropTable(name: "MediaAssets");
        migrationBuilder.DropCheckConstraint(name: "CK_StoredFiles_SizeBytes", table: "StoredFiles");
        migrationBuilder.DropForeignKey(name: "FK_StoredFiles_Episodes_EpisodeId", table: "StoredFiles");
        migrationBuilder.DropForeignKey(name: "FK_MediaTechnicalAnalyses_StoredFiles_StoredFileId", table: "MediaTechnicalAnalyses");
        migrationBuilder.DropForeignKey(name: "FK_MediaTracks_StoredFiles_StoredFileId", table: "MediaTracks");

        migrationBuilder.DropIndex(name: "IX_StoredFiles_MediaAssetId", table: "StoredFiles");
        migrationBuilder.DropColumn(name: "MediaAssetId", table: "StoredFiles");

        migrationBuilder.AlterColumn<Guid>(
            name: "EpisodeId",
            table: "StoredFiles",
            type: "uuid",
            nullable: false,
            defaultValue: Guid.Empty,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.RenameColumn(name: "StoredFileId", table: "MediaTechnicalAnalyses", newName: "MediaFileId");
        migrationBuilder.RenameColumn(name: "StoredFileId", table: "MediaTracks", newName: "MediaFileId");

        migrationBuilder.RenameIndex(
            name: "IX_StoredFiles_Path",
            table: "StoredFiles",
            newName: "IX_MediaFiles_Path");
        migrationBuilder.RenameIndex(
            name: "IX_StoredFiles_LibraryRootId_EpisodeId",
            table: "StoredFiles",
            newName: "IX_MediaFiles_LibraryRootId_EpisodeId");
        migrationBuilder.RenameIndex(
            name: "IX_MediaTechnicalAnalyses_Status_ProbeVersion",
            table: "MediaTechnicalAnalyses",
            newName: "IX_MediaAnalyses_Status_ProbeVersion");

        migrationBuilder.RenameTable(name: "StoredFiles", newName: "MediaFiles");
        migrationBuilder.RenameTable(name: "MediaTechnicalAnalyses", newName: "MediaAnalyses");
        migrationBuilder.RenameTable(name: "MediaTracks", newName: "MediaAnalysisStreams");

        migrationBuilder.AddForeignKey(
            name: "FK_MediaFiles_Episodes_EpisodeId",
            table: "MediaFiles",
            column: "EpisodeId",
            principalTable: "Episodes",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
        migrationBuilder.AddForeignKey(
            name: "FK_MediaAnalyses_MediaFiles_MediaFileId",
            table: "MediaAnalyses",
            column: "MediaFileId",
            principalTable: "MediaFiles",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
        migrationBuilder.AddForeignKey(
            name: "FK_MediaAnalysisStreams_MediaAnalyses_MediaFileId",
            table: "MediaAnalysisStreams",
            column: "MediaFileId",
            principalTable: "MediaAnalyses",
            principalColumn: "MediaFileId",
            onDelete: ReferentialAction.Cascade);
    }
}
