using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nhom2Service.Constants;
using Nhom2Service.Data;
using Nhom2Service.DTOs;
using Nhom2Service.Entities;
using System.Security.Claims;

namespace Nhom2Service.Controllers;

[ApiController, Route("api/leave-requests"), Authorize]
public sealed class LeaveRequestsController(AppDbContext db) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateLeaveRequestDto dto)
    {
        if (!Guid.TryParse(User.FindFirstValue("EmployeeId"), out var employeeId) ||
            !Guid.TryParse(User.FindFirstValue("DepartmentId"), out var departmentId)) return Forbid();
        if (dto.EndDate < dto.StartDate || dto.RequestedShifts < 1) return BadRequest("Thời gian hoặc số ca nghỉ không hợp lệ.");
        if (dto.StartDate.Year != dto.EndDate.Year || dto.StartDate.Month != dto.EndDate.Month)
            return BadRequest("Đơn nghỉ phải nằm trong cùng một tháng.");
        if (dto.StartDate < DateOnly.FromDateTime(DateTime.Today)) return BadRequest("Không thể tạo đơn nghỉ cho ngày đã qua.");

        var overlap = await db.LeaveRequests.AnyAsync(x => x.EmployeeId == employeeId &&
            x.StartDate <= dto.EndDate && x.EndDate >= dto.StartDate &&
            (x.Status == RequestStatuses.Pending || x.Status == RequestStatuses.Approved));
        if (overlap) return Conflict("Đã có đơn nghỉ trùng thời gian.");

        var availableShifts = await db.WorkSchedules.CountAsync(x => x.EmployeeId == employeeId &&
            x.WorkDate >= dto.StartDate && x.WorkDate <= dto.EndDate && x.CountsAsStandardWork &&
            !x.IsCancelled && !x.CountsAsPaidWork);
        if (dto.RequestedShifts > availableShifts)
            return BadRequest($"Khoảng ngày đã chọn chỉ có {availableShifts} ca làm có thể xin nghỉ.");

        var used = await db.LeaveRequests.Where(x => x.EmployeeId == employeeId &&
                x.StartDate.Year == dto.StartDate.Year && x.StartDate.Month == dto.StartDate.Month &&
                x.IsPaid && (x.Status == RequestStatuses.Approved || x.Status == RequestStatuses.Pending))
            .SumAsync(x => (int?)x.RequestedShifts) ?? 0;
        if (used + dto.RequestedShifts > 6)
            return BadRequest("Vượt hạn mức 3 ngày/6 ca nghỉ có lương trong tháng.");

        var managerSelfLeave = User.IsInRole(Roles.Manager);
        var request = new LeaveRequest
        {
            Id = Guid.NewGuid(), EmployeeId = employeeId, DepartmentId = departmentId,
            StartDate = dto.StartDate, EndDate = dto.EndDate, RequestedShifts = dto.RequestedShifts,
            Reason = dto.Reason.Trim(),
            Status = managerSelfLeave ? RequestStatuses.Approved : RequestStatuses.Pending,
            ApprovedBy = managerSelfLeave ? "AutoPolicy" : null,
            DecidedAt = managerSelfLeave ? DateTime.UtcNow : null
        };
        db.Add(request);
        await db.SaveChangesAsync();
        if (managerSelfLeave) await ApplyPaidLeaveAsync(request);
        return Ok(request);
    }

    [Authorize(Roles = Roles.AdminHrManager), HttpPost("{id:guid}/decision")]
    public async Task<IActionResult> Decide(Guid id, DecideRequestDto dto)
    {
        var request = await db.LeaveRequests.FindAsync(id);
        if (request is null) return NotFound();
        if (request.Status != RequestStatuses.Pending) return BadRequest("Đơn nghỉ đã được xử lý.");
        if (User.IsInRole(Roles.Manager) &&
            (!Guid.TryParse(User.FindFirstValue("DepartmentId"), out var departmentId) || departmentId != request.DepartmentId))
            return Forbid();

        request.Status = dto.Approve ? RequestStatuses.Approved : RequestStatuses.Rejected;
        request.ApprovedBy = User.Identity?.Name;
        request.DecidedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        if (dto.Approve) await ApplyPaidLeaveAsync(request);
        return Ok(request);
    }

    [HttpGet("mine")]
    public async Task<IActionResult> Mine()
    {
        if (!Guid.TryParse(User.FindFirstValue("EmployeeId"), out var employeeId)) return Forbid();
        return Ok(await db.LeaveRequests.AsNoTracking().Where(x => x.EmployeeId == employeeId)
            .OrderByDescending(x => x.CreatedAt).ToListAsync());
    }

    [Authorize(Roles = Roles.AdminHrManager), HttpGet("department/{departmentId:guid}")]
    public async Task<IActionResult> Department(Guid departmentId, [FromQuery] string? status = null)
    {
        if (User.IsInRole(Roles.Manager) &&
            (!Guid.TryParse(User.FindFirstValue("DepartmentId"), out var ownDepartmentId) || ownDepartmentId != departmentId))
            return Forbid();
        var query = db.LeaveRequests.AsNoTracking().Where(x => x.DepartmentId == departmentId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        return Ok(await query.OrderByDescending(x => x.CreatedAt).ToListAsync());
    }

    private async Task ApplyPaidLeaveAsync(LeaveRequest leave)
    {
        var schedules = await db.WorkSchedules.Where(x => x.EmployeeId == leave.EmployeeId &&
                x.WorkDate >= leave.StartDate && x.WorkDate <= leave.EndDate &&
                x.CountsAsStandardWork && !x.IsCancelled && !x.CountsAsPaidWork)
            .OrderBy(x => x.WorkDate).ThenBy(x => x.ShiftId).Take(leave.RequestedShifts).ToListAsync();
        foreach (var schedule in schedules)
        {
            schedule.ScheduleType = ScheduleTypes.PaidLeave;
            schedule.CountsAsPaidWork = true;
            schedule.SourceRequestId = leave.Id;
        }
        await db.SaveChangesAsync();
    }
}
