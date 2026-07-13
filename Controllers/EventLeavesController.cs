using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nhom2Service.Constants;
using Nhom2Service.Data;
using Nhom2Service.DTOs;
using Nhom2Service.Entities;
using System.Security.Claims;

namespace Nhom2Service.Controllers;

[ApiController, Route("api/event-leaves"), Authorize]
public sealed class EventLeavesController(AppDbContext db) : ControllerBase
{
    [Authorize(Roles = Roles.Admin + "," + Roles.Manager), HttpPost]
    public async Task<IActionResult> Create(CreateEventLeaveDto dto)
    {
        if (dto.EndDate < dto.StartDate) return BadRequest("Thời gian nghỉ sự kiện không hợp lệ.");
        if (dto.StartDate < DateOnly.FromDateTime(DateTime.Today))
            return BadRequest("Không thể đăng ký nghỉ sự kiện cho ngày đã qua.");
        var hasDepartment = Guid.TryParse(User.FindFirstValue("DepartmentId"), out var departmentId);
        if (User.IsInRole(Roles.Admin)) departmentId = dto.DepartmentId ?? Guid.Empty;
        if ((User.IsInRole(Roles.Manager) && !hasDepartment) || departmentId == Guid.Empty) return Forbid();

        var overlap = await db.EventLeaves.AnyAsync(x => x.DepartmentId == departmentId &&
            x.StartDate <= dto.EndDate && x.EndDate >= dto.StartDate &&
            (x.Status == RequestStatuses.Pending || x.Status == RequestStatuses.Approved));
        if (overlap) return Conflict("Phòng ban đã có yêu cầu nghỉ sự kiện trùng thời gian.");

        var request = new EventLeaveRequest
        {
            Id = Guid.NewGuid(), DepartmentId = departmentId, Name = dto.Name.Trim(),
            StartDate = dto.StartDate, EndDate = dto.EndDate, Reason = dto.Reason.Trim(),
            RequestedBy = User.Identity?.Name ?? "unknown",
            Status = User.IsInRole(Roles.Admin) ? RequestStatuses.Approved : RequestStatuses.Pending,
            ApprovedBy = User.IsInRole(Roles.Admin) ? User.Identity?.Name : null
        };
        db.Add(request);
        await db.SaveChangesAsync();
        if (request.Status == RequestStatuses.Approved) await ApplyEventLeaveAsync(request);
        return Ok(request);
    }

    [Authorize(Roles = Roles.Admin), HttpPost("{id:guid}/decision")]
    public async Task<IActionResult> Decide(Guid id, DecideRequestDto dto)
    {
        var request = await db.EventLeaves.FindAsync(id);
        if (request is null) return NotFound();
        if (request.Status != RequestStatuses.Pending) return BadRequest("Yêu cầu nghỉ sự kiện đã được xử lý.");
        request.Status = dto.Approve ? RequestStatuses.Approved : RequestStatuses.Rejected;
        request.ApprovedBy = User.Identity?.Name;
        await db.SaveChangesAsync();
        if (dto.Approve) await ApplyEventLeaveAsync(request);
        return Ok(request);
    }

    [Authorize(Roles = Roles.AdminHrManager), HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? departmentId = null)
    {
        if (User.IsInRole(Roles.Manager))
        {
            if (!Guid.TryParse(User.FindFirstValue("DepartmentId"), out var ownDepartment)) return Forbid();
            departmentId = ownDepartment;
        }
        var query = db.EventLeaves.AsNoTracking().AsQueryable();
        if (departmentId is Guid id) query = query.Where(x => x.DepartmentId == id);
        return Ok(await query.OrderByDescending(x => x.StartDate).ToListAsync());
    }

    private async Task ApplyEventLeaveAsync(EventLeaveRequest request)
    {
        var schedules = await db.WorkSchedules.Where(x => x.DepartmentId == request.DepartmentId &&
            x.WorkDate >= request.StartDate && x.WorkDate <= request.EndDate && x.CountsAsStandardWork && !x.IsCancelled).ToListAsync();
        foreach (var schedule in schedules)
        {
            schedule.ScheduleType = ScheduleTypes.EventLeave;
            schedule.CountsAsPaidWork = true;
            schedule.SourceRequestId = request.Id;
        }
        await db.SaveChangesAsync();
    }
}
