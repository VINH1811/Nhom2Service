using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nhom2Service.Constants;
using Nhom2Service.Data;
using Nhom2Service.DTOs;
using Nhom2Service.Entities;
using Nhom2Service.Services;
using System.Security.Claims;

namespace Nhom2Service.Controllers;

[ApiController, Route("api/attendances"), Authorize]
public sealed class AttendancesController(AppDbContext db, HrCoreClient hr) : ControllerBase
{
    [HttpPost("check-in/{scheduleId:guid}")]
    public async Task<IActionResult> CheckIn(Guid scheduleId, FaceAttendanceDto dto, CancellationToken ct)
    {
        var employeeId = CurrentEmployeeId();
        if (employeeId is null) return Forbid();

        var schedule = await db.WorkSchedules.Include(x => x.Shift)
            .FirstOrDefaultAsync(x => x.Id == scheduleId && x.EmployeeId == employeeId && !x.IsCancelled, ct);
        if (schedule is null) return NotFound();
        if (schedule.CountsAsPaidWork) return BadRequest("Ca này là nghỉ hưởng lương, không cần chấm công.");
        if (await db.Attendances.AnyAsync(x => x.ScheduleId == scheduleId, ct)) return Conflict("Ca này đã chấm công.");

        var now = DateTime.Now;
        if (schedule.WorkDate != DateOnly.FromDateTime(now)) return BadRequest("Chỉ được chấm công đúng ngày của ca làm việc.");
        var start = schedule.WorkDate.ToDateTime(schedule.PlannedStartTime ?? schedule.Shift.StartTime);
        var end = schedule.WorkDate.ToDateTime(schedule.PlannedEndTime ?? schedule.Shift.EndTime);
        if (now < start.AddMinutes(-schedule.Shift.CheckInEarlyMinutes)) return BadRequest("Chưa đến thời gian chấm công.");
        if (now > end) return BadRequest("Ca làm việc đã kết thúc, không thể check-in muộn.");

        var face = await hr.VerifyFaceAsync(dto.Descriptor, dto.ModelVersion, ct);
        if (!face.matched) return Unauthorized("Xác thực khuôn mặt không thành công.");

        var late = Math.Max(0, (int)(now - start).TotalMinutes - schedule.Shift.GraceMinutes);
        var record = new AttendanceRecord
        {
            Id = Guid.NewGuid(), ScheduleId = schedule.Id, EmployeeId = employeeId.Value,
            CheckInAt = now, Status = AttendanceStatuses.Present, LateMinutes = late,
            FaceDistance = face.distance
        };
        db.Add(record);
        await db.SaveChangesAsync(ct);
        return Ok(record);
    }

