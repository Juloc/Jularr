using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class GamesFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GamePlatforms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GamePlatforms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Games",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CanonicalTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Developer = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Publisher = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ReleaseYear = table.Column<int>(type: "integer", nullable: true),
                    ReleaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Games", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GameArtworks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Uri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameArtworks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameArtworks_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "GameExternalIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastVerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameExternalIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameExternalIdentities_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "GameTitles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    LanguageTag = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameTitles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameTitles_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "GameReleases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    GamePlatformId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Region = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Revision = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Version = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Source = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Format = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    LanguageTags = table.Column<string[]>(type: "text[]", nullable: false),
                    ReleaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameReleases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameReleases_GamePlatforms_GamePlatformId",
                        column: x => x.GamePlatformId,
                        principalTable: "GamePlatforms",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_GameReleases_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "GameReleaseFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    LibraryRootId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelativePath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    DiscNumber = table.Column<int>(type: "integer", nullable: true),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    Crc32 = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Md5 = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Sha1 = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameReleaseFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameReleaseFiles_GameReleases_GameReleaseId",
                        column: x => x.GameReleaseId,
                        principalTable: "GameReleases",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_GameReleaseFiles_LibraryRoots_LibraryRootId",
                        column: x => x.LibraryRootId,
                        principalTable: "LibraryRoots",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "GameReleaseHashes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Algorithm = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameReleaseHashes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameReleaseHashes_GameReleases_GameReleaseId",
                        column: x => x.GameReleaseId,
                        principalTable: "GameReleases",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(name: "IX_GameArtworks_GameId_Kind", table: "GameArtworks", columns: new[] { "GameId", "Kind" });
            migrationBuilder.CreateIndex(name: "IX_GameExternalIdentities_GameId", table: "GameExternalIdentities", column: "GameId");
            migrationBuilder.CreateIndex(name: "IX_GameExternalIdentities_Provider_ExternalId", table: "GameExternalIdentities", columns: new[] { "Provider", "ExternalId" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_GamePlatforms_Key", table: "GamePlatforms", column: "Key", unique: true);
            migrationBuilder.CreateIndex(name: "IX_GameReleaseFiles_GameReleaseId_Sequence", table: "GameReleaseFiles", columns: new[] { "GameReleaseId", "Sequence" });
            migrationBuilder.CreateIndex(name: "IX_GameReleaseFiles_LibraryRootId_RelativePath", table: "GameReleaseFiles", columns: new[] { "LibraryRootId", "RelativePath" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_GameReleaseHashes_Algorithm_Value", table: "GameReleaseHashes", columns: new[] { "Algorithm", "Value" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_GameReleaseHashes_GameReleaseId", table: "GameReleaseHashes", column: "GameReleaseId");
            migrationBuilder.CreateIndex(name: "IX_GameReleases_GameId_GamePlatformId", table: "GameReleases", columns: new[] { "GameId", "GamePlatformId" });
            migrationBuilder.CreateIndex(name: "IX_GameReleases_GamePlatformId", table: "GameReleases", column: "GamePlatformId");
            migrationBuilder.CreateIndex(name: "IX_Games_CanonicalTitle", table: "Games", column: "CanonicalTitle");
            migrationBuilder.CreateIndex(name: "IX_GameTitles_GameId_Value", table: "GameTitles", columns: new[] { "GameId", "Value" }, unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "GameArtworks");
            migrationBuilder.DropTable(name: "GameExternalIdentities");
            migrationBuilder.DropTable(name: "GameReleaseFiles");
            migrationBuilder.DropTable(name: "GameReleaseHashes");
            migrationBuilder.DropTable(name: "GameTitles");
            migrationBuilder.DropTable(name: "GameReleases");
            migrationBuilder.DropTable(name: "GamePlatforms");
            migrationBuilder.DropTable(name: "Games");
        }
    }
}
