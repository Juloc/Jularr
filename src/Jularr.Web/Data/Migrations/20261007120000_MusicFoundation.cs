using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class MusicFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LibraryRootContentAssignments_ContentType",
                table: "LibraryRootContentAssignments");

            migrationBuilder.AddColumn<Guid>(
                name: "WorkTrackId",
                table: "MediaAssets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MusicArtists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SortName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    MusicBrainzId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Monitor = table.Column<int>(type: "integer", nullable: false),
                    MonitorFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRefreshedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicArtists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkTracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: false),
                    Disc = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    MusicBrainzRecordingId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkTracks_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MusicAlbums",
                columns: table => new
                {
                    WorkId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtistId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    ReleaseDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MusicBrainzReleaseGroupId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Monitored = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicAlbums", x => x.WorkId);
                    table.ForeignKey(
                        name: "FK_MusicAlbums_MusicArtists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "MusicArtists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MusicAlbums_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_WorkTrackId",
                table: "MediaAssets",
                column: "WorkTrackId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LibraryRootContentAssignments_ContentType",
                table: "LibraryRootContentAssignments",
                sql: "\"ContentType\" >= 1 AND \"ContentType\" <= 9");

            migrationBuilder.CreateIndex(
                name: "IX_MusicAlbums_ArtistId_Monitored",
                table: "MusicAlbums",
                columns: new[] { "ArtistId", "Monitored" });

            migrationBuilder.CreateIndex(
                name: "IX_MusicAlbums_MusicBrainzReleaseGroupId",
                table: "MusicAlbums",
                column: "MusicBrainzReleaseGroupId",
                unique: true,
                filter: "\"MusicBrainzReleaseGroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MusicArtists_MusicBrainzId",
                table: "MusicArtists",
                column: "MusicBrainzId",
                unique: true,
                filter: "\"MusicBrainzId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MusicArtists_SortName",
                table: "MusicArtists",
                column: "SortName");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTracks_WorkId_Disc_Number",
                table: "WorkTracks",
                columns: new[] { "WorkId", "Disc", "Number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MediaAssets_WorkTracks_WorkTrackId",
                table: "MediaAssets",
                column: "WorkTrackId",
                principalTable: "WorkTracks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaAssets_WorkTracks_WorkTrackId",
                table: "MediaAssets");

            migrationBuilder.DropTable(
                name: "MusicAlbums");

            migrationBuilder.DropTable(
                name: "WorkTracks");

            migrationBuilder.DropTable(
                name: "MusicArtists");

            migrationBuilder.DropIndex(
                name: "IX_MediaAssets_WorkTrackId",
                table: "MediaAssets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LibraryRootContentAssignments_ContentType",
                table: "LibraryRootContentAssignments");

            migrationBuilder.DropColumn(
                name: "WorkTrackId",
                table: "MediaAssets");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LibraryRootContentAssignments_ContentType",
                table: "LibraryRootContentAssignments",
                sql: "\"ContentType\" >= 1 AND \"ContentType\" <= 8");
        }
    }
}
