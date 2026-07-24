using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DietTime.Persistence.Migrations;

[DbContext(typeof(DietTimeDbContext))]
[Migration("202607240001_AddGuestPlanPresentation")]
public sealed class AddGuestPlanPresentation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("display_order", "meal_plan_templates", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>("image_url", "meal_plan_templates", maxLength: 2048, nullable: true);
        migrationBuilder.AddColumn<string>("icon_url", "meal_plan_templates", maxLength: 2048, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("display_order", "meal_plan_templates");
        migrationBuilder.DropColumn("image_url", "meal_plan_templates");
        migrationBuilder.DropColumn("icon_url", "meal_plan_templates");
    }
}
