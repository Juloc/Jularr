using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261009150000_AccountLoginIdentities")]
public sealed class AccountLoginIdentities : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AccountLoginIdentities",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AccountId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ExternalAccountId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                LinkedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AccountLoginIdentities", x => x.Id);
                table.ForeignKey(
                    name: "FK_AccountLoginIdentities_OwnerAccounts_AccountId",
                    column: x => x.AccountId,
                    principalTable: "OwnerAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AccountLoginIdentities_AccountId_Provider",
            table: "AccountLoginIdentities",
            columns: new[] { "AccountId", "Provider" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AccountLoginIdentities_Provider_ExternalAccountId",
            table: "AccountLoginIdentities",
            columns: new[] { "Provider", "ExternalAccountId" },
            unique: true);
        migrationBuilder.CreateTable(
            name: "PlexLoginAttempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PinId = table.Column<long>(type: "bigint", nullable: false),
                ClientIdentifier = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                BrowserNonceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                StartedAccountId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                VerifiedPlexAccountId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                ReturnPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PlexLoginAttempts", x => x.Id);
                table.ForeignKey(
                    name: "FK_PlexLoginAttempts_OwnerAccounts_StartedAccountId",
                    column: x => x.StartedAccountId,
                    principalTable: "OwnerAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PlexLoginAttempts_ExpiresAtUtc",
            table: "PlexLoginAttempts",
            column: "ExpiresAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_PlexLoginAttempts_StartedAccountId",
            table: "PlexLoginAttempts",
            column: "StartedAccountId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PlexLoginAttempts");
        migrationBuilder.DropTable(name: "AccountLoginIdentities");
    }
}
