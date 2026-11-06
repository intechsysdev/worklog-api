using WorkLog.Domain.Common;

namespace WorkLog.Domain.Organization;

/// <summary>
/// Grupo de trabajo dentro de una sede (Instalaciones, Mantenimiento…). Su ruta de grupo en
/// MobiControl sirve para sugerir el grupo al invitar a alguien con un dispositivo de ese grupo.
/// </summary>
public sealed class WorkGroup : TenantEntity
{
    private WorkGroup()
    {
    }

    private WorkGroup(Guid tenantId) : base(tenantId)
    {
    }

    public string Name { get; private set; } = string.Empty;
    public string NameKey { get; private set; } = string.Empty;
    public Guid BranchId { get; private set; }
    public Guid? SupervisorId { get; private set; }
    public Guid? DefaultTemplateId { get; private set; }
    public string? MdmGroupPath { get; private set; }
    public bool IsActive { get; private set; } = true;

    public static Result<WorkGroup> Create(Guid tenantId, WorkGroupData data)
    {
        var group = new WorkGroup(tenantId);
        var applied = group.Apply(data);
        return applied.IsSuccess ? group : applied.Error!;
    }

    public Result Update(WorkGroupData data) => Apply(data);

    public Result Deactivate(int peopleAssigned)
    {
        if (peopleAssigned > 0)
        {
            return DomainError.Conflict("group.has_people",
                $"El grupo tiene {peopleAssigned} persona(s). Muévelas antes de desactivarlo.");
        }

        IsActive = false;
        return Result.Success();
    }

    public void Activate() => IsActive = true;

    private Result Apply(WorkGroupData data)
    {
        var name = Text.Clean(data.Name);
        if (Guard.Name(name, "name", 2, 120, "El nombre") is { } nameError) return nameError;

        if (data.BranchId == Guid.Empty)
            return DomainError.Validation("group.branch", "Elige la sede del grupo.", "branchId");

        var path = Text.CleanOrNull(data.MdmGroupPath);
        if (Guard.MaxLength(path, "mdmGroupPath", 300, "El grupo de MobiControl") is { } pathError) return pathError;

        Name = name;
        NameKey = Text.Key(name);
        BranchId = data.BranchId;
        SupervisorId = data.SupervisorId;
        DefaultTemplateId = data.DefaultTemplateId;
        MdmGroupPath = path;
        return Result.Success();
    }
}

public sealed record WorkGroupData(string Name, Guid BranchId, Guid? SupervisorId, Guid? DefaultTemplateId, string? MdmGroupPath);
