using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class MusicRecordings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MusicRecordingId",
                table: "WorkTracks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MusicBrainzReleaseId",
                table: "MusicAlbums",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MusicRecordings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MusicBrainzId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicRecordings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTracks_MusicRecordingId",
                table: "WorkTracks",
                column: "MusicRecordingId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicRecordings_MusicBrainzId",
                table: "MusicRecordings",
                column: "MusicBrainzId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkTracks_MusicRecordings_MusicRecordingId",
                table: "WorkTracks",
                column: "MusicRecordingId",
                principalTable: "MusicRecordings",
                principalColumn: "Id");

            // The recording ids the tracks carried become recordings (one per id, however many albums place it) and the tracks point at them.
            migrationBuilder.Sql(
                """
                INSERT INTO "MusicRecordings" ("Id", "MusicBrainzId", "Title", "CreatedAt")
                SELECT gen_random_uuid(), track."MusicBrainzRecordingId", min(track."Title"), now()
                FROM "WorkTracks" track
                WHERE track."MusicBrainzRecordingId" IS NOT NULL AND track."MusicBrainzRecordingId" <> ''
                GROUP BY track."MusicBrainzRecordingId";

                UPDATE "WorkTracks" track
                SET "MusicRecordingId" = recording."Id"
                FROM "MusicRecordings" recording
                WHERE recording."MusicBrainzId" = track."MusicBrainzRecordingId";
                """);

            migrationBuilder.DropColumn(
                name: "MusicBrainzRecordingId",
                table: "WorkTracks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MusicBrainzRecordingId",
                table: "WorkTracks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "WorkTracks" track SET "MusicBrainzRecordingId" = recording."MusicBrainzId"
                FROM "MusicRecordings" recording WHERE recording."Id" = track."MusicRecordingId";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_WorkTracks_MusicRecordings_MusicRecordingId",
                table: "WorkTracks");

            migrationBuilder.DropTable(
                name: "MusicRecordings");

            migrationBuilder.DropIndex(
                name: "IX_WorkTracks_MusicRecordingId",
                table: "WorkTracks");

            migrationBuilder.DropColumn(
                name: "MusicRecordingId",
                table: "WorkTracks");

            migrationBuilder.DropColumn(
                name: "MusicBrainzReleaseId",
                table: "MusicAlbums");
        }
    }
}
