using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DietTime.Persistence.Migrations;

[DbContext(typeof(DietTimeDbContext))]
[Migration("20260805000000_AddScreenPermissions")]
public partial class AddScreenPermissions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "can_read", table: "role_menu_mappings", type: "boolean", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<bool>(name: "can_write", table: "role_menu_mappings", type: "boolean", nullable: false, defaultValue: false);

        migrationBuilder.Sql("""
            INSERT INTO application_roles (id, role_name, description, is_active, created_at, updated_at, created_by)
            SELECT identity_role.id, identity_role.name, 'Identity role synchronized for access control', true, NOW(), NOW(), 'SYSTEM'
            FROM "AspNetRoles" identity_role
            WHERE identity_role.name IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM application_roles app_role WHERE UPPER(app_role.role_name) = UPPER(identity_role.name));

            INSERT INTO menus (id, main_menu_code, main_menu_name, sub_menu_code, sub_menu_name, route_url, icon, display_order, is_active, created_at, updated_at, created_by)
            VALUES
              ('10000000-0000-0000-0000-000000000001', 'GENERAL', 'General', 'DASHBOARD', 'Dashboard', '/', 'dashboard', 10, true, NOW(), NOW(), 'SYSTEM'),
              ('10000000-0000-0000-0000-000000000002', 'CATALOGUE', 'Catalogue', 'MEALS', 'Meals', '/meals', 'dinner_dining', 20, true, NOW(), NOW(), 'SYSTEM'),
              ('10000000-0000-0000-0000-000000000003', 'PLANS', 'Meal Plans', 'PLAN_TEMPLATES', 'Plan Templates', '/meal-plans', 'menu_book', 30, true, NOW(), NOW(), 'SYSTEM'),
              ('10000000-0000-0000-0000-000000000004', 'PLANS', 'Meal Plans', 'PLAN_PRICING', 'Plan Pricing', '/meal-plans/pricing', 'payments', 40, true, NOW(), NOW(), 'SYSTEM'),
              ('10000000-0000-0000-0000-000000000005', 'OPERATIONS', 'Operations', 'DELIVERY_CALENDAR', 'Delivery Calendar', '/operations/delivery-calendar', 'calendar_month', 50, true, NOW(), NOW(), 'SYSTEM'),
              ('10000000-0000-0000-0000-000000000006', 'OPERATIONS', 'Operations', 'CLOSURES', 'Holidays / Closures', '/operations/closures', 'event_busy', 60, true, NOW(), NOW(), 'SYSTEM'),
              ('10000000-0000-0000-0000-000000000007', 'MASTER_DATA', 'Master Data', 'CATEGORIES', 'Categories', '/categories', 'category', 70, true, NOW(), NOW(), 'SYSTEM'),
              ('10000000-0000-0000-0000-000000000008', 'MASTER_DATA', 'Master Data', 'INGREDIENTS', 'Ingredients', '/ingredients', 'spa', 80, true, NOW(), NOW(), 'SYSTEM'),
              ('10000000-0000-0000-0000-000000000009', 'MASTER_DATA', 'Master Data', 'ALLERGENS', 'Allergens', '/allergens', 'warning', 90, true, NOW(), NOW(), 'SYSTEM'),
              ('10000000-0000-0000-0000-000000000010', 'MASTER_DATA', 'Master Data', 'MEAL_TYPES', 'Meal Types', '/meal-types', 'restaurant_menu', 100, true, NOW(), NOW(), 'SYSTEM'),
              ('10000000-0000-0000-0000-000000000011', 'FINANCE', 'Finance', 'PRICING', 'Pricing', '/pricing', 'payments', 110, true, NOW(), NOW(), 'SYSTEM'),
              ('10000000-0000-0000-0000-000000000012', 'CONTENT', 'Content', 'MEDIA', 'Media', '/media', 'image', 120, true, NOW(), NOW(), 'SYSTEM'),
              ('10000000-0000-0000-0000-000000000013', 'SYSTEM', 'System', 'AUDIT', 'Audit History', '/audit', 'history', 130, true, NOW(), NOW(), 'SYSTEM'),
              ('10000000-0000-0000-0000-000000000014', 'SYSTEM', 'System', 'SETTINGS', 'Settings', '/settings', 'settings', 140, true, NOW(), NOW(), 'SYSTEM'),
              ('10000000-0000-0000-0000-000000000015', 'SYSTEM', 'System', 'ACCESS_CONTROL', 'Users & Roles', '/access-control', 'admin_panel_settings', 150, true, NOW(), NOW(), 'SYSTEM')
            ON CONFLICT (main_menu_code, sub_menu_code) DO UPDATE SET
              main_menu_name = EXCLUDED.main_menu_name,
              sub_menu_name = EXCLUDED.sub_menu_name,
              route_url = EXCLUDED.route_url,
              icon = EXCLUDED.icon,
              display_order = EXCLUDED.display_order,
              is_active = true,
              updated_at = NOW();

            INSERT INTO role_menu_mappings (id, role_id, menu_id, can_read, can_write, created_at)
            SELECT ('20000000-0000-0000-0000-' || LPAD(ROW_NUMBER() OVER (ORDER BY m.display_order)::text, 12, '0'))::uuid,
                   r.id, m.id, true, true, NOW()
            FROM application_roles r CROSS JOIN menus m
            WHERE UPPER(r.role_name) = 'ADMIN'
            ON CONFLICT (role_id, menu_id) DO UPDATE SET can_read = true, can_write = true;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DELETE FROM menus WHERE id::text LIKE '10000000-0000-0000-0000-%';");
        migrationBuilder.Sql("DELETE FROM application_roles WHERE description = 'Identity role synchronized for access control';");
        migrationBuilder.DropColumn(name: "can_read", table: "role_menu_mappings");
        migrationBuilder.DropColumn(name: "can_write", table: "role_menu_mappings");
    }
}
