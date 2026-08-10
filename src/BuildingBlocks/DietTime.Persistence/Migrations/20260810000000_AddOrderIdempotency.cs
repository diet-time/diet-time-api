using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DietTime.Persistence.Migrations;

[DbContext(typeof(DietTimeDbContext))]
[Migration("20260810000000_AddOrderIdempotency")]
public partial class AddOrderIdempotency : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "idempotency_key",
            schema: "public",
            table: "orders",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE public.orders
            SET idempotency_key = 'legacy-' || id::text
            WHERE idempotency_key IS NULL;
            """);

        migrationBuilder.AlterColumn<string>(
            name: "idempotency_key",
            schema: "public",
            table: "orders",
            type: "character varying(100)",
            maxLength: 100,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(100)",
            oldMaxLength: 100,
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "ux_orders_idempotency_key",
            schema: "public",
            table: "orders",
            column: "idempotency_key",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ux_orders_idempotency_key",
            schema: "public",
            table: "orders");
        migrationBuilder.DropColumn(
            name: "idempotency_key",
            schema: "public",
            table: "orders");
    }
}
