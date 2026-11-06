using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkLog.Domain.Access;
using WorkLog.Domain.Devices;
using WorkLog.Domain.Organization;
using WorkLog.Domain.Payroll;
using WorkLog.Domain.Requests;

namespace WorkLog.Infrastructure.Persistence.Configurations;

internal sealed class WorkRequestConfiguration : IEntityTypeConfiguration<WorkRequest>
{
    public void Configure(EntityTypeBuilder<WorkRequest> builder)
    {
        builder.ToTable("WorkRequests");
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.Property(x => x.AuthorizationReference).HasMaxLength(120);
        builder.Property(x => x.ResolverComment).HasMaxLength(500);
        builder.Ignore(x => x.IsPending);
        builder.Ignore(x => x.OvertimeMinutes);

        // Los días propuestos de un cambio de turno viajan con la solicitud: una columna JSON.
        builder.OwnsMany(x => x.Days, days => days.ToJson("ShiftChangeDays"));
        builder.Navigation(x => x.Days).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(x => new { x.TenantId, x.Status, x.SubmittedAt });
        builder.HasIndex(x => new { x.TenantId, x.MemberId, x.Date });
        builder.HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PayrollPeriodConfiguration : IEntityTypeConfiguration<PayrollPeriod>
{
    public void Configure(EntityTypeBuilder<PayrollPeriod> builder)
    {
        builder.ToTable("PayrollPeriods");
        builder.Ignore(x => x.IsClosed);
        builder.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.PayrollPeriodId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(x => new { x.TenantId, x.BranchId, x.From }).IsUnique();
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PayrollLineConfiguration : IEntityTypeConfiguration<PayrollLine>
{
    public void Configure(EntityTypeBuilder<PayrollLine> builder)
    {
        builder.ToTable("PayrollLines");
        builder.Property(x => x.MemberName).HasMaxLength(150);
        builder.Property(x => x.Absences).HasMaxLength(500);
        builder.Ignore(x => x.Hours);
    }
}

internal sealed class DeviceControlSettingsConfiguration : IEntityTypeConfiguration<DeviceControlSettings>
{
    public void Configure(EntityTypeBuilder<DeviceControlSettings> builder)
    {
        builder.ToTable("DeviceControlSettings");
        builder.Property(x => x.BranchScope).HasMaxLength(2000);
        builder.Property(x => x.GroupScope).HasMaxLength(2000);
        builder.Property(x => x.Warnings).HasMaxLength(40);
        builder.Property(x => x.CorporateAppList).HasMaxLength(4000);
        builder.Property(x => x.AlwaysAvailableAppList).HasMaxLength(4000);
        builder.Ignore(x => x.BranchIds);
        builder.Ignore(x => x.GroupIds);
        builder.Ignore(x => x.WarningMinutes);
        builder.Ignore(x => x.CorporateApps);
        builder.Ignore(x => x.AlwaysAvailableApps);
        builder.HasIndex(x => x.TenantId).IsUnique();
    }
}

internal sealed class DeviceCommandConfiguration : IEntityTypeConfiguration<DeviceCommand>
{
    public void Configure(EntityTypeBuilder<DeviceCommand> builder)
    {
        builder.ToTable("DeviceCommands");
        builder.Property(x => x.DeviceId).HasMaxLength(120);
        builder.Property(x => x.Error).HasMaxLength(400);
        builder.HasIndex(x => new { x.TenantId, x.DeviceId, x.RequestedAt });
    }
}
