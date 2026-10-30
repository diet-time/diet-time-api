using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace DietTime.Persistence.Migrations;
[DbContext(typeof(DietTimeDbContext))]
[Migration("20261030020000_StaffOrderApproval")]
public sealed class StaffOrderApproval : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
-- Run as schema owner before deploying the API. Historical statuses/payment values are not rewritten.

SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '60s';
ALTER TABLE public.orders ADD COLUMN IF NOT EXISTS requires_prepayment boolean NOT NULL DEFAULT FALSE;
ALTER TABLE public.orders ADD COLUMN IF NOT EXISTS fulfillment_blocked_reason varchar(500);
ALTER TABLE public.orders ADD COLUMN IF NOT EXISTS approved_by uuid;
ALTER TABLE public.orders ADD COLUMN IF NOT EXISTS approved_at timestamptz;
ALTER TABLE public.orders ADD COLUMN IF NOT EXISTS rejected_by uuid;
ALTER TABLE public.orders ADD COLUMN IF NOT EXISTS rejected_at timestamptz;
ALTER TABLE public.orders ADD COLUMN IF NOT EXISTS rejection_reason varchar(500);
ALTER TABLE public.orders ADD COLUMN IF NOT EXISTS hold_reason varchar(500);
ALTER TABLE public.orders ALTER COLUMN status SET DEFAULT 'PENDING_APPROVAL';
ALTER TABLE public.orders DROP CONSTRAINT IF EXISTS ck_orders_status;
ALTER TABLE public.orders ADD CONSTRAINT ck_orders_status CHECK (status IN
 ('PENDING_APPROVAL','ON_HOLD','CONFIRMED','REJECTED','CANCELLED','ACTIVE','COMPLETED',
  'PENDING','PAUSED','PENDING_ACTIVATION')) NOT VALID;
ALTER TABLE public.orders DROP CONSTRAINT IF EXISTS ck_orders_payment_status;
ALTER TABLE public.orders ADD CONSTRAINT ck_orders_payment_status CHECK (payment_status IN
 ('UNPAID','PENDING','PAID','FAILED','REFUND_PENDING','REFUNDED','PARTIALLY_REFUNDED')) NOT VALID;
-- NOT VALID retains unknown historical codes without rewriting them; all new writes are checked.
ALTER TABLE public.order_status_history ADD COLUMN IF NOT EXISTS previous_status varchar(30);
ALTER TABLE public.order_status_history ADD COLUMN IF NOT EXISTS changed_by uuid;
ALTER TABLE public.order_status_history ADD COLUMN IF NOT EXISTS previous_payment_status varchar(30);
ALTER TABLE public.order_status_history ADD COLUMN IF NOT EXISTS payment_status varchar(30);
CREATE TABLE IF NOT EXISTS public.order_notifications (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), order_id uuid NOT NULL REFERENCES public.orders(id) ON DELETE CASCADE,
 history_id uuid NOT NULL REFERENCES public.order_status_history(id) ON DELETE CASCADE,
 event_code varchar(40) NOT NULL, audience varchar(20) NOT NULL, reason varchar(500),
 status varchar(30) NOT NULL DEFAULT 'PENDING', attempts integer NOT NULL DEFAULT 0,
 available_at timestamptz NOT NULL DEFAULT now(), locked_until timestamptz, created_at timestamptz NOT NULL DEFAULT now(),
 sent_at timestamptz, message_sid varchar(50), last_error varchar(500)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_order_notifications_history_id_audience ON public.order_notifications(history_id,audience);
ALTER TABLE public.order_notifications ALTER COLUMN status TYPE varchar(30);
CREATE INDEX IF NOT EXISTS ix_order_notifications_status_available_at ON public.order_notifications(status,available_at);
-- Reuse the existing access-control catalogue; only administrators receive review access.
INSERT INTO public.menus(id,main_menu_code,main_menu_name,sub_menu_code,sub_menu_name,route_url,icon,display_order,is_active,created_at,updated_at,created_by)
 VALUES ('10000000-0000-0000-0000-000000000031','OPERATIONS','Operations','ORDERS','Orders','/orders','receipt_long',45,true,now(),now(),'SYSTEM')
 ON CONFLICT(main_menu_code,sub_menu_code) DO NOTHING;
INSERT INTO public.role_menu_mappings(id,role_id,menu_id,can_read,can_write,created_at)
 SELECT gen_random_uuid(),r.id,m.id,true,true,now() FROM public.application_roles r CROSS JOIN public.menus m
 WHERE upper(r.role_name)='ADMIN' AND m.main_menu_code='OPERATIONS' AND m.sub_menu_code='ORDERS'
 ON CONFLICT(role_id,menu_id) DO NOTHING;


""");
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Approval history and notification events require a reviewed forward migration.");
}
