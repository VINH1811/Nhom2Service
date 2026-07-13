namespace WLPRO.HRM.Contracts;

public sealed record EmployeeChangedEvent(
    Guid EventId,
    DateTime OccurredAt,
    string ChangeType,
    Guid EmployeeId,
    string EmployeeCode,
    string FullName,
    string Email,
    DateOnly HireDate,
    Guid DepartmentId,
    string DepartmentName,
    string PositionCode,
    string PositionName,
    decimal PositionCoefficient,
    string ContractTypeCode,
    string ContractTypeName,
    decimal ContractBaseSalary,
    string EmploymentStatus);

public sealed record AttendanceSummaryContract(
    Guid EmployeeId,
    Guid DepartmentId,
    int StandardShifts,
    int RecognizedShifts,
    int PaidLeaveShifts,
    int EventLeaveShifts,
    int UnauthorizedAbsentShifts,
    int LateOccurrences,
    int LateMinutes,
    int EarlyLeaveOccurrences,
    int EarlyLeaveMinutes,
    int MissingCheckoutRejectedCount,
    decimal OvertimeHours,
    int OvertimeNightShifts,
    decimal WeekendHours,
    decimal MeetingPenaltyDays);

public sealed record MonthlyAttendanceClosedEvent(
    Guid EventId,
    DateTime OccurredAt,
    string MonthYear,
    int Version,
    IReadOnlyList<AttendanceSummaryContract> Summaries);
