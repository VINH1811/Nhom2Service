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
[Route("api/overtime")]
[Authorize]
public sealed class OvertimeController(AppDbContext db) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateOvertimePlanDto dto)
    {
        if (dto.EndTime <= dto.StartTime)
        {
            return BadRequest("Giờ tăng ca không hợp lệ.");
        }

        if (dto.WorkDate < DateOnly.FromDateTime(DateTime.Today))
        {
            return BadRequest("Không thể đăng ký tăng ca cho ngày đã qua.");
        }

        if (dto.Kind is not ("Hourly" or "NightShift" or "Weekend"))
        {
            return BadRequest("Loại tăng ca không hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(dto.Reason))
        {
            return BadRequest("Phải nhập lý do tăng ca.");
        }

        var isAdmin = User.IsInRole(Roles.Admin);
        var isManager = User.IsInRole(Roles.Manager);
        var hasEmployee = Guid.TryParse(
            User.FindFirstValue("EmployeeId"),
            out var requester);
        var hasDepartment = Guid.TryParse(
            User.FindFirstValue("DepartmentId"),
            out var departmentId);

        if (isAdmin)
        {
            departmentId = dto.DepartmentId ?? Guid.Empty;
            requester = hasEmployee ? requester : Guid.Empty;
        }

        if ((!hasEmployee && !isAdmin) ||
            (!hasDepartment && !isAdmin) ||
            departmentId == Guid.Empty)
        {
            return Forbid();
        }

        var canAssignOthers = isManager || isAdmin;
        var employeeIds = dto.EmployeeIds.Count > 0
            ? dto.EmployeeIds.Distinct().ToList()
            : hasEmployee
                ? new List<Guid> { requester }
                : [];

        if (employeeIds.Count == 0)
        {
            return BadRequest("Phải chọn nhân viên tăng ca.");
        }

        if (!canAssignOthers &&
            employeeIds.Any(employeeId => employeeId != requester))
        {
            return Forbid();
        }

        var validEmployees = await db.WorkSchedules
            .AsNoTracking()
            .Where(schedule =>
                employeeIds.Contains(schedule.EmployeeId) &&
                schedule.DepartmentId == departmentId)
            .Select(schedule => schedule.EmployeeId)
            .Distinct()
            .ToListAsync();

        var invalidEmployees = employeeIds.Except(validEmployees).ToList();

        if (invalidEmployees.Count > 0)
        {
            return BadRequest(new
            {
                message = "Có nhân viên không thuộc phòng ban đã chọn.",
                employeeIds = invalidEmployees
            });
        }

        var conflictIds = await db.OvertimeParticipants
            .AsNoTracking()
            .Where(participant =>
                employeeIds.Contains(participant.EmployeeId) &&
                participant.Plan.WorkDate == dto.WorkDate &&
                participant.Plan.Status != RequestStatuses.Rejected &&
                participant.Plan.StartTime < dto.EndTime &&
                participant.Plan.EndTime > dto.StartTime)
            .Select(participant => participant.EmployeeId)
            .Distinct()
            .ToListAsync();

        if (conflictIds.Count > 0)
        {
            return Conflict(new
            {
                message = "Có nhân viên đã có lịch tăng ca trùng giờ.",
                employeeIds = conflictIds
            });
        }

        var isWeekend = dto.WorkDate.DayOfWeek is
            DayOfWeek.Saturday or DayOfWeek.Sunday;

        if (isWeekend && dto.Kind != "Weekend")
        {
            return BadRequest(
                "Tăng ca Thứ Bảy hoặc Chủ nhật phải sử dụng loại Weekend.");
        }

        if (!isWeekend && dto.Kind == "Weekend")
        {
            return BadRequest(
                "Loại Weekend chỉ được sử dụng cho Thứ Bảy hoặc Chủ nhật.");
        }

        var initialStatus = isAdmin
            ? RequestStatuses.Approved
            : isManager
                ? isWeekend
                    ? RequestStatuses.ManagerApproved
                    : RequestStatuses.Approved
                : RequestStatuses.Pending;

        var plan = new OvertimePlan
        {
            Id = Guid.NewGuid(),
            DepartmentId = departmentId,
            RequestedByEmployeeId = requester,
            WorkDate = dto.WorkDate,
            StartTime = dto.StartTime,
            EndTime = dto.EndTime,
            Kind = dto.Kind,
            Reason = dto.Reason.Trim(),
            RequiresAdminApproval = isWeekend,
            Status = initialStatus,
            ManagerApprovedBy = isManager ? User.Identity?.Name : null,
            AdminApprovedBy = isAdmin ? User.Identity?.Name : null,
            Participants = employeeIds
                .Select(employeeId => new OvertimeParticipant
                {
                    EmployeeId = employeeId
                })
                .ToList()
        };

        db.OvertimePlans.Add(plan);

        db.Notifications.AddRange(
            employeeIds.Select(employeeId => new Notification
            {
                EmployeeId = employeeId,
                Type = "Overtime",
                Title = "Yêu cầu/lịch tăng ca",
                Message =
                    $"{dto.WorkDate:dd/MM/yyyy} " +
                    $"{dto.StartTime:HH\\:mm}-{dto.EndTime:HH\\:mm}: " +
                    dto.Reason.Trim(),
                ReferenceId = plan.Id
            }));

        await db.SaveChangesAsync();

        if (plan.Status == RequestStatuses.Approved)
        {
            await CreateSchedulesAsync(plan);
        }

        return Ok(ToPlanResponse(plan));
    }

    [HttpGet("mine")]
    public async Task<IActionResult> Mine()
    {
        if (!Guid.TryParse(
                User.FindFirstValue("EmployeeId"),
                out var employeeId))
        {
            return Ok(Array.Empty<object>());
        }

        var result = await db.OvertimeParticipants
            .AsNoTracking()
            .Where(participant => participant.EmployeeId == employeeId)
            .OrderByDescending(participant => participant.Plan.WorkDate)
            .ThenByDescending(participant => participant.Plan.StartTime)
            .Select(participant => new
            {
                participant.Id,
                participant.OvertimePlanId,
                participant.EmployeeId,
                participant.Completed,
                Plan = new
                {
                    participant.Plan.Id,
                    participant.Plan.DepartmentId,
                    participant.Plan.RequestedByEmployeeId,
                    participant.Plan.WorkDate,
                    participant.Plan.StartTime,
                    participant.Plan.EndTime,
                    participant.Plan.Kind,
                    participant.Plan.Reason,
                    participant.Plan.Status,
                    participant.Plan.RequiresAdminApproval,
                    participant.Plan.ManagerApprovedBy,
                    participant.Plan.AdminApprovedBy,
                    Participants = participant.Plan.Participants
                        .Select(item => new
                        {
                            item.Id,
                            item.OvertimePlanId,
                            item.EmployeeId,
                            item.Completed
                        })
                        .ToList()
                }
            })
            .ToListAsync();

        return Ok(result);
    }

    [Authorize(Roles = Roles.AdminHrManager)]
    [HttpGet("department/{departmentId:guid}")]
    public async Task<IActionResult> Department(Guid departmentId)
    {
        if (User.IsInRole(Roles.Manager) &&
            (!Guid.TryParse(
                 User.FindFirstValue("DepartmentId"),
                 out var ownDepartmentId) ||
             ownDepartmentId != departmentId))
        {
            return Forbid();
        }

        var result = await db.OvertimePlans
            .AsNoTracking()
            .Where(plan => plan.DepartmentId == departmentId)
            .OrderByDescending(plan => plan.WorkDate)
            .ThenByDescending(plan => plan.StartTime)
            .Select(plan => new
            {
                plan.Id,
                plan.DepartmentId,
                plan.RequestedByEmployeeId,
                plan.WorkDate,
                plan.StartTime,
                plan.EndTime,
                plan.Kind,
                plan.Reason,
                plan.Status,
                plan.RequiresAdminApproval,
                plan.ManagerApprovedBy,
                plan.AdminApprovedBy,
                Participants = plan.Participants
                    .Select(participant => new
                    {
                        participant.Id,
                        participant.OvertimePlanId,
                        participant.EmployeeId,
                        participant.Completed
                    })
                    .ToList()
            })
            .ToListAsync();

        return Ok(result);
    }

    [Authorize(Roles = Roles.Admin + "," + Roles.Manager)]
    [HttpPost("{id:guid}/decision")]
    public async Task<IActionResult> Decide(
        Guid id,
        DecideRequestDto dto)
    {
        var plan = await db.OvertimePlans
            .Include(item => item.Participants)
            .FirstOrDefaultAsync(item => item.Id == id);

        if (plan is null)
        {
            return NotFound();
        }

        if (plan.Status is
            RequestStatuses.Approved or RequestStatuses.Rejected)
        {
            return BadRequest("Yêu cầu tăng ca đã được xử lý.");
        }

        if (User.IsInRole(Roles.Manager))
        {
            if (!Guid.TryParse(
                    User.FindFirstValue("DepartmentId"),
                    out var departmentId) ||
                departmentId != plan.DepartmentId)
            {
                return Forbid();
            }

            plan.ManagerApprovedBy = User.Identity?.Name;
            plan.Status = dto.Approve
                ? plan.RequiresAdminApproval
                    ? RequestStatuses.ManagerApproved
                    : RequestStatuses.Approved
                : RequestStatuses.Rejected;
        }
        else
        {
            plan.AdminApprovedBy = User.Identity?.Name;
            plan.Status = dto.Approve
                ? RequestStatuses.Approved
                : RequestStatuses.Rejected;
        }

        await db.SaveChangesAsync();

        if (plan.Status == RequestStatuses.Approved)
        {
            await CreateSchedulesAsync(plan);
        }

        return Ok(ToPlanResponse(plan));
    }

    private async Task CreateSchedulesAsync(OvertimePlan plan)
    {
        var shiftCode = plan.Kind == "NightShift"
            ? "EVENING"
            : "MORNING";

        var shift = await db.Shifts
            .FirstOrDefaultAsync(item => item.Code == shiftCode);

        if (shift is null)
        {
            throw new InvalidOperationException(
                $"Không tìm thấy ca làm có mã {shiftCode}.");
        }

        foreach (var participant in plan.Participants)
        {
            var exists = await db.WorkSchedules.AnyAsync(schedule =>
                schedule.EmployeeId == participant.EmployeeId &&
                schedule.WorkDate == plan.WorkDate &&
                schedule.SourceRequestId == plan.Id);

            if (exists)
            {
                continue;
            }

            db.WorkSchedules.Add(new WorkSchedule
            {
                Id = Guid.NewGuid(),
                EmployeeId = participant.EmployeeId,
                DepartmentId = plan.DepartmentId,
                WorkDate = plan.WorkDate,
                ShiftId = shift.Id,
                PlannedStartTime = plan.StartTime,
                PlannedEndTime = plan.EndTime,
                ScheduleType = plan.RequiresAdminApproval
                    ? ScheduleTypes.Weekend
                    : ScheduleTypes.Overtime,
                CountsAsStandardWork = false,
                SourceRequestId = plan.Id,
                Note = plan.Reason
            });
        }

        await db.SaveChangesAsync();
    }

    private static object ToPlanResponse(OvertimePlan plan)
    {
        return new
        {
            plan.Id,
            plan.DepartmentId,
            plan.RequestedByEmployeeId,
            plan.WorkDate,
            plan.StartTime,
            plan.EndTime,
            plan.Kind,
            plan.Reason,
            plan.Status,
            plan.RequiresAdminApproval,
            plan.ManagerApprovedBy,
            plan.AdminApprovedBy,
            Participants = plan.Participants
                .Select(participant => new
                {
                    participant.Id,
                    participant.OvertimePlanId,
                    participant.EmployeeId,
                    participant.Completed
                })
                .ToList()
        };
    }
}
