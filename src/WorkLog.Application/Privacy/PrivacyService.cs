using WorkLog.Application.Abstractions;
using WorkLog.Application.Common;

namespace WorkLog.Application.Privacy;

public sealed record PrivacyPolicyDto(string Version, DateOnly EffectiveFrom, IReadOnlyList<string> Summary, string? FullTextUrl, bool Accepted, DateTimeOffset? AcceptedAt);

public sealed record AcceptPolicyCommand(string? Version);

public interface IPrivacyService
{
    Task<OperationResult<PrivacyPolicyDto>> GetCurrentAsync(CancellationToken ct = default);

    Task<OperationResult<PrivacyPolicyDto>> AcceptAsync(AcceptPolicyCommand command, CancellationToken ct = default);
}

internal sealed class PrivacyService(
    ICurrentActor actor,
    IMemberRepository members,
    IPrivacyPolicyRepository policies,
    IAuditTrail audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : IPrivacyService
{
    public async Task<OperationResult<PrivacyPolicyDto>> GetCurrentAsync(CancellationToken ct = default)
    {
        var policy = await policies.CurrentAsync(Today(), ct);
        if (policy is null) return OperationResult<PrivacyPolicyDto>.NotFound("No hay una política de datos vigente.");

        var member = await members.FindAsync(actor.MemberId, ct);
        var accepted = member?.HasAccepted(policy.Version) ?? false;
        return new PrivacyPolicyDto(policy.Version, policy.EffectiveFrom, policy.SummaryPoints, policy.FullTextUrl, accepted,
            accepted ? member!.PolicyAcceptedAt : null);
    }

    public async Task<OperationResult<PrivacyPolicyDto>> AcceptAsync(AcceptPolicyCommand command, CancellationToken ct = default)
    {
        var policy = await policies.CurrentAsync(Today(), ct);
        if (policy is null) return OperationResult<PrivacyPolicyDto>.NotFound("No hay una política de datos vigente.");

        // Se acepta exactamente la versión que se mostró: si cambió mientras la leía, debe leer la nueva.
        if (!string.Equals(command.Version?.Trim(), policy.Version, StringComparison.Ordinal))
            return OperationResult<PrivacyPolicyDto>.Conflict($"La política vigente es la versión {policy.Version}. Léela de nuevo.", "policy.outdated");

        var member = await members.FindAsync(actor.MemberId, ct);
        if (member is null) return OperationResult<PrivacyPolicyDto>.NotFound();

        if (!member.HasAccepted(policy.Version))
        {
            var accepted = member.AcceptPolicy(policy.Version, clock.GetUtcNow());
            if (accepted.IsFailure) return OperationResult<PrivacyPolicyDto>.From(accepted.Error!);

            audit.Record("policy.accepted", $"Aceptó la política de tratamiento de datos v{policy.Version}.", "Usuarios", member.Id.ToString());
            await unitOfWork.SaveChangesAsync(ct);
        }

        return new PrivacyPolicyDto(policy.Version, policy.EffectiveFrom, policy.SummaryPoints, policy.FullTextUrl, true, member.PolicyAcceptedAt);
    }

    private DateOnly Today() => LocalTime.Today(clock, LocalTime.Find(null));
}
