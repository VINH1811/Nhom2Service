using System.ComponentModel.DataAnnotations;

namespace Nhom2Service.DTOs;

public sealed record FaceAttendanceDto(
    [Required] double[] Descriptor,
    string ModelVersion = "face-api.js-128");

public sealed record CreateLeaveRequestDto(
    DateOnly StartDate,
    DateOnly EndDate,
    int RequestedShifts,
    [Required] string Reason);

public sealed record DecideRequestDto(bool Approve, string? Reason);

public sealed class CreateOvertimePlanDto
{
    public Guid? DepartmentId { get; set; }
    public DateOnly WorkDate { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    [Required] public string Kind { get; set; } = "Hourly";
    [Required] public string Reason { get; set; } = string.Empty;
    public List<Guid> EmployeeIds { get; set; } = [];
}

public sealed class CreateMeetingDto
{
    public Guid? DepartmentId { get; set; }
    [Required] public string Title { get; set; } = string.Empty;
    public DateOnly MeetingDate { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    [Required] public string Location { get; set; } = string.Empty;
    [Required] public string Objective { get; set; } = string.Empty;
    public string? PresentationRequirement { get; set; }
    public List<Guid> EmployeeIds { get; set; } = [];
    public bool ContinueWhenPeopleOnLeave { get; set; }
}

public sealed record MeetingResponseDto(bool Attend, string? DeclineReason);
public sealed record MeetingPenaltyDecisionDto(bool AcceptDecline, decimal PenaltyDays);
public sealed record CreateEventLeaveDto(
    [Required] string Name,
    DateOnly StartDate,
    DateOnly EndDate,
    [Required] string Reason,
    Guid? DepartmentId = null);
public sealed record ReopenAttendanceMonthDto([Required] string Reason);
