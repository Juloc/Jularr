using System;
using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jularr.Web.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20261006002056_AddWorkMetadataAndArtwork")]
    public partial class AddWorkMetadataAndArtwork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkArtwork",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slot = table.Column<int>(type: "integer", nullable: false),
                    Language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Source = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ProviderFilePath = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    VoteAverage = table.Column<double>(type: "double precision", nullable: true),
                    VoteCount = table.Column<int>(type: "integer", nullable: true),
                    CacheKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    IsManualOverride = table.Column<bool>(type: "boolean", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CachedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkArtwork", x => x.Id);
                    table.CheckConstraint("CK_WorkArtwork_CacheKey", "\"CacheKey\" IS NULL OR \"CacheKey\" ~ '^[0-9a-f]{32}$'");
                    table.CheckConstraint("CK_WorkArtwork_Dimensions", "(\"Width\" IS NULL OR \"Width\" > 0) AND (\"Height\" IS NULL OR \"Height\" > 0)");
                    table.ForeignKey(
                        name: "FK_WorkArtwork_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkCredits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Role = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Source = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ProviderPersonId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkCredits", x => x.Id);
                    table.CheckConstraint("CK_WorkCredits_Position", "\"Position\" >= 0");
                    table.ForeignKey(
                        name: "FK_WorkCredits_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkLocalizedValues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: false),
                    Locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    Field = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Origin = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SourceLocale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    ProviderExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    FallbackPriority = table.Column<int>(type: "integer", nullable: false),
                    IsManualOverride = table.Column<bool>(type: "boolean", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkLocalizedValues", x => x.Id);
                    table.CheckConstraint("CK_WorkLocalizedValues_Locale", "length(\"Locale\") > 0");
                    table.CheckConstraint("CK_WorkLocalizedValues_Position", "\"Position\" >= 0");
                    table.ForeignKey(
                        name: "FK_WorkLocalizedValues_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkMetadataFacts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalTitle = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    OriginalLanguage = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ReleaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RuntimeMinutes = table.Column<int>(type: "integer", nullable: true),
                    Rating = table.Column<double>(type: "double precision", nullable: true),
                    RatingCount = table.Column<int>(type: "integer", nullable: true),
                    Certification = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CertificationCountry = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    Studios = table.Column<string[]>(type: "text[]", nullable: false),
                    ProductionCountries = table.Column<string[]>(type: "text[]", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkMetadataFacts", x => x.Id);
                    table.CheckConstraint("CK_WorkMetadataFacts_Rating", "\"Rating\" IS NULL OR (\"Rating\" >= 0 AND \"Rating\" <= 10)");
                    table.CheckConstraint("CK_WorkMetadataFacts_RatingCount", "\"RatingCount\" IS NULL OR \"RatingCount\" >= 0");
                    table.CheckConstraint("CK_WorkMetadataFacts_RuntimeMinutes", "\"RuntimeMinutes\" IS NULL OR \"RuntimeMinutes\" > 0");
                    table.ForeignKey(
                        name: "FK_WorkMetadataFacts_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkMetadataRefreshes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: false),
                    Locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSucceededAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkMetadataRefreshes", x => x.Id);
                    table.CheckConstraint("CK_WorkMetadataRefreshes_Attempts", "\"Attempts\" >= 0");
                    table.CheckConstraint("CK_WorkMetadataRefreshes_Locale", "length(\"Locale\") > 0");
                    table.ForeignKey(
                        name: "FK_WorkMetadataRefreshes_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkArtwork_WorkId_Slot_Language",
                table: "WorkArtwork",
                columns: new[] { "WorkId", "Slot", "Language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkCredits_WorkId_Kind_Position",
                table: "WorkCredits",
                columns: new[] { "WorkId", "Kind", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkLocalizedValues_WorkId_Locale_Field_Position",
                table: "WorkLocalizedValues",
                columns: new[] { "WorkId", "Locale", "Field", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkMetadataFacts_WorkId",
                table: "WorkMetadataFacts",
                column: "WorkId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkMetadataRefreshes_NextAttemptAt",
                table: "WorkMetadataRefreshes",
                column: "NextAttemptAt");

            migrationBuilder.CreateIndex(
                name: "IX_WorkMetadataRefreshes_WorkId_Locale",
                table: "WorkMetadataRefreshes",
                columns: new[] { "WorkId", "Locale" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkArtwork");

            migrationBuilder.DropTable(
                name: "WorkCredits");

            migrationBuilder.DropTable(
                name: "WorkLocalizedValues");

            migrationBuilder.DropTable(
                name: "WorkMetadataFacts");

            migrationBuilder.DropTable(
                name: "WorkMetadataRefreshes");
        }
    }
}
