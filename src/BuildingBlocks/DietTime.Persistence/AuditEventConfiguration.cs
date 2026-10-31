using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public static class AuditEventConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.Property(x => x.ActorName).HasMaxLength(200);
            e.Property(x => x.Source).HasMaxLength(20);
            e.Property(x => x.HttpMethod).HasMaxLength(10);
            e.Property(x => x.Endpoint).HasMaxLength(300);
            e.Property(x => x.EntityType).HasMaxLength(100);
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.Outcome).HasMaxLength(20);
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.HasIndex(x => new { x.OccurredAt, x.Id }).HasDatabaseName("ix_audit_events_time_id");
            e.HasIndex(x => new { x.ActorUserId, x.OccurredAt }).HasDatabaseName("ix_audit_events_actor_time");
            e.HasIndex(x => new { x.Source, x.OccurredAt }).HasDatabaseName("ix_audit_events_source_time");
            e.HasIndex(x => new { x.EntityType, x.Action, x.OccurredAt }).HasDatabaseName("ix_audit_events_entity_action_time");
        });
        model.Entity<AuditEventTarget>(e =>
        {
            e.ToTable("audit_event_targets");
            e.Property(x => x.TargetType).HasMaxLength(100);
            e.Property(x => x.TargetId).HasMaxLength(100);
            e.HasOne(x => x.AuditEvent).WithMany(x => x.Targets).HasForeignKey(x => x.AuditEventId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.AuditEventId, x.TargetType, x.TargetId }).IsUnique().HasDatabaseName("ux_audit_event_targets_event_type_id");
            e.HasIndex(x => new { x.TargetType, x.TargetId }).HasDatabaseName("ix_audit_event_targets_resource");
        });
    }
}
