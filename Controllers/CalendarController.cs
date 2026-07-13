using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nhom2Service.Constants;
using Nhom2Service.Data;
using System.Security.Claims;

namespace Nhom2Service.Controllers;

[ApiController, Route("api/calendar"), Authorize]
public sealed class CalendarController(AppDbContext db) : ControllerBase
{
    [HttpGet("mine")]
    public async Task<IActionResult> Mine(DateOnly from, DateOnly to)
    {
        if (!Guid.TryParse(User.FindFirstValue("EmployeeId"), out var employeeId)) return Forbid();
        if (to < from) return BadRequest("Khoảng ngày không hợp lệ.");

        return Ok(await db.WorkSchedules.AsNoTracking().Include(x => x.Shift)
            .Where(x => x.EmployeeId == employeeId && x.WorkDate >= from && x.WorkDate <= to && !x.IsCancelled)
            .OrderBy(x => x.WorkDate).ThenBy(x => x.Shift.StartTime)
            .ToListAsync());
    }

    [Authorize(Roles = Roles.AdminHrManager), HttpGet("department/{departmentId:guid}")]
    public async Task<IActionResult> Department(Guid departmentId, DateOnly from, DateOnly to)
    {
        if (to < from) return BadRequest("Khoảng ngày không hợp lệ.");
        if (User.IsInRole(Roles.Manager) &&
            (!Guid.TryParse(User.FindFirstValue("DepartmentId"), out var ownDepartmentId) ||
             ownDepartmentId != departmentId))
            return Forbid();

        return Ok(await db.WorkSchedules.AsNoTracking().Include(x => x.Shift)
            .Where(x => x.DepartmentId == departmentId && x.WorkDate >= from && x.WorkDate <= to && !x.IsCancelled)
            .OrderBy(x => x.WorkDate).ThenBy(x => x.Shift.StartTime)
            .ToListAsync());
    }
}