    [HttpPost("check-out/{scheduleId:guid}")]
    public async Task<IActionResult> CheckOut(Guid scheduleId, FaceAttendanceDto dto, CancellationToken ct)
    {
        var employeeId = CurrentEmployeeId();
        if (employeeId is null) return Forbid();

        var record = await db.Attendances.Include(x => x.Schedule).ThenInclude(x => x.Shift)
            .FirstOrDefaultAsync(x => x.ScheduleId == scheduleId && x.EmployeeId == employeeId, ct);
        if (record is null) return NotFound();
        if (record.CheckOutAt is not null) return Conflict("Đã check-out.");

        var now = DateTime.Now;
        if (record.Schedule.WorkDate != DateOnly.FromDateTime(now)) return BadRequest("Chỉ được check-out đúng ngày của ca làm việc.");
        var face = await hr.VerifyFaceAsync(dto.Descriptor, dto.ModelVersion, ct);
        if (!face.matched) return Unauthorized("Xác thực khuôn mặt không thành công.");
        record.FaceDistance = face.distance;
        var end = record.Schedule.WorkDate.ToDateTime(record.Schedule.PlannedEndTime ?? record.Schedule.Shift.EndTime);
        record.CheckOutAt = now;
        record.EarlyLeaveMinutes = Math.Max(0, (int)(end - now).TotalMinutes);

        if (record.Schedule.ScheduleType is ScheduleTypes.Overtime or ScheduleTypes.Weekend)
        {
            var completed = now >= end;
            if (record.Schedule.ScheduleType == ScheduleTypes.Overtime)
            {
                var mainSchedules = await db.WorkSchedules.AsNoTracking()
                    .Where(x => x.EmployeeId == employeeId && x.WorkDate == record.Schedule.WorkDate &&
                                x.CountsAsStandardWork && !x.IsCancelled)
                    .Select(x => new { x.Id, x.CountsAsPaidWork })
                    .ToListAsync(ct);
                var mainIds = mainSchedules.Select(x => x.Id).ToList();
                var completedMainCount = await db.Attendances.CountAsync(x => mainIds.Contains(x.ScheduleId) &&
                    (x.Status == AttendanceStatuses.Present || x.Status == AttendanceStatuses.MissingCheckoutApproved) &&
                    (x.CheckOutAt != null || x.Status == AttendanceStatuses.MissingCheckoutApproved), ct);
                completed = completed && mainSchedules.Count > 0 &&
                            mainSchedules.All(x => !x.CountsAsPaidWork) &&
                            completedMainCount == mainSchedules.Count;
            }
            var start = record.Schedule.WorkDate.ToDateTime(record.Schedule.PlannedStartTime ?? record.Schedule.Shift.StartTime);
            record.OvertimeHours = completed ? (decimal)Math.Max(0, (end - start).TotalHours) : 0;
            record.OvertimeKind = record.Schedule.ScheduleType == ScheduleTypes.Weekend
                ? "Weekend"
                : record.Schedule.Shift.Code == "EVENING" ? "NightShift" : "Hourly";

            if (record.Schedule.SourceRequestId is Guid planId)
            {
                var participant = await db.OvertimeParticipants.FirstOrDefaultAsync(
                    x => x.OvertimePlanId == planId && x.EmployeeId == employeeId, ct);
                if (participant is not null) participant.Completed = completed;
            }
        }

        await db.SaveChangesAsync(ct);
        return Ok(record);
    }

    [Authorize(Roles = Roles.AdminHrManager), HttpPost("missing-checkout/{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid id, DecideRequestDto dto)
    {
        var item = await db.Attendances.Include(x => x.Schedule).FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound();
        if (item.Status != AttendanceStatuses.MissingCheckoutPending) return BadRequest("Bản ghi không chờ xác nhận.");
        if (User.IsInRole(Roles.Manager) &&
            (!Guid.TryParse(User.FindFirstValue("DepartmentId"), out var departmentId) || departmentId != item.Schedule.DepartmentId))
            return Forbid();

        item.Status = dto.Approve ? AttendanceStatuses.MissingCheckoutApproved : AttendanceStatuses.MissingCheckoutRejected;
        item.MissingCheckoutReason = dto.Reason;
        item.ResolvedBy = User.Identity?.Name;
        item.ResolvedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(item);
    }

    [Authorize(Roles = Roles.AdminHrManager), HttpPost("scan-missing-checkouts")]
    public async Task<IActionResult> ScanMissingCheckouts()
    {
        var now = DateTime.Now;
        var query = db.Attendances.Include(x => x.Schedule).ThenInclude(x => x.Shift)
            .Where(x => x.CheckInAt != null && x.CheckOutAt == null && x.Status == AttendanceStatuses.Present);
        if (User.IsInRole(Roles.Manager))
        {
            if (!Guid.TryParse(User.FindFirstValue("DepartmentId"), out var departmentId)) return Forbid();
            query = query.Where(x => x.Schedule.DepartmentId == departmentId);
        }
        var records = await query.ToListAsync();
        var changed = 0;
        foreach (var record in records)
        {
            var plannedEnd = record.Schedule.WorkDate.ToDateTime(record.Schedule.PlannedEndTime ?? record.Schedule.Shift.EndTime);
            if (now > plannedEnd.AddMinutes(record.Schedule.Shift.CheckoutDeadlineMinutes))
            {
                record.Status = AttendanceStatuses.MissingCheckoutPending;
                changed++;
            }
        }
        await db.SaveChangesAsync();
        return Ok(new { changed });
    }

    private Guid? CurrentEmployeeId() => Guid.TryParse(User.FindFirstValue("EmployeeId"), out var id) ? id : null;
}
