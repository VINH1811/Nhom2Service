using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nhom2Service.Data;

namespace Nhom2Service.Controllers;

[ApiController]
[Route("api/internal")]
public sealed class InternalController(
    AppDbContext db,
    IConfiguration config) : ControllerBase
{
    [HttpGet("attendance-summary/{monthYear}")]
    public async Task<IActionResult> Summary(string monthYear)
    {
        if (!Request.Headers.TryGetValue("X-Internal-Key", out var key) ||
            key != config["InternalApiKey"])
        {
            return Unauthorized();
        }

        var closure = await db.MonthlyClosures
            .AsNoTracking()
            .Where(item => item.MonthYear == monthYear)
            .OrderByDescending(item => item.Version)
            .Select(item => new
            {
                item.Id,
                item.MonthYear,
                item.Status,
                item.Version,
                item.ClosedAt,
                item.ClosedBy,
                item.ReopenedAt,
                item.ReopenedBy,
                item.ReopenReason,
                Summaries = item.Summaries
                    .OrderBy(summary => summary.EmployeeId)
                    .Select(summary => new
                    {
                        summary.EmployeeId,
                        summary.DepartmentId,
                        summary.StandardShifts,
                        summary.RecognizedShifts,
                        summary.PaidLeaveShifts,
                        summary.EventLeaveShifts,
                        summary.UnauthorizedAbsentShifts,
                        summary.LateOccurrences,
                        summary.LateMinutes,
                        summary.EarlyLeaveOccurrences,
                        summary.EarlyLeaveMinutes,
                        summary.MissingCheckoutRejectedCount,
                        summary.OvertimeHours,
                        summary.OvertimeNightShifts,
                        summary.WeekendHours,
                        summary.MeetingPenaltyDays
                    })
                    .ToList()
            })
            .FirstOrDefaultAsync();

        return closure is null ? NotFound() : Ok(closure);
    }
}
