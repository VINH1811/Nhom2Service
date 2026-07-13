using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nhom2Service.Constants;
using Nhom2Service.Data;
using Nhom2Service.DTOs;
using Nhom2Service.Entities;
using System.Security.Claims;

namespace Nhom2Service.Controllers;

[ApiController]
[Route("api/meetings")]
[Authorize]
public sealed class MeetingsController(AppDbContext db) : ControllerBase
{
    [Authorize(Roles = Roles.Admin + "," + Roles.Manager)]
    [HttpPost]
    public async Task<IActionResult> Create(CreateMeetingDto dto)
    {
        var hasDepartment = Guid.TryParse(
            User.FindFirstValue("DepartmentId"),
            out var departmentId);

        if (User.IsInRole(Roles.Admin))
        {
            departmentId = dto.DepartmentId ?? Guid.Empty;
        }

        if ((User.IsInRole(Roles.Manager) && !hasDepartment) ||
            departmentId == Guid.Empty)
        {
            return Forbid();
        }

        if (dto.EmployeeIds.Count == 0)
        {
            return BadRequest("Phải chọn ít nhất một người tham gia.");
        }

        if (dto.MeetingDate < DateOnly.FromDateTime(DateTime.Today))
        {
            return BadRequest("Không thể tạo cuộc họp cho ngày đã qua.");
        }

        if (dto.EndTime <= dto.StartTime)
        {
            return BadRequest("Giờ họp không hợp lệ.");
        }

        var inMorning =
            dto.StartTime >= new TimeOnly(8, 0) &&
            dto.EndTime <= new TimeOnly(12, 0);

        var inAfternoon =
            dto.StartTime >= new TimeOnly(13, 30) &&
            dto.EndTime <= new TimeOnly(17, 30);

        if (!inMorning && !inAfternoon)
        {
            return BadRequest("Cuộc họp phải nằm trọn trong ca làm việc.");
        }

        var selectedIds = dto.EmployeeIds.Distinct().ToList();

        var scheduledPeople = await db.WorkSchedules
            .AsNoTracking()
            .Where(schedule =>
                selectedIds.Contains(schedule.EmployeeId) &&
                schedule.DepartmentId == departmentId &&
                schedule.WorkDate == dto.MeetingDate &&
                schedule.CountsAsStandardWork &&
                !schedule.IsCancelled)
            .Select(schedule => schedule.EmployeeId)
            .Distinct()
            .ToListAsync();

        var invalidPeople = selectedIds.Except(scheduledPeople).ToList();

        if (invalidPeople.Count > 0)
        {
            return BadRequest(new
            {
                message =
                    "Có người không thuộc phòng hoặc không có lịch làm trong ngày họp.",
                employeeIds = invalidPeople
            });
        }

        var conflictingPeople = await db.MeetingParticipants
            .AsNoTracking()
            .Where(participant =>
                selectedIds.Contains(participant.EmployeeId) &&
                participant.Meeting.MeetingDate == dto.MeetingDate &&
                participant.Meeting.StartTime < dto.EndTime &&
                participant.Meeting.EndTime > dto.StartTime)
            .Select(participant => participant.EmployeeId)
            .Distinct()
            .ToListAsync();

        if (conflictingPeople.Count > 0)
        {
            return Conflict(new
            {
                message = "Có nhân viên bị trùng lịch họp.",
                employeeIds = conflictingPeople
            });
        }

        var leavePeople = await db.WorkSchedules
            .AsNoTracking()
            .Where(schedule =>
                selectedIds.Contains(schedule.EmployeeId) &&
                schedule.DepartmentId == departmentId &&
                schedule.WorkDate == dto.MeetingDate &&
                schedule.CountsAsPaidWork)
            .Select(schedule => schedule.EmployeeId)
            .Distinct()
            .ToListAsync();

        if (leavePeople.Count > 0 && !dto.ContinueWhenPeopleOnLeave)
        {
            return Conflict(new
            {
                message = "Có nhân viên đang nghỉ phép hoặc nghỉ sự kiện.",
                employeeIds = leavePeople
            });
        }

        // Người đang nghỉ hợp lệ được loại khỏi lời mời và tuyệt đối không bị phạt.
        var participants = selectedIds
            .Except(leavePeople)
            .Select(employeeId => new MeetingParticipant
            {
                EmployeeId = employeeId
            })
            .ToList();

        if (participants.Count == 0)
        {
            return BadRequest(
                "Không còn người có thể tham gia sau khi loại các nhân viên đang nghỉ.");
        }

        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            DepartmentId = departmentId,
            Title = dto.Title.Trim(),
            MeetingDate = dto.MeetingDate,
            StartTime = dto.StartTime,
            EndTime = dto.EndTime,
            Location = dto.Location.Trim(),
            Objective = dto.Objective.Trim(),
            PresentationRequirement = dto.PresentationRequirement?.Trim(),
            CreatedBy = User.Identity?.Name ?? "unknown",
            Participants = participants
        };

