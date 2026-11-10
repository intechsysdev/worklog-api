using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkLog.Domain.Access;
using WorkLog.Domain.Organization;

namespace WorkLog.Infrastructure.Persistence.Configurations;

internal sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        builder.ToTable("Companies");
        builder.Property(x => x.Name).HasMaxLength(160);
        builder.Property(x => x.Slug).HasMaxLength(80);
        builder.Property(x => x.CountryCode).HasMaxLength(2).IsFixedLength();
        builder.Property(x => x.OneApiKey).HasMaxLength(200);
        builder.Property(x => x.OneApiSecretProtected).HasMaxLength(2000);
        builder.Ignore(x => x.HasOneCredential);
    }
}

internal sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("Branches");
        builder.Property(x => x.Name).HasMaxLength(120);
        builder.Property(x => x.NameKey).HasMaxLength(120);
        builder.Property(x => x.CountryCode).HasMaxLength(2).IsFixedLength();
        builder.Property(x => x.City).HasMaxLength(120);
        builder.Property(x => x.Region).HasMaxLength(120);
        builder.Property(x => x.TimeZoneId).HasMaxLength(64);
        builder.HasIndex(x => new { x.TenantId, x.NameKey }).IsUnique();
    }
}

internal sealed class WorkGroupConfiguration : IEntityTypeConfiguration<WorkGroup>
{
    public void Configure(EntityTypeBuilder<WorkGroup> builder)
    {
        builder.ToTable("WorkGroups");
        builder.Property(x => x.Name).HasMaxLength(120);
        builder.Property(x => x.NameKey).HasMaxLength(120);
        builder.Property(x => x.MdmGroupPath).HasMaxLength(300);
        builder.HasIndex(x => new { x.TenantId, x.BranchId, x.NameKey }).IsUnique();
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkSiteConfiguration : IEntityTypeConfiguration<WorkSite>
{
    public void Configure(EntityTypeBuilder<WorkSite> builder)
    {
        builder.ToTable("WorkSites");
        builder.Property(x => x.Name).HasMaxLength(120);
        builder.Property(x => x.NameKey).HasMaxLength(120);
        builder.Property(x => x.Address).HasMaxLength(250);
        builder.Property(x => x.PolygonVertices).HasMaxLength(2000);
        builder.Ignore(x => x.Geofence);
        builder.HasIndex(x => new { x.TenantId, x.NameKey }).IsUnique();
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles");
        builder.Property(x => x.Name).HasMaxLength(80);
        builder.Property(x => x.NameKey).HasMaxLength(80);
        builder.Property(x => x.SystemKey).HasMaxLength(20);
        builder.HasIndex(x => new { x.TenantId, x.NameKey }).IsUnique();
        builder.HasIndex(x => new { x.TenantId, x.SystemKey }).IsUnique().HasFilter("[SystemKey] IS NOT NULL");
    }
}

internal sealed class MobileSessionConfiguration : IEntityTypeConfiguration<MobileSession>
{
    public void Configure(EntityTypeBuilder<MobileSession> builder)
    {
        builder.ToTable("MobileSessions");
        builder.Property(x => x.AccessTokenHash).HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.RefreshTokenHash).HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.PreviousRefreshTokenHash).HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.Device).HasMaxLength(120);
        builder.Property(x => x.RevokedReason).HasMaxLength(60);
        builder.Ignore(x => x.IsRevoked);
        builder.HasIndex(x => x.AccessTokenHash).IsUnique();
        builder.HasIndex(x => x.RefreshTokenHash).IsUnique();
        builder.HasIndex(x => x.PreviousRefreshTokenHash);
        builder.HasIndex(x => new { x.TenantId, x.MemberId });
        builder.HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MemberConfiguration : IEntityTypeConfiguration<Member>
{
    public void Configure(EntityTypeBuilder<Member> builder)
    {
        builder.ToTable("Members");
        builder.Property(x => x.Email).HasMaxLength(200);
        builder.Property(x => x.FullName).HasMaxLength(150);
        builder.Property(x => x.DeviceId).HasMaxLength(120);
        builder.Property(x => x.DeviceModel).HasMaxLength(120);
        builder.Property(x => x.PolicyVersion).HasMaxLength(20);
        builder.Property(x => x.PasswordHash).HasMaxLength(200);
        builder.Ignore(x => x.HasAppPassword);
        builder.HasIndex(x => new { x.TenantId, x.Email }).IsUnique();
        // El ingreso a la app busca por correo en todas las empresas.
        builder.HasIndex(x => x.Email);
        builder.HasIndex(x => new { x.TenantId, x.OneUserId }).IsUnique().HasFilter("[OneUserId] IS NOT NULL");
        builder.HasIndex(x => new { x.TenantId, x.BranchId });
        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WorkGroup>().WithMany().HasForeignKey(x => x.WorkGroupId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Member>().WithMany().HasForeignKey(x => x.SupervisorId).OnDelete(DeleteBehavior.Restrict);
    }
}
