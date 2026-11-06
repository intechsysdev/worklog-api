using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkLog.Domain.Absences;
using WorkLog.Domain.Access;
using WorkLog.Domain.Parameters;
using WorkLog.Domain.Privacy;
using WorkLog.Domain.Scheduling;

namespace WorkLog.Infrastructure.Persistence.Configurations;

internal sealed class ScheduleTemplateConfiguration : IEntityTypeConfiguration<ScheduleTemplate>
{
    public void Configure(EntityTypeBuilder<ScheduleTemplate> builder)
    {
        builder.ToTable("ScheduleTemplates");
        builder.Property(x => x.Name).HasMaxLength(80);
        builder.Property(x => x.NameKey).HasMaxLength(80);
        builder.Ignore(x => x.DailyMinutes);
        builder.Ignore(x => x.WeeklyMinutes);
        builder.Ignore(x => x.Summary);
        builder.HasIndex(x => new { x.TenantId, x.NameKey }).IsUnique();
    }
}

internal sealed class ScheduleAssignmentConfiguration : IEntityTypeConfiguration<ScheduleAssignment>
{
    public void Configure(EntityTypeBuilder<ScheduleAssignment> builder)
    {
        builder.ToTable("ScheduleAssignments");
        builder.HasIndex(x => new { x.TenantId, x.Level, x.TargetId, x.EffectiveFrom }).IsUnique();
        builder.HasOne<ScheduleTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ShiftOverrideConfiguration : IEntityTypeConfiguration<ShiftOverride>
{
    public void Configure(EntityTypeBuilder<ShiftOverride> builder)
    {
        builder.ToTable("ShiftOverrides");
        builder.Property(x => x.Reason).HasMaxLength(300);
        builder.HasIndex(x => new { x.TenantId, x.MemberId, x.Date }).IsUnique();
        builder.HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class HolidayConfiguration : IEntityTypeConfiguration<Holiday>
{
    public void Configure(EntityTypeBuilder<Holiday> builder)
    {
        builder.ToTable("Holidays");
        builder.Property(x => x.CountryCode).HasMaxLength(2).IsFixedLength();
        builder.Property(x => x.Name).HasMaxLength(120);
        builder.HasIndex(x => new { x.CountryCode, x.Date }).IsUnique();
    }
}

internal sealed class AbsenceConfiguration : IEntityTypeConfiguration<Absence>
{
    public void Configure(EntityTypeBuilder<Absence> builder)
    {
        builder.ToTable("Absences");
        builder.Property(x => x.Notes).HasMaxLength(500);
        builder.Property(x => x.SupportFileKey).HasMaxLength(300);
        builder.Ignore(x => x.IsPartialDay);
        builder.HasIndex(x => new { x.TenantId, x.MemberId, x.From, x.To });
        builder.HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ParameterValueConfiguration : IEntityTypeConfiguration<ParameterValue>
{
    public void Configure(EntityTypeBuilder<ParameterValue> builder)
    {
        builder.ToTable("ParameterValues");
        builder.Property(x => x.Scope).HasMaxLength(40);
        builder.Property(x => x.LegalBasis).HasMaxLength(300);
        builder.HasIndex(x => new { x.Key, x.Level, x.TenantId, x.Scope, x.EffectiveFrom }).IsUnique();
    }
}

internal sealed class PrivacyPolicyConfiguration : IEntityTypeConfiguration<PrivacyPolicyVersion>
{
    public void Configure(EntityTypeBuilder<PrivacyPolicyVersion> builder)
    {
        builder.ToTable("PrivacyPolicies");
        builder.Property(x => x.Version).HasMaxLength(20);
        builder.Property(x => x.Summary).HasMaxLength(4000);
        builder.Property(x => x.FullTextUrl).HasMaxLength(500);
        builder.Ignore(x => x.SummaryPoints);
        builder.HasIndex(x => x.Version).IsUnique();
    }
}
