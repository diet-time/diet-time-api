using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DietTime.Persistence.Migrations;

[DbContext(typeof(DietTimeDbContext))]
[Migration("20260805030000_GroupAdministrationMenus")]
public partial class GroupAdministrationMenus : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE menus
            SET main_menu_code = 'ADMINISTRATION', main_menu_name = 'Administration', updated_at = NOW()
            WHERE sub_menu_code IN ('SETTINGS', 'USERS', 'ROLES');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE menus
            SET main_menu_code = 'SYSTEM', main_menu_name = 'System', updated_at = NOW()
            WHERE sub_menu_code IN ('SETTINGS', 'USERS', 'ROLES');
            """);
    }
}
