using WorkLog.Domain.Common;

namespace WorkLog.Domain.Organization;

public enum BranchStatus
{
    Draft = 1,
    Active = 2,
    Inactive = 3
}

/// <summary>
/// Sede de la empresa. De su ciudad salen la zona horaria y de su país los festivos y las
/// reglas laborales; por eso país y ciudad son obligatorios y no se escriben a mano.
/// </summary>
public sealed class Branch : TenantEntity
{
    private Branch()
    {
    }

    private Branch(Guid tenantId) : base(tenantId)
    {
    }

    public string Name { get; private set; } = string.Empty;
    public string NameKey { get; private set; } = string.Empty;
    public string CountryCode { get; private set; } = string.Empty;
    public string City { get; private set; } = string.Empty;
    public string? Region { get; private set; }
    public string TimeZoneId { get; private set; } = string.Empty;
    public Guid? MainSiteId { get; private set; }
    public Guid? DefaultTemplateId { get; private set; }
    public BranchStatus Status { get; private set; }

    public bool IsActive => Status == BranchStatus.Active;

    public static Result<Branch> Create(Guid tenantId, BranchData data, bool asDraft = false)
    {
        var branch = new Branch(tenantId) { Status = asDraft ? BranchStatus.Draft : BranchStatus.Active };
        var applied = branch.Apply(data);
        return applied.IsSuccess ? branch : applied.Error!;
    }

    public Result Update(BranchData data) => Apply(data);

    public void SetMainSite(Guid? siteId) => MainSiteId = siteId;

    public void SetDefaultTemplate(Guid? templateId) => DefaultTemplateId = templateId;

    public void Activate() => Status = BranchStatus.Active;

    /// <summary>Una sede con personas no se desactiva: quedarían sin turno ni festivos.</summary>
    public Result Deactivate(int peopleAssigned)
    {
        if (peopleAssigned > 0)
        {
            return DomainError.Conflict("branch.has_people",
                $"La sede tiene {peopleAssigned} persona(s). Muévelas antes de desactivarla.");
        }

        Status = BranchStatus.Inactive;
        return Result.Success();
    }

    private Result Apply(BranchData data)
    {
        var name = Text.Clean(data.Name);
        if (Guard.Name(name, "name", 2, 120, "El nombre") is { } nameError) return nameError;

        var country = Text.Clean(data.CountryCode).ToUpperInvariant();
        if (country.Length != 2 || !country.All(char.IsAsciiLetterUpper))
            return DomainError.Validation("branch.country", "Elige el país de la lista.", "countryCode");

        var city = Text.Clean(data.City);
        if (city.Length < 2)
            return DomainError.Validation("branch.city", "Elige la ciudad de la lista.", "city");

        var timeZone = Text.Clean(data.TimeZoneId);
        if (timeZone.Length == 0)
            return DomainError.Validation("branch.time_zone", "La ciudad no trae zona horaria.", "timeZoneId");

        Name = name;
        NameKey = Text.Key(name);
        CountryCode = country;
        City = city;
        Region = Text.CleanOrNull(data.Region);
        TimeZoneId = timeZone;
        return Result.Success();
    }
}

public sealed record BranchData(string Name, string CountryCode, string City, string? Region, string TimeZoneId);
