using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkLog.Domain.Access;
using WorkLog.Domain.Activities;
using WorkLog.Domain.Auditing;
using WorkLog.Domain.Organization;
using WorkLog.Domain.Sync;
using WorkLog.Domain.Workdays;

namespace WorkLog.Infrastructure.Persistence.Configurations;

internal sealed class ActivityCategoryConfiguration : IEntityTypeConfiguration<ActivityCategory>
{
    public void Configure(EntityTypeBuilder<ActivityCategory> builder)
    {
        builder.ToTable("ActivityCategories");
        builder.Property(x => x.Name).HasMaxLength(60);
        builder.Property(x => x.NameKey).HasMaxLength(60);
        builder.Property(x => x.Color).HasMaxLength(7);
        builder.HasIndex(x => new { x.TenantId, x.NameKey }).IsUnique();
    }
}

internal sealed class ActivityTypeConfiguration : IEntityTypeConfiguration<ActivityType>
{
    public void Configure(EntityTypeBuilder<ActivityType> builder)
    {
        builder.ToTable("ActivityTypes");
        builder.Property(x => x.Name).HasMaxLength(100);
        builder.Property(x => x.NameKey).HasMaxLength(100);
        builder.HasIndex(x => new { x.TenantId, x.NameKey }).IsUnique();
        builder.HasOne<ActivityCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OutcomeSettingConfiguration : IEntityTypeConfiguration<OutcomeSetting>
{
    public void Configure(EntityTypeBuilder<OutcomeSetting> builder)
    {
        builder.ToTable("OutcomeSettings");
        builder.HasIndex(x => new { x.TenantId, x.Outcome }).IsUnique();
    }
}

internal sealed class WorkOrderConfiguration : IEntityTypeConfiguration<WorkOrder>
{
    public void Configure(EntityTypeBuilder<WorkOrder> builder)
    {
        builder.ToTable("WorkOrders");
        builder.Property(x => x.Number).HasMaxLength(40);
        builder.Property(x => x.Notes).HasMaxLength(500);
        builder.Ignore(x => x.IsWorkable);
        builder.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        builder.HasIndex(x => new { x.TenantId, x.AssigneeId, x.ScheduledDate });
        builder.HasOne<ActivityType>().WithMany().HasForeignKey(x => x.ActivityTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WorkSite>().WithMany().HasForeignKey(x => x.WorkSiteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Member>().WithMany().HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkdayConfiguration : IEntityTypeConfiguration<Workday>
{
    public void Configure(EntityTypeBuilder<Workday> builder)
    {
        builder.ToTable("Workdays");
        builder.Property(x => x.StartSiteName).HasMaxLength(120);
        builder.Property(x => x.Note).HasMaxLength(1000);
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.Ignore(x => x.IsOpen);
        builder.Ignore(x => x.CurrentActivity);
        builder.Ignore(x => x.LastEventAt);

        builder.ComplexProperty(x => x.StartLocation, c => MapLocation(c, "Start"));
        builder.ComplexProperty(x => x.EndLocation, c => MapLocation(c, "End"));

        builder.HasMany(x => x.Activities).WithOne().HasForeignKey(a => a.WorkdayId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Activities).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Una jornada por persona y día: también protege contra dos inicios simultáneos.
        builder.HasIndex(x => new { x.TenantId, x.MemberId, x.Date }).IsUnique();
        builder.HasIndex(x => new { x.TenantId, x.Date, x.Status });
        builder.HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Restrict);
    }

    internal static void MapLocation(ComplexPropertyBuilder<LocationColumns> c, string prefix)
    {
        c.Property(p => p.Latitude).HasColumnName($"{prefix}Latitude");
        c.Property(p => p.Longitude).HasColumnName($"{prefix}Longitude");
        c.Property(p => p.AccuracyMeters).HasColumnName($"{prefix}AccuracyMeters");
        c.Ignore(p => p.HasValue);
    }
}

internal sealed class ActivityRecordConfiguration : IEntityTypeConfiguration<ActivityRecord>
{
    public void Configure(EntityTypeBuilder<ActivityRecord> builder)
    {
        builder.ToTable("ActivityRecords");
        builder.Property(x => x.Name).HasMaxLength(100);
        builder.Property(x => x.CategoryName).HasMaxLength(60);
        builder.Property(x => x.Color).HasMaxLength(7);
        builder.Property(x => x.WorkOrderNumber).HasMaxLength(40);
        builder.Property(x => x.WorkSiteName).HasMaxLength(120);
        builder.Property(x => x.Observation).HasMaxLength(1000);
        builder.Ignore(x => x.IsOpen);
        builder.Ignore(x => x.LastEventAt);

        builder.ComplexProperty(x => x.StartLocation, c => WorkdayConfiguration.MapLocation(c, "Start"));
        builder.ComplexProperty(x => x.EndLocation, c => WorkdayConfiguration.MapLocation(c, "End"));

        builder.HasMany(x => x.Pauses).WithOne().HasForeignKey(p => p.ActivityRecordId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Pauses).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(x => x.WorkOrderId);
    }
}

internal sealed class ActivityPauseConfiguration : IEntityTypeConfiguration<ActivityPause>
{
    public void Configure(EntityTypeBuilder<ActivityPause> builder) => builder.ToTable("ActivityPauses");
}

internal sealed class LocationPointConfiguration : IEntityTypeConfiguration<LocationPoint>
{
    public void Configure(EntityTypeBuilder<LocationPoint> builder)
    {
        builder.ToTable("LocationPoints");
        builder.HasIndex(x => new { x.TenantId, x.WorkdayId, x.CapturedAt });
        builder.HasIndex(x => new { x.TenantId, x.MemberId, x.CapturedAt });
    }
}

internal sealed class EvidenceConfiguration : IEntityTypeConfiguration<Evidence>
{
    public void Configure(EntityTypeBuilder<Evidence> builder)
    {
        builder.ToTable("Evidences");
        builder.Property(x => x.StorageKey).HasMaxLength(300);
        builder.Property(x => x.ContentType).HasMaxLength(60);
        builder.HasIndex(x => new { x.TenantId, x.ClientId }).IsUnique();
        builder.HasIndex(x => new { x.TenantId, x.WorkdayId });
    }
}

internal sealed class ProcessedClientEventConfiguration : IEntityTypeConfiguration<ProcessedClientEvent>
{
    public void Configure(EntityTypeBuilder<ProcessedClientEvent> builder)
    {
        builder.ToTable("ProcessedClientEvents");
        builder.Property(x => x.Kind).HasMaxLength(40);
        builder.Property(x => x.Message).HasMaxLength(400);
        builder.HasIndex(x => new { x.TenantId, x.MemberId, x.ClientEventId }).IsUnique();
    }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents");
        builder.Property(x => x.ActorName).HasMaxLength(150);
        builder.Property(x => x.ActorRole).HasMaxLength(80);
        builder.Property(x => x.Action).HasMaxLength(80);
        builder.Property(x => x.Summary).HasMaxLength(400);
        builder.Property(x => x.EntityType).HasMaxLength(60);
        builder.Property(x => x.EntityId).HasMaxLength(120);
        builder.Property(x => x.Before).HasMaxLength(-1);
        builder.Property(x => x.After).HasMaxLength(-1);
        builder.HasIndex(x => new { x.TenantId, x.OccurredAt });
        builder.HasIndex(x => new { x.TenantId, x.EntityType, x.OccurredAt });
    }
}
