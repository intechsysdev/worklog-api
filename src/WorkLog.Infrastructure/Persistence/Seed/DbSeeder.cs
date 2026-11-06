using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkLog.Domain.Parameters;
using WorkLog.Domain.Privacy;
using WorkLog.Domain.Scheduling;

namespace WorkLog.Infrastructure.Persistence.Seed;

/// <summary>
/// Aplica migraciones y deja los datos del producto (no de una empresa): política de datos,
/// festivos y parámetros legales de Colombia. Es idempotente: solo completa lo que falta.
/// </summary>
public sealed class DbSeeder(WorkLogDbContext db, TimeProvider clock, ILogger<DbSeeder> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);

        await SeedPrivacyPolicyAsync(ct);
        await SeedColombiaAsync(ct);

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedPrivacyPolicyAsync(CancellationToken ct)
    {
        if (await db.PrivacyPolicies.AnyAsync(ct)) return;

        db.PrivacyPolicies.Add(PrivacyPolicyVersion.Create("1.0", new DateOnly(2026, 1, 1),
        [
            "Ubicación solo en jornada: al iniciar, cambiar y finalizar actividades, y cada 15 minutos mientras trabajas.",
            "Horas y actividades: para calcular tu jornada, horas extra y recargos.",
            "Fotos y firma: como evidencia del trabajo realizado.",
            "Tus derechos: conocer, actualizar, rectificar y revocar la autorización (Ley 1581 de 2012)."
        ], null));

        logger.LogInformation("Política de datos v1.0 creada.");
    }

    private async Task SeedColombiaAsync(CancellationToken ct)
    {
        const string country = "CO";
        var year = clock.GetUtcNow().Year;

        var existing = (await db.Holidays.Where(h => h.CountryCode == country).Select(h => h.Date).ToListAsync(ct)).ToHashSet();
        var added = 0;

        for (var y = year - 1; y <= year + 2; y++)
        {
            foreach (var (date, name) in ColombianHolidays.For(y))
            {
                if (!existing.Add(date)) continue;
                db.Holidays.Add(Holiday.Create(country, date, name));
                added++;
            }
        }

        if (added > 0) logger.LogInformation("{Count} festivos de Colombia cargados.", added);

        // Reglas legales vigentes en Colombia: tope de 42 h (Ley 2101 de 2021, desde el 15 de julio
        // de 2026), jornada nocturna desde las 19:00 y recargo dominical del 90 % (Ley 2466 de 2025).
        (ParameterKey Key, int Value, DateOnly From, string Basis)[] legal =
        [
            (ParameterKey.WeeklyCapHours, 42, new DateOnly(2026, 7, 15), "Ley 2101 de 2021, art. 3 (reducción gradual)"),
            (ParameterKey.NightStartHour, 19, new DateOnly(2025, 12, 25), "Ley 2466 de 2025, art. 10"),
            (ParameterKey.SundaySurchargePercent, 90, new DateOnly(2026, 7, 1), "Ley 2466 de 2025, art. 14 (aplicación gradual)"),
            (ParameterKey.NightSurchargePercent, 35, new DateOnly(2025, 1, 1), "Código Sustantivo del Trabajo, art. 168"),
            (ParameterKey.OvertimeDailyMaxHours, 2, new DateOnly(2025, 1, 1), "Código Sustantivo del Trabajo, art. 22 Ley 50 de 1990"),
            (ParameterKey.OvertimeWeeklyMaxHours, 12, new DateOnly(2025, 1, 1), "Código Sustantivo del Trabajo, art. 22 Ley 50 de 1990")
        ];

        var scope = ParameterScope.Country(country);
        var stored = await db.ParameterValues.Where(v => v.Level == ParameterLevel.Country && v.Scope == country).ToListAsync(ct);

        foreach (var (key, value, from, basis) in legal)
        {
            if (stored.Any(v => v.Key == key)) continue;
            db.ParameterValues.Add(ParameterValue.Create(scope, key, value, from, basis).Value);
        }
    }
}
