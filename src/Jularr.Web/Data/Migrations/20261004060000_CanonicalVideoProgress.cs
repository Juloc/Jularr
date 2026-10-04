using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261004060000_CanonicalVideoProgress")]
public partial class CanonicalVideoProgress : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "MediaProgress",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                WorkId = table.Column<Guid>(type: "uuid", nullable: false),
                WorkEpisodeId = table.Column<Guid>(type: "uuid", nullable: true),
                PositionMs = table.Column<long>(type: "bigint", nullable: false),
                DurationMs = table.Column<long>(type: "bigint", nullable: true),
                IsCompleted = table.Column<bool>(type: "boolean", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MediaProgress", x => x.Id);
                table.CheckConstraint("CK_MediaProgress_PositionMs", ""PositionMs" >= 0");
                table.CheckConstraint("CK_MediaProgress_DurationMs", ""DurationMs" IS NULL OR "DurationMs" > 0");
                table.ForeignKey(
                    name: "FK_MediaProgress_WorkEpisodes_WorkEpisodeId",
                    column: x => x.WorkEpisodeId,
                    principalTable: "WorkEpisodes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_MediaProgress_Works_WorkId",
                    column: x => x.WorkId,
                    principalTable: "Works",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "MediaPlaybackHistory",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                WorkId = table.Column<Guid>(type: "uuid", nullable: false),
                WorkEpisodeId = table.Column<Guid>(type: "uuid", nullable: true),
                StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                LastPlayedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                PositionMs = table.Column<long>(type: "bigint", nullable: false),
                DurationMs = table.Column<long>(type: "bigint", nullable: true),
                ReachedEnd = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MediaPlaybackHistory", x => x.Id);
                table.CheckConstraint("CK_MediaPlaybackHistory_PositionMs", ""PositionMs" >= 0");
                table.CheckConstraint("CK_MediaPlaybackHistory_DurationMs", ""DurationMs" IS NULL OR "DurationMs" > 0");
                table.ForeignKey(
                    name: "FK_MediaPlaybackHistory_WorkEpisodes_WorkEpisodeId",
                    column: x => x.WorkEpisodeId,
                    principalTable: "WorkEpisodes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_MediaPlaybackHistory_Works_WorkId",
                    column: x => x.WorkId,
                    principalTable: "Works",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "ActiveSessions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                WorkId = table.Column<Guid>(type: "uuid", nullable: false),
                WorkEpisodeId = table.Column<Guid>(type: "uuid", nullable: true),
                MediaAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                StoredFileId = table.Column<Guid>(type: "uuid", nullable: false),
                DeliveryMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ClientKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                LastUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ActiveSessions", x => x.Id);
                table.ForeignKey(
                    name: "FK_ActiveSessions_MediaAssets_MediaAssetId",
                    column: x => x.MediaAssetId,
                    principalTable: "MediaAssets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ActiveSessions_StoredFiles_StoredFileId",
                    column: x => x.StoredFileId,
                    principalTable: "StoredFiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ActiveSessions_WorkEpisodes_WorkEpisodeId",
                    column: x => x.WorkEpisodeId,
                    principalTable: "WorkEpisodes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ActiveSessions_Works_WorkId",
                    column: x => x.WorkId,
                    principalTable: "Works",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MediaProgress_ProfileId_WorkId_Movie",
            table: "MediaProgress",
            columns: new[] { "ProfileId", "WorkId" },
            unique: true,
            filter: ""WorkEpisodeId" IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_MediaProgress_ProfileId_WorkEpisodeId",
            table: "MediaProgress",
            columns: new[] { "ProfileId", "WorkEpisodeId" },
            unique: true,
            filter: ""WorkEpisodeId" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_MediaProgress_ProfileId_UpdatedAt",
            table: "MediaProgress",
            columns: new[] { "ProfileId", "UpdatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_MediaProgress_WorkId",
            table: "MediaProgress",
            column: "WorkId");

        migrationBuilder.CreateIndex(
            name: "IX_MediaProgress_WorkEpisodeId",
            table: "MediaProgress",
            column: "WorkEpisodeId");

        migrationBuilder.CreateIndex(
            name: "IX_MediaPlaybackHistory_ProfileId_LastPlayedAt",
            table: "MediaPlaybackHistory",
            columns: new[] { "ProfileId", "LastPlayedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_MediaPlaybackHistory_WorkId",
            table: "MediaPlaybackHistory",
            column: "WorkId");

        migrationBuilder.CreateIndex(
            name: "IX_MediaPlaybackHistory_WorkEpisodeId",
            table: "MediaPlaybackHistory",
            column: "WorkEpisodeId");

        migrationBuilder.CreateIndex(
            name: "IX_ActiveSessions_ProfileId_LastUpdatedAt",
            table: "ActiveSessions",
            columns: new[] { "ProfileId", "LastUpdatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_ActiveSessions_ProfileId_EndedAt",
            table: "ActiveSessions",
            columns: new[] { "ProfileId", "EndedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_ActiveSessions_WorkId",
            table: "ActiveSessions",
            column: "WorkId");

        migrationBuilder.CreateIndex(
            name: "IX_ActiveSessions_WorkEpisodeId",
            table: "ActiveSessions",
            column: "WorkEpisodeId");

        migrationBuilder.CreateIndex(
            name: "IX_ActiveSessions_MediaAssetId",
            table: "ActiveSessions",
            column: "MediaAssetId");

        migrationBuilder.CreateIndex(
            name: "IX_ActiveSessions_StoredFileId",
            table: "ActiveSessions",
            column: "StoredFileId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ActiveSessions");
        migrationBuilder.DropTable(name: "MediaPlaybackHistory");
        migrationBuilder.DropTable(name: "MediaProgress");
    }
}
