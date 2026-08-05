using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DietTime.Persistence.Migrations;

[DbContext(typeof(DietTimeDbContext))]
[Migration("20260805020000_SplitRoleAndUserScreens")]
public partial class SplitRoleAndUserScreens : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE menus
            SET sub_menu_code = 'ROLES', sub_menu_name = 'Roles', route_url = '/roles',
                icon = 'account_tree', display_order = 150, updated_at = NOW()
            WHERE main_menu_code = 'SYSTEM' AND sub_menu_code = 'ACCESS_CONTROL';

            INSERT INTO menus (id, main_menu_code, main_menu_name, sub_menu_code, sub_menu_name, route_url, icon, display_order, is_active, created_at, updated_at, created_by)
            VALUES ('10000000-0000-0000-0000-000000000016', 'SYSTEM', 'System', 'USERS', 'Users', '/users', 'people', 160, true, NOW(), NOW(), 'SYSTEM')
            ON CONFLICT (main_menu_code, sub_menu_code) DO UPDATE SET
              sub_menu_name = EXCLUDED.sub_menu_name, route_url = EXCLUDED.route_url,
              icon = EXCLUDED.icon, display_order = EXCLUDED.display_order,
              is_active = true, updated_at = NOW();

            INSERT INTO role_menu_mappings (id, role_id, menu_id, can_read, can_write, created_at)
            SELECT (md5(app_role.id::text || menu.id::text))::uuid,
                   app_role.id, menu.id, true, true, NOW()
            FROM application_roles app_role CROSS JOIN menus menu
            WHERE UPPER(app_role.role_name) = 'ADMIN'
            ON CONFLICT (role_id, menu_id) DO UPDATE SET can_read = true, can_write = true;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DELETE FROM menus WHERE id = '10000000-0000-0000-0000-000000000016';");
        migrationBuilder.Sql("""
            UPDATE menus
            SET sub_menu_code = 'ACCESS_CONTROL', sub_menu_name = 'Users & Roles',
                route_url = '/access-control', icon = 'admin_panel_settings', display_order = 150, updated_at = NOW()
            WHERE main_menu_code = 'SYSTEM' AND sub_menu_code = 'ROLES';
            """);
    }
}
