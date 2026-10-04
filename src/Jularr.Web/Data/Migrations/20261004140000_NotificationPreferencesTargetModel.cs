using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261004140000_NotificationPreferencesTargetModel")]
public partial class NotificationPreferencesTargetModel : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "Enabled",
            table: "NotificationSubscriptions",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<int>(
            name: "Timing",
            table: "NotificationSubscriptions",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.CreateTable(
            name: "NotificationSubscriptionChannels",
            columns: table => new
            {
                ProfileId = table.Column<string>(type: "text", nullable: false),
                Category = table.Column<int>(type: "integer", nullable: false),
                Channel = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NotificationSubscriptionChannels", row => new { row.ProfileId, row.Category, row.Channel });
                table.CheckConstraint("CK_NotificationSubscriptionChannels_Channel", "\"Channel\" >= 1 AND \"Channel\" <= 3");
                table.ForeignKey(
                    name: "FK_NotificationSubscriptionChannels_NotificationSubscriptions",
                    columns: row => new { row.ProfileId, row.Category },
                    principalTable: "NotificationSubscriptions",
                    principalColumns: new[] { "ProfileId", "Category" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "NotificationProfileChannels",
            columns: table => new
            {
                ProfileId = table.Column<string>(type: "text", nullable: false),
                Channel = table.Column<int>(type: "integer", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NotificationProfileChannels", row => new { row.ProfileId, row.Channel });
                table.CheckConstraint("CK_NotificationProfileChannels_Channel", "\"Channel\" >= 1 AND \"Channel\" <= 3");
            });

        migrationBuilder.Sql(
            """
            UPDATE "NotificationSubscriptions"
            SET "Enabled" = CASE WHEN "Mode" IN (1, 2) THEN TRUE ELSE FALSE END,
                "Timing" = CASE WHEN "Mode" = 3 THEN 2 ELSE 1 END;
            """);

        migrationBuilder.Sql(
            """
            INSERT INTO "NotificationSubscriptionChannels" ("ProfileId", "Category", "Channel")
            SELECT "ProfileId", "Category",
                   CASE WHEN "Mode" = 2 THEN 2 ELSE 1 END
            FROM "NotificationSubscriptions"
            ON CONFLICT ("ProfileId", "Category", "Channel") DO NOTHING;
            """);

        migrationBuilder.Sql(
            """
            INSERT INTO "NotificationProfileChannels" ("ProfileId", "Channel", "Enabled", "UpdatedAtUtc")
            SELECT DISTINCT "ProfileId", 2, TRUE, CURRENT_TIMESTAMP
            FROM "NotificationSubscriptions"
            WHERE "Mode" = 2
            ON CONFLICT ("ProfileId", "Channel") DO NOTHING;
            """);

        migrationBuilder.DropColumn(name: "Mode", table: "NotificationSubscriptions");

        migrationBuilder.Sql(
            """
            ALTER TABLE "NotificationSubscriptions"
                ALTER COLUMN "Enabled" DROP DEFAULT,
                ALTER COLUMN "Timing" DROP DEFAULT;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Mode",
            table: "NotificationSubscriptions",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.Sql(
            """
            UPDATE "NotificationSubscriptions" AS subscription
            SET "Mode" =
                CASE
                    WHEN NOT subscription."Enabled" THEN 0
                    WHEN subscription."Timing" = 2 THEN 3
                    WHEN EXISTS (
                        SELECT 1
                        FROM "NotificationSubscriptionChannels" AS channel
                        WHERE channel."ProfileId" = subscription."ProfileId"
                          AND channel."Category" = subscription."Category"
                          AND channel."Channel" = 1) THEN 1
                    WHEN EXISTS (
                        SELECT 1
                        FROM "NotificationSubscriptionChannels" AS channel
                        WHERE channel."ProfileId" = subscription."ProfileId"
                          AND channel."Category" = subscription."Category"
                          AND channel."Channel" = 2) THEN 2
                    ELSE 0
                END;
            """);

        migrationBuilder.Sql(
            """
            ALTER TABLE "NotificationSubscriptions"
                ALTER COLUMN "Mode" DROP DEFAULT;
            """);

        migrationBuilder.DropTable(name: "NotificationSubscriptionChannels");
        migrationBuilder.DropTable(name: "NotificationProfileChannels");
        migrationBuilder.DropColumn(name: "Enabled", table: "NotificationSubscriptions");
        migrationBuilder.DropColumn(name: "Timing", table: "NotificationSubscriptions");
    }
}
