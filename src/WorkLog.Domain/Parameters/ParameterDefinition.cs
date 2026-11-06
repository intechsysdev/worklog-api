namespace WorkLog.Domain.Parameters;

/// <summary>Parámetros que gobiernan la jornada, la ubicación, los avisos y el cálculo legal.</summary>
public enum ParameterKey
{
    EarlyStartMinutes = 1,
    LateToleranceMinutes = 2,
    NoStartReminderMinutes = 3,
    AutoCloseGraceMinutes = 4,
    DefaultGeofenceRadiusMeters = 10,
    MinGpsAccuracyMeters = 11,
    SamplingMinutes = 12,
    InactivityAlertMinutes = 20,
    ShiftEndWarningMinutes = 21,
    MaxPauseMinutes = 30,
    WeeklyCapHours = 40,
    NightStartHour = 41,
    SundaySurchargePercent = 42,
    NightSurchargePercent = 43,
    OvertimeDailyMaxHours = 44,
    OvertimeWeeklyMaxHours = 45
}

/// <summary>Niveles de la cadena de herencia, del más general al más específico.</summary>
public enum ParameterLevel
{
    Global = 1,
    Country = 2,
    Company = 3,
    Branch = 4
}

/// <summary>
/// Definición de un parámetro: rango, paso y valor por defecto. Vive en el código porque
/// cambiarla cambia el comportamiento del producto; lo que se guarda en base son los valores.
/// </summary>
public sealed record ParameterDefinition(
    ParameterKey Key,
    string Code,
    string Group,
    string Name,
    string Help,
    string Unit,
    int Min,
    int Max,
    int Step,
    int DefaultValue,
    bool IsLegal)
{
    public static readonly IReadOnlyList<ParameterDefinition> All =
    [
        new(ParameterKey.EarlyStartMinutes, "ant", "Jornada", "Inicio anticipado permitido",
            "Cuántos minutos antes del turno se puede iniciar la jornada.", "min antes", 0, 60, 5, 15, false),
        new(ParameterKey.LateToleranceMinutes, "tol", "Jornada", "Tolerancia de llegada",
            "Después de este margen, la llegada se marca como tarde.", "min", 0, 30, 5, 5, false),
        new(ParameterKey.NoStartReminderMinutes, "rec", "Jornada", "Recordatorio si no inicia",
            "Aviso al usuario y a su supervisor si no ha iniciado la jornada.", "min después", 5, 60, 5, 15, false),
        new(ParameterKey.AutoCloseGraceMinutes, "gra", "Jornada", "Gracia antes del cierre automático",
            "Minutos después del fin del turno en que se cierra la jornada abierta.", "min", 0, 30, 5, 10, false),
        new(ParameterKey.DefaultGeofenceRadiusMeters, "rad", "Ubicación", "Radio de geocerca por defecto",
            "Radio con el que nace un sitio nuevo.", "m", 30, 500, 10, 100, false),
        new(ParameterKey.MinGpsAccuracyMeters, "pre", "Ubicación", "Precisión GPS mínima",
            "Con peor precisión el punto se guarda, pero no valida la geocerca.", "m", 10, 200, 10, 50, false),
        new(ParameterKey.SamplingMinutes, "mue", "Ubicación", "Muestreo en actividad",
            "Cada cuánto se registra la ubicación durante la jornada.", "min", 5, 60, 5, 15, false),
        new(ParameterKey.InactivityAlertMinutes, "ina", "Avisos", "Aviso por inactividad",
            "Minutos sin actividad antes de avisar al usuario.", "min", 5, 60, 5, 20, false),
        new(ParameterKey.ShiftEndWarningMinutes, "fin", "Avisos", "Aviso de fin de turno",
            "Minutos antes del cierre automático en que se avisa.", "min antes", 5, 60, 5, 15, false),
        new(ParameterKey.MaxPauseMinutes, "paus", "Pausas", "Duración máxima de pausa",
            "Al pasarla se avisa al usuario.", "min", 10, 120, 5, 30, false),
        new(ParameterKey.WeeklyCapHours, "top", "Legal", "Tope semanal",
            "Horas ordinarias por semana. Lo que pase es suplementario.", "h", 30, 60, 1, 42, true),
        new(ParameterKey.NightStartHour, "noc", "Legal", "Inicio de franja nocturna",
            "Hora desde la que el trabajo es nocturno.", "hora", 18, 22, 1, 20, true),
        new(ParameterKey.SundaySurchargePercent, "dom", "Legal", "Recargo dominical y festivo",
            "Porcentaje sobre la hora ordinaria.", "%", 0, 200, 5, 25, true),
        new(ParameterKey.NightSurchargePercent, "nrc", "Legal", "Recargo nocturno",
            "Porcentaje sobre la hora ordinaria en franja nocturna.", "%", 0, 100, 5, 35, true),
        new(ParameterKey.OvertimeDailyMaxHours, "hed", "Legal", "Horas extra máximas al día",
            "Solo con autorización vigente.", "h", 0, 4, 1, 2, true),
        new(ParameterKey.OvertimeWeeklyMaxHours, "hes", "Legal", "Horas extra máximas a la semana",
            "Solo con autorización vigente.", "h", 0, 20, 1, 12, true)
    ];

    public static ParameterDefinition Of(ParameterKey key) => All.First(d => d.Key == key);

    public static ParameterDefinition? FromCode(string code) =>
        All.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>Los legales se editan solo en el país: dependen de la ley, no de la empresa.</summary>
    public bool IsEditableAt(ParameterLevel level) => !IsLegal || level == ParameterLevel.Country;

    public bool Accepts(int value) => value >= Min && value <= Max && (value - Min) % Step == 0;
}
