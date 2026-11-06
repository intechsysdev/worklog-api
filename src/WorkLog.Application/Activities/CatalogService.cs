using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;
using WorkLog.Domain.Activities;
using WorkLog.Domain.Common;

namespace WorkLog.Application.Activities;

public sealed record CategoryDto(Guid Id, string Name, TimeClass TimeClass, string Color, bool IsActive, int Activities);

public sealed record SaveCategoryCommand(string? Name, TimeClass TimeClass, string? Color);

public sealed record ActivityTypeDto(
    Guid Id,
    string Name,
    Guid CategoryId,
    string CategoryName,
    string CategoryColor,
    TimeClass TimeClass,
    bool RequiresPhoto,
    bool RequiresSignature,
    ObservationPolicy Observation,
    bool IsActive,
    DateTimeOffset CreatedAt);

public sealed record SaveActivityTypeCommand(string? Name, Guid CategoryId, bool RequiresPhoto, bool RequiresSignature, ObservationPolicy Observation);

/// <summary>Cambio parcial de un tipo de actividad (los interruptores de la tabla del catálogo).</summary>
public sealed record PatchActivityTypeCommand(bool? RequiresPhoto, bool? RequiresSignature, ObservationPolicy? Observation, bool? IsActive);

public sealed record OutcomeDto(ActivityOutcome Outcome, string Label, string Description, bool RequiresObservation);

public sealed record CatalogDto(
    IReadOnlyList<CategoryDto> Categories,
    IReadOnlyList<ActivityTypeDto> Activities,
    IReadOnlyList<OutcomeDto> Outcomes,
    IReadOnlyList<string> Palette);

public interface ICatalogService
{
    Task<CatalogDto> GetAsync(CancellationToken ct = default);

    Task<OperationResult<CategoryDto>> CreateCategoryAsync(SaveCategoryCommand command, CancellationToken ct = default);
    Task<OperationResult<CategoryDto>> UpdateCategoryAsync(Guid id, SaveCategoryCommand command, CancellationToken ct = default);

    Task<OperationResult<CategoryDto>> SetCategoryActiveAsync(Guid id, bool active, CancellationToken ct = default);

    Task<OperationResult<ActivityTypeDto>> CreateActivityAsync(SaveActivityTypeCommand command, CancellationToken ct = default);
    Task<OperationResult<ActivityTypeDto>> UpdateActivityAsync(Guid id, SaveActivityTypeCommand command, CancellationToken ct = default);
    Task<OperationResult<ActivityTypeDto>> PatchActivityAsync(Guid id, PatchActivityTypeCommand command, CancellationToken ct = default);

    Task<OperationResult<OutcomeDto>> SetOutcomeObservationAsync(ActivityOutcome outcome, bool requiresObservation, CancellationToken ct = default);
}

