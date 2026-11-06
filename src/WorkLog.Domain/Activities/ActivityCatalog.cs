using WorkLog.Domain.Common;

namespace WorkLog.Domain.Activities;

/// <summary>Cómo cuenta el tiempo de una categoría en los resúmenes y en el cálculo de horas.</summary>
public enum TimeClass
{
    /// <summary>Cuenta como tiempo productivo.</summary>
    Productive = 1,

    /// <summary>Tiempo pagado, no productivo (traslados, labores internas).</summary>
    PaidNonProductive = 2,

    /// <summary>Se reporta como sin actividad (espera de materiales…).</summary>
    Unproductive = 3
}

/// <summary>Colores permitidos para las categorías (paleta de la marca).</summary>
public static class CategoryPalette
{
    public static readonly IReadOnlyList<string> Colors = ["#AF1839", "#751326", "#E51E49", "#868787", "#3C3C3B", "#5A5958", "#1E1F1D", "#DA830B", "#F8B21D", "#1E6B3F"];

    public static bool IsValid(string color) => Colors.Contains(color.ToUpperInvariant());
}

public sealed class ActivityCategory : TenantEntity
{
    private ActivityCategory()
    {
    }

    private ActivityCategory(Guid tenantId) : base(tenantId)
    {
    }

    public string Name { get; private set; } = string.Empty;
    public string NameKey { get; private set; } = string.Empty;
    public TimeClass TimeClass { get; private set; }
    public string Color { get; private set; } = CategoryPalette.Colors[0];
    public bool IsActive { get; private set; } = true;

    public static Result<ActivityCategory> Create(Guid tenantId, string name, TimeClass timeClass, string color)
    {
        var category = new ActivityCategory(tenantId);
        var applied = category.Update(name, timeClass, color);
        return applied.IsSuccess ? category : applied.Error!;
    }

    public Result Update(string name, TimeClass timeClass, string color)
    {
        var cleaned = Text.Clean(name);
        if (Guard.Name(cleaned, "name", 3, 60, "El nombre") is { } error) return error;

        if (!Enum.IsDefined(timeClass))
            return DomainError.Validation("category.time_class", "Elige cómo cuenta el tiempo.", "timeClass");

        if (!CategoryPalette.IsValid(color))
            return DomainError.Validation("category.color", "Elige un color de la paleta.", "color");

        Name = cleaned;
        NameKey = Text.Key(cleaned);
        TimeClass = timeClass;
        Color = color.ToUpperInvariant();
        return Result.Success();
    }

    public void SetActive(bool active) => IsActive = active;

    public static IReadOnlyList<ActivityCategory> Defaults(Guid tenantId) =>
    [
        Create(tenantId, "Productivo", TimeClass.Productive, "#AF1839").Value,
        Create(tenantId, "Traslado", TimeClass.PaidNonProductive, "#868787").Value,
        Create(tenantId, "Interna", TimeClass.PaidNonProductive, "#3C3C3B").Value,
        Create(tenantId, "Improductivo", TimeClass.Unproductive, "#DA830B").Value
    ];
}

public enum ObservationPolicy
{
    Optional = 1,
    Always = 2
}

/// <summary>Actividad del catálogo y lo que exige al finalizarla.</summary>
public sealed class ActivityType : TenantEntity
{
    private ActivityType()
    {
    }

    private ActivityType(Guid tenantId) : base(tenantId)
    {
    }

    public string Name { get; private set; } = string.Empty;
    public string NameKey { get; private set; } = string.Empty;
    public Guid CategoryId { get; private set; }
    public bool RequiresPhoto { get; private set; }
    public bool RequiresSignature { get; private set; }
    public ObservationPolicy Observation { get; private set; } = ObservationPolicy.Optional;
    public bool IsActive { get; private set; } = true;

    public static Result<ActivityType> Create(Guid tenantId, ActivityTypeData data)
    {
        var type = new ActivityType(tenantId);
        var applied = type.Update(data);
        return applied.IsSuccess ? type : applied.Error!;
    }

    public Result Update(ActivityTypeData data)
    {
        var cleaned = Text.Clean(data.Name);
        if (Guard.Name(cleaned, "name", 3, 100, "El nombre") is { } error) return error;

        if (data.CategoryId == Guid.Empty)
            return DomainError.Validation("activity.category", "Elige la categoría.", "categoryId");

        Name = cleaned;
        NameKey = Text.Key(cleaned);
        CategoryId = data.CategoryId;
        RequiresPhoto = data.RequiresPhoto;
        RequiresSignature = data.RequiresSignature;
        Observation = data.Observation;
        return Result.Success();
    }

    public void SetActive(bool active) => IsActive = active;
}

public sealed record ActivityTypeData(string Name, Guid CategoryId, bool RequiresPhoto, bool RequiresSignature, ObservationPolicy Observation);

/// <summary>Resultado con el que se cierra una actividad.</summary>
public enum ActivityOutcome
{
    Completed = 1,
    Incomplete = 2,
    Rescheduled = 3
}

/// <summary>Configuración por empresa de cada resultado: si pide observación.</summary>
public sealed class OutcomeSetting : TenantEntity
{
    private OutcomeSetting()
    {
    }

    private OutcomeSetting(Guid tenantId) : base(tenantId)
    {
    }

    public ActivityOutcome Outcome { get; private set; }
    public bool RequiresObservation { get; private set; }

    public void SetRequiresObservation(bool value) => RequiresObservation = value;

    public static string LabelOf(ActivityOutcome outcome) => outcome switch
    {
        ActivityOutcome.Completed => "Completada",
        ActivityOutcome.Incomplete => "Incompleta",
        _ => "Reprogramar"
    };

    public static string DescriptionOf(ActivityOutcome outcome) => outcome switch
    {
        ActivityOutcome.Completed => "La actividad terminó.",
        ActivityOutcome.Incomplete => "Quedó trabajo pendiente.",
        _ => "Se mueve a otra fecha."
    };

    public static IReadOnlyList<OutcomeSetting> Defaults(Guid tenantId) =>
    [
        new(tenantId) { Outcome = ActivityOutcome.Completed, RequiresObservation = false },
        new(tenantId) { Outcome = ActivityOutcome.Incomplete, RequiresObservation = true },
        new(tenantId) { Outcome = ActivityOutcome.Rescheduled, RequiresObservation = true }
    ];
}
