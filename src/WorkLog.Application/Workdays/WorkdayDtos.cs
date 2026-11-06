using WorkLog.Application.Activities;
using WorkLog.Domain.Activities;
using WorkLog.Domain.Geo;
using WorkLog.Domain.Scheduling;
using WorkLog.Domain.Sync;
using WorkLog.Domain.Workdays;

namespace WorkLog.Application.Workdays;

/// <summary>Lectura del GPS que envía el teléfono.</summary>
public sealed record LocationInput(double Latitude, double Longitude, double Accuracy, DateTimeOffset? CapturedAt);

public sealed record StartWorkdayCommand(Guid? ClientEventId, DateTimeOffset? OccurredAt, LocationInput? Location, Guid? WorkdayId);

public sealed record StartActivityCommand(
    Guid? ClientEventId,
    DateTimeOffset? OccurredAt,
    LocationInput? Location,
    Guid? ActivityId,
    Guid? ActivityTypeId,
    Guid? WorkOrderId);

public sealed record ActivityEventCommand(Guid? ClientEventId, DateTimeOffset? OccurredAt, LocationInput? Location);

public sealed record FinishActivityCommand(
    Guid? ClientEventId,
    DateTimeOffset? OccurredAt,
    LocationInput? Location,
    ActivityOutcome Outcome,
    string? Observation,
    int PhotoCount,
    bool HasSignature);

public sealed record CloseWorkdayCommand(Guid? ClientEventId, DateTimeOffset? OccurredAt, LocationInput? Location, string? Note);

public sealed record LocationSamplesCommand(IReadOnlyList<LocationInput>? Samples);

public sealed record UploadEvidenceCommand(
    Guid ClientId,
    Guid ActivityId,
    EvidenceKind Kind,
    DateTimeOffset? CapturedAt,
    Stream Content,
    string ContentType,
    long Length);

/// <summary>Qué hizo el servidor con el evento: aceptado, aceptado con corrección o rechazado.</summary>
public sealed record CommandOutcome(SyncOutcome Outcome, string? Message, Guid? ResultId);

public sealed record WorkdayCommandResult(CommandOutcome Outcome, TodayDto Today);

public sealed record DayPlanDto(
    DayKind Kind,
    TimeOnly? Start,
    TimeOnly? End,
    int LunchMinutes,
    int ScheduledMinutes,
    string? Label,
    string? TemplateName,
    string? DayLetters);

public sealed record StartWindowDto(DateTimeOffset OpensAt, DateTimeOffset ShiftStart, DateTimeOffset ShiftEnd, bool CanStartNow, string? BlockedReason);

public sealed record RequirementsDto(bool Photo, bool Signature, bool ObservationAlways);

public sealed record CurrentActivityDto(
    Guid Id,
    Guid ActivityTypeId,
    string Name,
    string Color,
    TimeClass TimeClass,
    string? WorkOrderNumber,
    string? SiteName,
    DateTimeOffset StartedAt,
    ActivityStatus Status,
    int WorkedSeconds,
    int PausedSeconds,
    DateTimeOffset? PausedSince,
    GeofenceStatus StartGeofence,
    double? StartAccuracy,
    RequirementsDto Requirements);

public sealed record CategoryMinutesDto(string Name, string Color, TimeClass TimeClass, int Minutes);

public sealed record DaySummaryDto(
    IReadOnlyList<CategoryMinutesDto> ByCategory,
    int ActivityCount,
    int ProductiveMinutes,
    int PaidNonProductiveMinutes,
    int UnproductiveMinutes,
    int IdleMinutes,
    int WithoutActivityMinutes,
    int PausedMinutes,
    int ElapsedMinutes);

public sealed record WorkdayDto(
    Guid Id,
    DateOnly Date,
    WorkdayStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    bool StartAdjusted,
    int LateMinutes,
    GeofenceStatus StartGeofence,
    string? StartSiteName,
    CloseKind? CloseKind,
    string? Note,
    CurrentActivityDto? Current,
    DaySummaryDto Summary);

public sealed record AbsenceInfoDto(string Label, DateOnly From, DateOnly To, string? ApprovedBy);

public sealed record NextShiftDto(DateOnly Date, TimeOnly Start, TimeOnly End, string? TemplateName, IReadOnlyList<string> Notes);

public sealed record PolicyStatusDto(string? CurrentVersion, bool Accepted, DateOnly? EffectiveFrom);

/// <summary>Parámetros que el teléfono necesita para muestrear y avisar sin preguntar al servidor.</summary>
public sealed record ClientSettingsDto(
    int SamplingMinutes,
    int MinGpsAccuracyMeters,
    int InactivityAlertMinutes,
    int ShiftEndWarningMinutes,
    int MaxPauseMinutes,
    int AutoCloseGraceMinutes);

public sealed record TodayDto(
    DateTimeOffset ServerTime,
    DateOnly Date,
    string TimeZoneId,
    string MemberName,
    string? BranchName,
    string? SupervisorName,
    DayPlanDto Plan,
    StartWindowDto? Window,
    WorkdayDto? Workday,
    AbsenceInfoDto? Absence,
    NextShiftDto? NextShift,
    PolicyStatusDto Policy,
    ClientSettingsDto Settings,
    DeviceStatusDto Device,
    TimeOnly? OvertimeUntil,
    DateTimeOffset? AutoCloseAt);

/// <summary>Control de apps por MobiControl para el kiosco y el aviso de fin de turno.</summary>
public sealed record DeviceStatusDto(
    bool ControlEnabled,
    bool IsShared,
    string? DeviceModel,
    IReadOnlyList<int> WarningMinutes,
    bool LogoutSharedOnClose,
    IReadOnlyList<string> CorporateApps,
    IReadOnlyList<string> AlwaysAvailableApps);

public sealed record SegmentDto(SegmentKind Kind, DateTimeOffset Start, DateTimeOffset? End, int Minutes, string Title, string Color, TimeClass? TimeClass);

public sealed record TimelineEntryDto(
    string Kind,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset? End,
    int Minutes,
    string Color,
    bool Running,
    string? WorkOrderNumber,
    string? SiteName,
    ActivityOutcome? Outcome,
    string? OutcomeLabel,
    bool AutoFinished,
    int PhotoCount,
    bool HasSignature,
    GeofenceStatus Geofence);

public sealed record TimelineDto(
    DateOnly Date,
    TimeOnly? ShiftStart,
    TimeOnly? ShiftEnd,
    WorkdayDto? Workday,
    IReadOnlyList<SegmentDto> Segments,
    IReadOnlyList<TimelineEntryDto> Entries);

public sealed record AssignedOptionDto(
    Guid WorkOrderId,
    string Number,
    Guid ActivityTypeId,
    string Title,
    string? SiteName,
    TimeOnly? ScheduledTime,
    WorkOrderStatus Status);

public sealed record CatalogOptionDto(
    Guid ActivityTypeId,
    string Title,
    string CategoryName,
    TimeClass TimeClass,
    string Color,
    bool RequiresPhoto,
    bool RequiresSignature,
    ObservationPolicy Observation);

public sealed record ActivityOptionsDto(
    IReadOnlyList<AssignedOptionDto> Assigned,
    IReadOnlyList<CatalogOptionDto> Catalog,
    IReadOnlyList<OutcomeDto> Outcomes);

public sealed record EvidenceDto(Guid Id, Guid ActivityId, EvidenceKind Kind, long SizeBytes, DateTimeOffset CapturedAt);
