using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DietTime.Persistence.Migrations;

[DbContext(typeof(DietTimeDbContext))]
[Migration("20261030010000_AddTakeawayBranches")]
public sealed class AddTakeawayBranches : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
-- Run as schema owner before deploying the API. Safe to rerun; never rewrites orders.

SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '60s';
CREATE TABLE IF NOT EXISTS public.takeaway_branches (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name varchar(200) NOT NULL,
    address varchar(500),
    is_active boolean NOT NULL DEFAULT TRUE,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    created_by uuid,
    updated_by uuid
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_takeaway_branches_name ON public.takeaway_branches(name);
INSERT INTO public.takeaway_branches (id, name) VALUES
    ('e159eace-7dfc-4ad1-8431-a4de27563821', 'Diet Time – Izghawa Branch'),
    ('e159eace-7dfc-4ad1-8431-a4de27563822', 'Diet Time – Azizia Branch')
ON CONFLICT DO NOTHING;
-- Unknown addresses intentionally remain NULL; administrators can configure them later.
ALTER TABLE public.orders ADD COLUMN IF NOT EXISTS takeaway_branch_id uuid;
ALTER TABLE public.orders ADD COLUMN IF NOT EXISTS takeaway_branch_name varchar(200);
ALTER TABLE public.orders ADD COLUMN IF NOT EXISTS takeaway_branch_address varchar(500);
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public.orders'::regclass AND conname = 'fk_orders_takeaway_branches') THEN
        ALTER TABLE public.orders ADD CONSTRAINT fk_orders_takeaway_branches
            FOREIGN KEY (takeaway_branch_id) REFERENCES public.takeaway_branches(id) ON DELETE RESTRICT NOT VALID;
    END IF;
END $$;
ALTER TABLE public.orders VALIDATE CONSTRAINT fk_orders_takeaway_branches;
CREATE INDEX IF NOT EXISTS ix_orders_takeaway_branch_id ON public.orders(takeaway_branch_id);
""");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Preserve order branch snapshots; use a reviewed forward migration.");
}
