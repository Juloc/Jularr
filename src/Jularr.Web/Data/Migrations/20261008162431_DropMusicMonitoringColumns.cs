using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropMusicMonitoringColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MusicAlbums_ArtistId_Monitored",
                table: "MusicAlbums");

            migrationBuilder.DropColumn(
                name: "Monitor",
                table: "MusicArtists");

            migrationBuilder.DropColumn(
                name: "MonitorFromUtc",
                table: "MusicArtists");

            migrationBuilder.DropColumn(
                name: "Monitored",
                table: "MusicAlbums");

            migrationBuilder.CreateIndex(
                name: "IX_MusicAlbums_ArtistId",
                table: "MusicAlbums",
                column: "ArtistId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MusicAlbums_ArtistId",
                table: "MusicAlbums");

            migrationBuilder.AddColumn<int>(
                name: "Monitor",
                table: "MusicArtists",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "MonitorFromUtc",
                table: "MusicArtists",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<bool>(
                name: "Monitored",
                table: "MusicAlbums",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_MusicAlbums_ArtistId_Monitored",
                table: "MusicAlbums",
                columns: new[] { "ArtistId", "Monitored" });
        }
    }
}
