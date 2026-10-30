using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DietTime.Persistence.Migrations;

[DbContext(typeof(DietTimeDbContext))]
[Migration("20261030000000_EnablePackageDelivery")]
public sealed class EnablePackageDelivery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
-- Apply before deploying the API and clients. Run as the schema owner.
-- Transactional and safe to rerun: package defaults are seeded only on first addition.

SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '60s';

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'meal_plan_price_packages'
          AND column_name = 'is_delivery_enabled') THEN
        ALTER TABLE public.meal_plan_price_packages
            ADD COLUMN is_delivery_enabled boolean NOT NULL DEFAULT TRUE;
        UPDATE public.meal_plan_price_packages SET is_delivery_enabled = FALSE WHERE duration_days = 6;
        -- All other existing durations, including 12 and 24 days, retain TRUE.
    END IF;
END $$;

ALTER TABLE public.orders
    ADD COLUMN IF NOT EXISTS fulfillment_method varchar(30) NOT NULL DEFAULT 'HOME_DELIVERY';
ALTER TABLE public.orders ALTER COLUMN customer_address_id DROP NOT NULL;
ALTER TABLE public.orders ALTER COLUMN delivery_time_slot_id DROP NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public.orders'::regclass
        AND conname = 'ck_orders_fulfillment_method') THEN
        ALTER TABLE public.orders ADD CONSTRAINT ck_orders_fulfillment_method
            CHECK (fulfillment_method IN ('HOME_DELIVERY', 'TAKEAWAY')) NOT VALID;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public.orders'::regclass
        AND conname = 'ck_orders_home_delivery_details') THEN
        ALTER TABLE public.orders ADD CONSTRAINT ck_orders_home_delivery_details
            CHECK (fulfillment_method <> 'HOME_DELIVERY' OR
                (customer_address_id IS NOT NULL AND delivery_time_slot_id IS NOT NULL)) NOT VALID;
    END IF;
END $$;

ALTER TABLE public.orders VALIDATE CONSTRAINT ck_orders_fulfillment_method;
ALTER TABLE public.orders VALIDATE CONSTRAINT ck_orders_home_delivery_details;


""");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Fulfillment snapshots must be preserved. Use a reviewed forward migration.");
}
