using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;

namespace WorkLog.Application.Integrations;

/// <summary>
/// Vínculo de la empresa con ONE y lo que ONE responde con él. Nunca incluye el secreto.
/// <paramref name="Problem"/> explica, en palabras para quien administra, qué falta o qué falló.
/// </summary>
public sealed record OneLinkDto(
    bool Linked,
    string? ApiKeyHint,
    DateTimeOffset? LinkedAt,
    bool Reachable,
    string? TenantName,
    bool MobiControlConfigured,
    string? MobiControlHost,
    IReadOnlyList<string> MissingVariables,
    string? Problem);

public sealed record SaveOneLinkCommand(string? ApiKey, string? ApiSecret);

/// <summary>Puerto de entrada: la credencial de ONE con que WorkLog lee la configuración de la empresa.</summary>
public interface IOneLinkService
{
    /// <summary>Estado del vínculo. Con <paramref name="refresh"/> vuelve a preguntarle a ONE en vez de usar la caché.</summary>
    Task<OneLinkDto> GetAsync(bool refresh, CancellationToken ct = default);

    /// <summary>Guarda la credencial solo si ONE la acepta ahora y es de esta empresa y de WorkLog.</summary>
    Task<OperationResult<OneLinkDto>> SaveAsync(SaveOneLinkCommand command, CancellationToken ct = default);

    Task<OperationResult> RemoveAsync(CancellationToken ct = default);
}

internal sealed class OneLinkService(
    ICurrentActor actor,
    ICompanyRepository companies,
    ISecretProtector protector,
    IOneIntegrationClient one,
    ITenantConfiguration configuration,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : IOneLinkService
{
    private const string NotLinked =
        "WorkLog no tiene la credencial de ONE de esta empresa. Créala en ONE (Empresas → la empresa → WorkLog → Credenciales) y pégala aquí.";

    public async Task<OneLinkDto> GetAsync(bool refresh, CancellationToken ct = default)
    {
        var company = await companies.FindAsync(actor.TenantId, ct);
        if (company is not { HasOneCredential: true })
            return Unlinked();

        if (refresh) configuration.Forget(actor.TenantId);
        return ToDto(company.OneApiKey, company.OneLinkedAt, await configuration.GetAsync(ct));
    }

    public async Task<OperationResult<OneLinkDto>> SaveAsync(SaveOneLinkCommand command, CancellationToken ct = default)
    {
        var key = command.ApiKey?.Trim() ?? string.Empty;
        var secret = command.ApiSecret?.Trim() ?? string.Empty;
        if (key.Length == 0) return OperationResult<OneLinkDto>.Invalid("Pega la API key que ONE emitió para WorkLog.", "apiKey");
        if (secret.Length == 0) return OperationResult<OneLinkDto>.Invalid("Pega el secreto de la credencial.", "apiSecret");

        var company = await companies.FindAsync(actor.TenantId, ct);
        if (company is null) return OperationResult<OneLinkDto>.NotFound("La empresa no existe.");

        // Se prueba antes de guardar: una credencial que ONE no acepta, o de otra empresa, no se guarda.
        var check = await one.GetConfigurationAsync(key, secret, actor.TenantId, ct);
        if (check.Configuration is null)
            return OperationResult<OneLinkDto>.Invalid(check.Problem ?? "ONE no aceptó la credencial.", "apiKey", "one.credential_rejected");

        var linked = company.LinkOne(key, protector.Protect(secret), clock.GetUtcNow());
        if (linked.IsFailure) return OperationResult<OneLinkDto>.From(linked.Error!);

        audit.Record("company.one_linked", $"Vinculó WorkLog con INTECHSYS ONE (credencial {Hint(key)}).", "Empresa", company.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        configuration.Forget(actor.TenantId);

        return ToDto(company.OneApiKey, company.OneLinkedAt, check);
    }

    public async Task<OperationResult> RemoveAsync(CancellationToken ct = default)
    {
        var company = await companies.FindAsync(actor.TenantId, ct);
        if (company is not { HasOneCredential: true }) return OperationResult.Ok();

        var hint = Hint(company.OneApiKey);
        company.UnlinkOne();
        audit.Record("company.one_unlinked", $"Quitó la credencial de INTECHSYS ONE ({hint}).", "Empresa", company.Id.ToString());
        await unitOfWork.SaveChangesAsync(ct);
        configuration.Forget(actor.TenantId);
        return OperationResult.Ok();
    }

    private static OneLinkDto Unlinked() => new(false, null, null, false, null, false, null, [], NotLinked);

    private static OneLinkDto ToDto(string? apiKey, DateTimeOffset? linkedAt, OneConfigurationResult result)
    {
        if (result.Configuration is not { } config)
            return new OneLinkDto(true, Hint(apiKey), linkedAt, false, null, false, null, [], result.Problem);

        var missing = MobiControlSettings.Missing(config.Settings);
        var connection = MobiControlSettings.Read(config.Settings);
        var problem = missing.Count > 0
            ? $"En ONE faltan variables de MobiControl para {config.TenantName}: {string.Join(", ", missing)}."
            : connection is null
                ? $"{MobiControlSettings.BaseUrl} debe ser una dirección https completa."
                : null;

        return new OneLinkDto(true, Hint(apiKey), linkedAt, true, config.TenantName, connection is not null,
            connection?.BaseUrl.Host, missing, problem);
    }

    /// <summary>Lo justo para reconocer la credencial sin mostrarla: el principio de la key.</summary>
    private static string? Hint(string? apiKey) =>
        apiKey is null ? null : apiKey.Length <= 8 ? new string('•', apiKey.Length) : $"{apiKey[..8]}…";
}
