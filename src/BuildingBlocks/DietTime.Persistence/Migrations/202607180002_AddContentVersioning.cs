using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DietTime.Persistence.Migrations;

[DbContext(typeof(DietTimeDbContext))]
[Migration("202607180002_AddContentVersioning")]
public sealed class AddContentVersioning : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE meal_items ADD COLUMN IF NOT EXISTS version_group_id uuid NOT NULL DEFAULT gen_random_uuid();
        ALTER TABLE meal_items ADD COLUMN IF NOT EXISTS version_number integer NOT NULL DEFAULT 1;
        ALTER TABLE meal_items ADD COLUMN IF NOT EXISTS is_latest boolean NOT NULL DEFAULT true;
        ALTER TABLE meal_items ADD COLUMN IF NOT EXISTS supersedes_id uuid NULL;
        ALTER TABLE meal_items DROP CONSTRAINT IF EXISTS meal_items_sku_key;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_meal_items_version ON meal_items(version_group_id, version_number);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_meal_items_latest_sku ON meal_items(sku) WHERE is_latest = true;
        ALTER TABLE meal_items ADD CONSTRAINT fk_meal_items_supersedes FOREIGN KEY (supersedes_id) REFERENCES meal_items(id) ON DELETE SET NULL;

        ALTER TABLE meal_plan_templates ADD COLUMN IF NOT EXISTS version_group_id uuid NOT NULL DEFAULT gen_random_uuid();
        ALTER TABLE meal_plan_templates ADD COLUMN IF NOT EXISTS version_number integer NOT NULL DEFAULT 1;
        ALTER TABLE meal_plan_templates ADD COLUMN IF NOT EXISTS is_latest boolean NOT NULL DEFAULT true;
        ALTER TABLE meal_plan_templates ADD COLUMN IF NOT EXISTS supersedes_id uuid NULL;
        ALTER TABLE meal_plan_templates DROP CONSTRAINT IF EXISTS meal_plan_templates_code_key;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_meal_plan_templates_version ON meal_plan_templates(version_group_id, version_number);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_meal_plan_templates_latest_code ON meal_plan_templates(code) WHERE is_latest = true;
        ALTER TABLE meal_plan_templates ADD CONSTRAINT fk_meal_plan_templates_supersedes FOREIGN KEY (supersedes_id) REFERENCES meal_plan_templates(id) ON DELETE SET NULL;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Content versioning cannot be rolled back without deleting historical versions.");
}
