using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DietTime.Persistence.Migrations;

[DbContext(typeof(DietTimeDbContext))]
[Migration("20260805010000_EnsureAdminFullAccess")]
public partial class EnsureAdminFullAccess : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO application_roles (id, role_name, description, is_active, created_at, updated_at, created_by)
            SELECT identity_role.id, identity_role.name, 'Identity role synchronized for access control', true, NOW(), NOW(), 'SYSTEM'
            FROM "AspNetRoles" identity_role
            WHERE identity_role.normalized_name = 'ADMIN'
              AND NOT EXISTS (SELECT 1 FROM application_roles app_role WHERE UPPER(app_role.role_name) = 'ADMIN');

            INSERT INTO "AspNetUserRoles" (user_id, role_id)
            SELECT identity_user.id, identity_role.id
            FROM "AspNetUsers" identity_user
            CROSS JOIN "AspNetRoles" identity_role
            WHERE identity_user.normalized_email = 'ADMIN@DIETTIME.LOCAL'
              AND identity_role.normalized_name = 'ADMIN'
            ON CONFLICT (user_id, role_id) DO NOTHING;

            INSERT INTO role_menu_mappings (id, role_id, menu_id, can_read, can_write, created_at)
            SELECT (md5(app_role.id::text || menu.id::text))::uuid,
                   app_role.id, menu.id, true, true, NOW()
            FROM application_roles app_role
            CROSS JOIN menus menu
            WHERE UPPER(app_role.role_name) = 'ADMIN'
            ON CONFLICT (role_id, menu_id) DO UPDATE
              SET can_read = true, can_write = true;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Admin access is intentionally retained on rollback to avoid locking out administrators.
    }
}
