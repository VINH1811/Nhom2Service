using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nhom2Service.Data;
using System.Security.Claims;

namespace Nhom2Service.Controllers;

[ApiController, Route("api/notifications"), Authorize]
public sealed class NotificationsController(AppDbContext db) : ControllerBase
{
    [HttpGet("mine")]
    public async Task<IActionResult> Mine([FromQuery] bool unreadOnly = false, [FromQuery] int take = 50)
    {
        if (!Guid.TryParse(User.FindFirstValue("EmployeeId"), out var employeeId)) return Ok(Array.Empty<object>());
        var q = db.Notifications.AsNoTracking().Where(x => x.EmployeeId == employeeId);
        if (unreadOnly) q = q.Where(x => !x.IsRead);
        return Ok(await q.OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync());
    }

    [HttpPost("{id:long}/read")]
    public async Task<IActionResult> MarkRead(long id)
    {
        if (!Guid.TryParse(User.FindFirstValue("EmployeeId"), out var employeeId)) return Forbid();
        var item = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.EmployeeId == employeeId);
        if (item is null) return NotFound(); item.IsRead = true; item.ReadAt = DateTime.UtcNow; await db.SaveChangesAsync(); return Ok();
    }
}