internal sealed class CatalogService(
    ICurrentActor actor,
    IActivityCategoryRepository categories,
    IActivityTypeRepository types,
    IOutcomeSettingRepository outcomes,
    IAuditTrail audit,
    IUnitOfWork unitOfWork) : ICatalogService
{
    public async Task<CatalogDto> GetAsync(CancellationToken ct = default)
    {
        var categoryList = await categories.ListAsync(ct);
        var typeList = await types.ListAsync(false, ct);
        var categoryMap = categoryList.ToDictionary(c => c.Id);

        return new CatalogDto(
            categoryList.OrderBy(c => c.TimeClass).ThenBy(c => c.Name)
                .Select(c => ToDto(c, typeList.Count(t => t.CategoryId == c.Id))).ToList(),
            typeList.OrderBy(t => !t.IsActive).ThenBy(t => t.Name).Select(t => ToDto(t, categoryMap)).ToList(),
            (await outcomes.ListAsync(ct)).OrderBy(o => o.Outcome).Select(ToDto).ToList(),
            CategoryPalette.Colors);
    }

    public async Task<OperationResult<CategoryDto>> CreateCategoryAsync(SaveCategoryCommand command, CancellationToken ct = default)
    {
        if (await categories.NameExistsAsync(Text.Key(command.Name), null, ct))
            return OperationResult<CategoryDto>.Invalid("Ya existe una categoría con ese nombre.", "name", "category.name_taken");

        var created = ActivityCategory.Create(actor.TenantId, command.Name ?? string.Empty, command.TimeClass, command.Color ?? string.Empty);
        if (created.IsFailure) return OperationResult<CategoryDto>.From(created.Error!);

        categories.Add(created.Value);
        audit.Record("category.created", $"Creó la categoría {created.Value.Name}.", "Actividades", created.Value.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(created.Value, 0);
    }

    public async Task<OperationResult<CategoryDto>> UpdateCategoryAsync(Guid id, SaveCategoryCommand command, CancellationToken ct = default)
    {
        var category = await categories.FindAsync(id, ct);
        if (category is null) return OperationResult<CategoryDto>.NotFound("La categoría no existe.");

        if (await categories.NameExistsAsync(Text.Key(command.Name), id, ct))
            return OperationResult<CategoryDto>.Invalid("Ya existe una categoría con ese nombre.", "name", "category.name_taken");

        var before = new { category.Name, category.TimeClass, category.Color };
        var updated = category.Update(command.Name ?? string.Empty, command.TimeClass, command.Color ?? string.Empty);
        if (updated.IsFailure) return OperationResult<CategoryDto>.From(updated.Error!);

        audit.Record("category.updated", $"Actualizó la categoría {category.Name}.", "Actividades", category.Id.ToString(),
            before, new { category.Name, category.TimeClass, category.Color });
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(category, (await types.ListAsync(false, ct)).Count(t => t.CategoryId == id));
    }

    public async Task<OperationResult<CategoryDto>> SetCategoryActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var category = await categories.FindAsync(id, ct);
        if (category is null) return OperationResult<CategoryDto>.NotFound("La categoría no existe.");

        var activities = (await types.ListAsync(true, ct)).Count(t => t.CategoryId == id);
        if (!active && activities > 0)
            return OperationResult<CategoryDto>.Conflict($"La categoría tiene {activities} actividad(es) activa(s). Desactívalas o muévelas antes.", "category.has_activities");

        category.SetActive(active);
        audit.Record(active ? "category.activated" : "category.deactivated",
            $"{(active ? "Activó" : "Desactivó")} la categoría {category.Name}.", "Actividades", category.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(category, (await types.ListAsync(false, ct)).Count(t => t.CategoryId == id));
    }

    public async Task<OperationResult<ActivityTypeDto>> CreateActivityAsync(SaveActivityTypeCommand command, CancellationToken ct = default)
    {
        if (await types.NameExistsAsync(Text.Key(command.Name), null, ct))
            return OperationResult<ActivityTypeDto>.Invalid("Ya existe una actividad con ese nombre.", "name", "activity.name_taken");

        if (await categories.FindAsync(command.CategoryId, ct) is not { IsActive: true } category)
            return OperationResult<ActivityTypeDto>.Invalid("Elige una categoría activa.", "categoryId");

        var created = ActivityType.Create(actor.TenantId, ToData(command));
        if (created.IsFailure) return OperationResult<ActivityTypeDto>.From(created.Error!);

        types.Add(created.Value);
        audit.Record("activity.created", $"Creó la actividad {created.Value.Name} ({category.Name}).", "Actividades", created.Value.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(created.Value, new Dictionary<Guid, ActivityCategory> { [category.Id] = category });
    }

    public async Task<OperationResult<ActivityTypeDto>> UpdateActivityAsync(Guid id, SaveActivityTypeCommand command, CancellationToken ct = default)
    {
        var type = await types.FindAsync(id, ct);
        if (type is null) return OperationResult<ActivityTypeDto>.NotFound("La actividad no existe.");

        if (await types.NameExistsAsync(Text.Key(command.Name), id, ct))
            return OperationResult<ActivityTypeDto>.Invalid("Ya existe una actividad con ese nombre.", "name", "activity.name_taken");

        if (await categories.FindAsync(command.CategoryId, ct) is not { } category)
            return OperationResult<ActivityTypeDto>.Invalid("Elige la categoría.", "categoryId");

        var updated = type.Update(ToData(command));
        if (updated.IsFailure) return OperationResult<ActivityTypeDto>.From(updated.Error!);

        audit.Record("activity.updated", $"Actualizó la actividad {type.Name}.", "Actividades", type.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(type, new Dictionary<Guid, ActivityCategory> { [category.Id] = category });
    }

    public async Task<OperationResult<ActivityTypeDto>> PatchActivityAsync(Guid id, PatchActivityTypeCommand command, CancellationToken ct = default)
    {
        var type = await types.FindAsync(id, ct);
        if (type is null) return OperationResult<ActivityTypeDto>.NotFound("La actividad no existe.");

        var data = new ActivityTypeData(type.Name, type.CategoryId,
            command.RequiresPhoto ?? type.RequiresPhoto,
            command.RequiresSignature ?? type.RequiresSignature,
            command.Observation ?? type.Observation);

        var updated = type.Update(data);
        if (updated.IsFailure) return OperationResult<ActivityTypeDto>.From(updated.Error!);
        if (command.IsActive is { } active) type.SetActive(active);

        audit.Record("activity.updated", $"Cambió las exigencias de {type.Name}.", "Actividades", type.Id.ToString(),
            after: new { type.RequiresPhoto, type.RequiresSignature, type.Observation, type.IsActive });
        await unitOfWork.SaveChangesAsync(ct);

        var category = await categories.FindAsync(type.CategoryId, ct);
        return ToDto(type, category is null ? new Dictionary<Guid, ActivityCategory>() : new Dictionary<Guid, ActivityCategory> { [category.Id] = category });
    }

    public async Task<OperationResult<OutcomeDto>> SetOutcomeObservationAsync(ActivityOutcome outcome, bool requiresObservation, CancellationToken ct = default)
    {
        var setting = (await outcomes.ListAsync(ct)).FirstOrDefault(o => o.Outcome == outcome);
        if (setting is null) return OperationResult<OutcomeDto>.NotFound("El resultado no existe.");

        setting.SetRequiresObservation(requiresObservation);
        audit.Record("outcome.updated",
            $"«{OutcomeSetting.LabelOf(outcome)}» {(requiresObservation ? "ahora pide" : "ya no pide")} observación.", "Actividades", outcome.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        return ToDto(setting);
    }

    private static ActivityTypeData ToData(SaveActivityTypeCommand c) =>
        new(c.Name ?? string.Empty, c.CategoryId, c.RequiresPhoto, c.RequiresSignature, c.Observation);

    private static CategoryDto ToDto(ActivityCategory c, int activities) =>
        new(c.Id, c.Name, c.TimeClass, c.Color, c.IsActive, activities);

    internal static ActivityTypeDto ToDto(ActivityType t, IReadOnlyDictionary<Guid, ActivityCategory> categories)
    {
        var category = categories.GetValueOrDefault(t.CategoryId);
        return new ActivityTypeDto(t.Id, t.Name, t.CategoryId, category?.Name ?? string.Empty, category?.Color ?? "#868787",
            category?.TimeClass ?? TimeClass.Productive, t.RequiresPhoto, t.RequiresSignature, t.Observation, t.IsActive, t.CreatedAt);
    }

    internal static OutcomeDto ToDto(OutcomeSetting o) =>
        new(o.Outcome, OutcomeSetting.LabelOf(o.Outcome), OutcomeSetting.DescriptionOf(o.Outcome), o.RequiresObservation);
}
