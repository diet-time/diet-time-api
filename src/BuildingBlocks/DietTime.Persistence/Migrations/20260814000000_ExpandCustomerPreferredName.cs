using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DietTime.Persistence.Migrations;

[DbContext(typeof(DietTimeDbContext))]
[Migration("20260814000000_ExpandCustomerPreferredName")]
public partial class ExpandCustomerPreferredName : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AlterColumn<string>(
            name: "preferred_name",
            schema: "public",
            table: "customer_profiles",
            type: "character varying(150)",
            maxLength: 150,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(100)",
            oldMaxLength: 100,
            oldNullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AlterColumn<string>(
            name: "preferred_name",
            schema: "public",
            table: "customer_profiles",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(150)",
            oldMaxLength: 150,
            oldNullable: true);
}
