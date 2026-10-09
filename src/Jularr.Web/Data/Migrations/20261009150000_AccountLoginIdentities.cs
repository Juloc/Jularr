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
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AccountLoginIdentities");
    }
}