        db.Meetings.Add(meeting);

        db.Notifications.AddRange(
            participants.Select(participant => new Notification
            {
                EmployeeId = participant.EmployeeId,
                Type = "Meeting",
                Title = $"Lịch họp: {meeting.Title}",
                Message =
                    $"{meeting.MeetingDate:dd/MM/yyyy} " +
                    $"{meeting.StartTime:HH\\:mm}-{meeting.EndTime:HH\\:mm} " +
                    $"tại {meeting.Location}",
                ReferenceId = meeting.Id
            }));

        await db.SaveChangesAsync();

        return Ok(new
        {
            meeting = ToMeetingResponse(meeting),
            excludedOnLeave = leavePeople
        });
    }

    [HttpGet("mine")]
    public async Task<IActionResult> Mine()
    {
        if (!Guid.TryParse(
                User.FindFirstValue("EmployeeId"),
                out var employeeId))
        {
            return Forbid();
        }

        var result = await db.MeetingParticipants
            .AsNoTracking()
            .Where(participant => participant.EmployeeId == employeeId)
            .OrderByDescending(participant => participant.Meeting.MeetingDate)
            .ThenByDescending(participant => participant.Meeting.StartTime)
            .Select(participant => new
            {
                participant.Id,
                participant.MeetingId,
                participant.EmployeeId,
                participant.Response,
                participant.DeclineReason,
                participant.DeclineAccepted,
                participant.PenaltyDays,
                participant.DecidedBy,
                Meeting = new
                {
                    participant.Meeting.Id,
                    participant.Meeting.DepartmentId,
                    participant.Meeting.Title,
                    participant.Meeting.MeetingDate,
                    participant.Meeting.StartTime,
                    participant.Meeting.EndTime,
                    participant.Meeting.Location,
                    participant.Meeting.Objective,
                    participant.Meeting.PresentationRequirement,
                    participant.Meeting.CreatedBy
                }
            })
            .ToListAsync();

        return Ok(result);
    }

    [Authorize(Roles = Roles.AdminHrManager)]
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id)
    {
        var header = await db.Meetings
            .AsNoTracking()
            .Where(meeting => meeting.Id == id)
            .Select(meeting => new
            {
                meeting.Id,
                meeting.DepartmentId
            })
            .FirstOrDefaultAsync();

        if (header is null)
        {
            return NotFound();
        }

        if (User.IsInRole(Roles.Manager) &&
            (!Guid.TryParse(
                 User.FindFirstValue("DepartmentId"),
                 out var departmentId) ||
             departmentId != header.DepartmentId))
        {
            return Forbid();
        }

        var meeting = await db.Meetings
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new
            {
                item.Id,
                item.DepartmentId,
                item.Title,
                item.MeetingDate,
                item.StartTime,
                item.EndTime,
                item.Location,
                item.Objective,
                item.PresentationRequirement,
                item.CreatedBy,
                Participants = item.Participants
                    .OrderBy(participant => participant.EmployeeId)
                    .Select(participant => new
                    {
                        participant.Id,
                        participant.MeetingId,
                        participant.EmployeeId,
                        participant.Response,
                        participant.DeclineReason,
                        participant.DeclineAccepted,
                        participant.PenaltyDays,
                        participant.DecidedBy
                    })
                    .ToList()
            })
            .FirstAsync();

        return Ok(meeting);
    }

    [HttpPost("{meetingId:guid}/response")]
    public async Task<IActionResult> Respond(
        Guid meetingId,
        MeetingResponseDto dto)
    {
        if (!Guid.TryParse(
                User.FindFirstValue("EmployeeId"),
                out var employeeId))
        {
            return Forbid();
        }

        var participant = await db.MeetingParticipants
            .Include(item => item.Meeting)
            .FirstOrDefaultAsync(item =>
                item.MeetingId == meetingId &&
                item.EmployeeId == employeeId);

        if (participant is null)
        {
            return NotFound();
        }

        if (participant.Meeting.MeetingDate <
            DateOnly.FromDateTime(DateTime.Today))
        {
            return BadRequest("Cuộc họp đã diễn ra.");
        }

        if (!dto.Attend && string.IsNullOrWhiteSpace(dto.DeclineReason))
        {
            return BadRequest("Từ chối phải nêu lý do.");
        }

        participant.Response = dto.Attend ? "Accepted" : "Declined";
        participant.DeclineReason =
            dto.Attend ? null : dto.DeclineReason?.Trim();
        participant.DeclineAccepted = null;
        participant.PenaltyDays = 0;
        participant.DecidedBy = null;

        await db.SaveChangesAsync();

        return Ok(ToParticipantResponse(participant));
    }

    [Authorize(Roles = Roles.AdminHrManager)]
    [HttpPost("participants/{participantId:long}/decision")]
    public async Task<IActionResult> Decide(
        long participantId,
        MeetingPenaltyDecisionDto dto)
    {
        var participant = await db.MeetingParticipants
            .Include(item => item.Meeting)
            .FirstOrDefaultAsync(item => item.Id == participantId);

        if (participant is null)
        {
            return NotFound();
        }

        if (User.IsInRole(Roles.Manager) &&
            (!Guid.TryParse(
                 User.FindFirstValue("DepartmentId"),
                 out var departmentId) ||
             departmentId != participant.Meeting.DepartmentId))
        {
            return Forbid();
        }

        if (participant.Response != "Declined")
        {
            return BadRequest(
                "Chỉ xử lý hình phạt đối với người đã từ chối cuộc họp.");
        }

        participant.DeclineAccepted = dto.AcceptDecline;
        participant.PenaltyDays = dto.AcceptDecline
            ? 0
            : Math.Clamp(dto.PenaltyDays, 0, 1);
        participant.DecidedBy = User.Identity?.Name;

        await db.SaveChangesAsync();

        return Ok(ToParticipantResponse(participant));
    }

    private static object ToMeetingResponse(Meeting meeting)
    {
        return new
        {
            meeting.Id,
            meeting.DepartmentId,
            meeting.Title,
            meeting.MeetingDate,
            meeting.StartTime,
            meeting.EndTime,
            meeting.Location,
            meeting.Objective,
            meeting.PresentationRequirement,
            meeting.CreatedBy,
            Participants = meeting.Participants
                .Select(participant => new
                {
                    participant.Id,
                    participant.MeetingId,
                    participant.EmployeeId,
                    participant.Response,
                    participant.DeclineReason,
                    participant.DeclineAccepted,
                    participant.PenaltyDays,
                    participant.DecidedBy
                })
                .ToList()
        };
    }

    private static object ToParticipantResponse(
        MeetingParticipant participant)
    {
        return new
        {
            participant.Id,
            participant.MeetingId,
            participant.EmployeeId,
            participant.Response,
            participant.DeclineReason,
            participant.DeclineAccepted,
            participant.PenaltyDays,
            participant.DecidedBy,
            Meeting = new
            {
                participant.Meeting.Id,
                participant.Meeting.DepartmentId,
                participant.Meeting.Title,
                participant.Meeting.MeetingDate,
                participant.Meeting.StartTime,
                participant.Meeting.EndTime,
                participant.Meeting.Location,
                participant.Meeting.Objective,
                participant.Meeting.PresentationRequirement,
                participant.Meeting.CreatedBy
            }
        };
    }
}
