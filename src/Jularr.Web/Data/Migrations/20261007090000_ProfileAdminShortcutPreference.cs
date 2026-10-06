using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

/// <summary>
/// The per-profile switch for the permanent Admin shortcut in the sidebar. <c>UiProfileThemes</c> is a raw-SQL table outside the EF
/// model (see the baseline), so only the table changes and the model snapshot stays as it is.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261007090000_ProfileAdminShortcutPreference")]
public partial class ProfileAdminShortcutPreference : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE \"UiProfileThemes\" ADD COLUMN \"ShowAdminShortcut\" boolean NOT NULL DEFAULT true;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE \"UiProfileThemes\" DROP COLUMN \"ShowAdminShortcut\";");
    }
}
