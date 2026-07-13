using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nhom2Service.Constants;
using Nhom2Service.Data;
using System.Security.Claims;

namespace Nhom2Service.Controllers;

[ApiController, Route("api/workforce"), Authorize(Roles = Roles.AdminHrManager)]
public sealed class WorkforceController(AppDbContext db) : ControllerBase
{
    [HttpGet("department/{departmentId:guid}/{monthYear}")]
    public async Task<IActionResult> Department(Guid departmentId, string monthYear)
    {
        if (User.IsInRole(Roles.Manager) && User.FindFirstValue("DepartmentId") != departmentId.ToString()) return Forbid();
        var closure = await db.MonthlyClosures.AsNoTracking().OrderByDescending(x => x.Version).FirstOrDefaultAsync(x => x.MonthYear == monthYear);
        if (closure is null) return NotFound("Chưa có dữ liệu tổng hợp tháng.");
        var rows = await db.MonthlySummaries.AsNoTracking().Where(x => x.ClosureId == closure.Id && x.DepartmentId == departmentId).OrderBy(x => x.EmployeeId).ToListAsync();
        return Ok(new
        {
            closure.MonthYear, closure.Status, closure.Version,
            employeeCount = rows.Count,
            pendingMissingCheckout = await db.Attendances.CountAsync(x => x.Schedule.DepartmentId == departmentId && x.Status == AttendanceStatuses.MissingCheckoutPending),
            totalLateOccurrences = rows.Sum(x => x.LateOccurrences),
            totalAbsentShifts = rows.Sum(x => x.UnauthorizedAbsentShifts),
            totalOvertimeHours = rows.Sum(x => x.OvertimeHours + x.WeekendHours),
            rows
        });
    }

    [HttpGet("pending-actions")]
    public async Task<IActionResult> PendingActions()
    {
        Guid? departmentId = Guid.TryParse(User.FindFirstValue("DepartmentId"), out var dep) ? dep : null;
        var leave = db.LeaveRequests.Where(x => x.Status == RequestStatuses.Pending);
        var overtime = db.OvertimePlans.Where(x => x.Status == RequestStatuses.Pending || x.Status == RequestStatuses.ManagerApproved);
        var missing = db.Attendances.Where(x => x.Status == AttendanceStatuses.MissingCheckoutPending);
        if (User.IsInRole(Roles.Manager) && departmentId is Guid id)
        {
            leave = leave.Where(x => x.DepartmentId == id);
            overtime = overtime.Where(x => x.DepartmentId == id);
            missing = missing.Where(x => x.Schedule.DepartmentId == id);
        }
        return Ok(new { leaveRequests = await leave.CountAsync(), overtimeRequests = await overtime.CountAsync(), missingCheckouts = await missing.CountAsync() });
    }
}
