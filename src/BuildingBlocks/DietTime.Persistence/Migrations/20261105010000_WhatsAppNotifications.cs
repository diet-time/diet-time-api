using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace DietTime.Persistence.Migrations;
[DbContext(typeof(DietTimeDbContext))]
[Migration("20261105010000_WhatsAppNotifications")]
public sealed class WhatsAppNotifications : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
SET LOCAL lock_timeout = '5s';
ALTER TABLE public.order_notifications ALTER COLUMN history_id DROP NOT NULL;
ALTER TABLE public.order_notifications
 ADD COLUMN IF NOT EXISTS event_key varchar(180),
 ADD COLUMN IF NOT EXISTS payload_json jsonb NOT NULL DEFAULT '{}',
 ADD COLUMN IF NOT EXISTS recipient_phone varchar(40),
 ADD COLUMN IF NOT EXISTS template_name varchar(100),
 ADD COLUMN IF NOT EXISTS content_sid varchar(64),
 ADD COLUMN IF NOT EXISTS provider_status varchar(30),
 ADD COLUMN IF NOT EXISTS delivered_at timestamptz,
 ADD COLUMN IF NOT EXISTS read_at timestamptz;
CREATE UNIQUE INDEX IF NOT EXISTS ix_order_notifications_event_key ON public.order_notifications(event_key) WHERE event_key IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ix_order_notifications_message_sid ON public.order_notifications(message_sid) WHERE message_sid IS NOT NULL;
CREATE TABLE IF NOT EXISTS public.whatsapp_webhook_receipts (
 id uuid PRIMARY KEY, message_sid varchar(50) NOT NULL, order_id uuid NOT NULL,
 actor_id uuid NOT NULL, action varchar(20) NOT NULL, outcome varchar(100) NOT NULL, created_at timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_whatsapp_webhook_receipts_message_sid ON public.whatsapp_webhook_receipts(message_sid);
""");
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("WhatsApp delivery and approval audit history must be retained.");
}
