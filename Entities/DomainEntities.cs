using System.ComponentModel.DataAnnotations;
namespace Nhom2Service.Entities;

public sealed class Shift
{
    public int Id { get; set; }
    [MaxLength(30)] public string Code { get; set; } = string.Empty;
    [MaxLength(100)] public string Name { get; set; } = string.Empty;
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int CheckInEarlyMinutes { get; set; } = 30;
    public int GraceMinutes { get; set; } = 10;
    public int CheckoutDeadlineMinutes { get; set; } = 60;
    public bool IsOvertimeShift { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class WorkSchedule
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid DepartmentId { get; set; }
    public DateOnly WorkDate { get; set; }
    public int ShiftId { get; set; }
    public Shift Shift { get; set; } = null!;
    public TimeOnly? PlannedStartTime { get; set; }
    public TimeOnly? PlannedEndTime { get; set; }
    [MaxLength(30)] public string ScheduleType { get; set; } = "Regular";
    public bool CountsAsStandardWork { get; set; } = true;
    public bool CountsAsPaidWork { get; set; }
    public Guid? SourceRequestId { get; set; }
    [MaxLength(500)] public string? Note { get; set; }
    public bool IsCancelled { get; set; }
}

public sealed class AttendanceRecord
{
    public Guid Id { get; set; }
    public Guid ScheduleId { get; set; }
    public WorkSchedule Schedule { get; set; } = null!;
    public Guid EmployeeId { get; set; }
    public DateTime? CheckInAt { get; set; }
    public DateTime? CheckOutAt { get; set; }
    [MaxLength(40)] public string Status { get; set; } = "Present";
    public int LateMinutes { get; set; }
    public int EarlyLeaveMinutes { get; set; }
    public decimal OvertimeHours { get; set; }
    [MaxLength(30)] public string? OvertimeKind { get; set; }
    public double? FaceDistance { get; set; }
    [MaxLength(500)] public string? MissingCheckoutReason { get; set; }
    [MaxLength(200)] public string? ResolvedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public sealed class LeaveRequest
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid DepartmentId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int RequestedShifts { get; set; }
    public bool IsPaid { get; set; } = true;
    [MaxLength(500)] public string Reason { get; set; } = string.Empty;
    [MaxLength(30)] public string Status { get; set; } = "Pending";
    [MaxLength(200)] public string? ApprovedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedAt { get; set; }
}

public sealed class OvertimePlan
{
    public Guid Id { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid RequestedByEmployeeId { get; set; }
    public DateOnly WorkDate { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    [MaxLength(30)] public string Kind { get; set; } = "Hourly";
    [MaxLength(500)] public string Reason { get; set; } = string.Empty;
    [MaxLength(30)] public string Status { get; set; } = "Pending";
    public bool RequiresAdminApproval { get; set; }
    [MaxLength(200)] public string? ManagerApprovedBy { get; set; }
    [MaxLength(200)] public string? AdminApprovedBy { get; set; }
    public ICollection<OvertimeParticipant> Participants { get; set; } = new List<OvertimeParticipant>();
}

public sealed class OvertimeParticipant
{
    public long Id { get; set; }
    public Guid OvertimePlanId { get; set; }
    public OvertimePlan Plan { get; set; } = null!;
    public Guid EmployeeId { get; set; }
    public bool Completed { get; set; }
}

public sealed class EventLeaveRequest
{
    public Guid Id { get; set; }
    public Guid DepartmentId { get; set; }
    [MaxLength(200)] public string Name { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    [MaxLength(500)] public string Reason { get; set; } = string.Empty;
    [MaxLength(30)] public string Status { get; set; } = "Pending";
    [MaxLength(200)] public string RequestedBy { get; set; } = string.Empty;
    [MaxLength(200)] public string? ApprovedBy { get; set; }
}

public sealed class Meeting
{
    public Guid Id { get; set; }
    public Guid DepartmentId { get; set; }
    [MaxLength(200)] public string Title { get; set; } = string.Empty;
    public DateOnly MeetingDate { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    [MaxLength(300)] public string Location { get; set; } = string.Empty;
    [MaxLength(1000)] public string Objective { get; set; } = string.Empty;
    [MaxLength(1000)] public string? PresentationRequirement { get; set; }
    [MaxLength(200)] public string CreatedBy { get; set; } = string.Empty;
    public ICollection<MeetingParticipant> Participants { get; set; } = new List<MeetingParticipant>();
}

public sealed class MeetingParticipant
{
    public long Id { get; set; }
    public Guid MeetingId { get; set; }
    public Meeting Meeting { get; set; } = null!;
    public Guid EmployeeId { get; set; }
    [MaxLength(30)] public string Response { get; set; } = "Pending";
    [MaxLength(500)] public string? DeclineReason { get; set; }
    public bool? DeclineAccepted { get; set; }
    public decimal PenaltyDays { get; set; }
    [MaxLength(200)] public string? DecidedBy { get; set; }
}

public sealed class MonthlyAttendanceClosure
{
    public Guid Id { get; set; }
    [MaxLength(7)] public string MonthYear { get; set; } = string.Empty;
    [MaxLength(30)] public string Status { get; set; } = "Open";
    public DateTime? ClosedAt { get; set; }
    [MaxLength(200)] public string? ClosedBy { get; set; }
    public DateTime? ReopenedAt { get; set; }
    [MaxLength(200)] public string? ReopenedBy { get; set; }
    [MaxLength(500)] public string? ReopenReason { get; set; }
    public int Version { get; set; } = 1;
    public ICollection<MonthlyAttendanceSummary> Summaries { get; set; } = new List<MonthlyAttendanceSummary>();
}

public sealed class MonthlyAttendanceSummary
{
    public long Id { get; set; }
    public Guid ClosureId { get; set; }
    public MonthlyAttendanceClosure Closure { get; set; } = null!;
    public Guid EmployeeId { get; set; }
    public Guid DepartmentId { get; set; }
    public int StandardShifts { get; set; }
    public int RecognizedShifts { get; set; }
    public int PaidLeaveShifts { get; set; }
    public int EventLeaveShifts { get; set; }
    public int UnauthorizedAbsentShifts { get; set; }
    public int LateOccurrences { get; set; }
    public int LateMinutes { get; set; }
    public int EarlyLeaveOccurrences { get; set; }
    public int EarlyLeaveMinutes { get; set; }
    public int MissingCheckoutRejectedCount { get; set; }
    public decimal OvertimeHours { get; set; }
    public int OvertimeNightShifts { get; set; }
    public decimal WeekendHours { get; set; }
    public decimal MeetingPenaltyDays { get; set; }
}


public sealed class Notification
{
    public long Id { get; set; }
    public Guid EmployeeId { get; set; }
    [MaxLength(50)] public string Type { get; set; } = string.Empty;
    [MaxLength(200)] public string Title { get; set; } = string.Empty;
    [MaxLength(1000)] public string Message { get; set; } = string.Empty;
    public Guid? ReferenceId { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}
