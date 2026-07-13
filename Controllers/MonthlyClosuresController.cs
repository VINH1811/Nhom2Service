using System.Collections.Concurrent;
using System.Globalization;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nhom2Service.Constants;
using Nhom2Service.Data;
using Nhom2Service.DTOs;
using Nhom2Service.Entities;
using Nhom2Service.Services;
using WLPRO.HRM.Contracts;

namespace Nhom2Service.Controllers;

[ApiController]
[Route("api/monthly-closures")]
[Authorize(Roles = Roles.AdminHr)]
public sealed class MonthlyClosuresController(
    AppDbContext db,
    AttendanceSummaryService summaries,
    IPublishEndpoint publisher) : ControllerBase
{
    /*
     * Khóa theo từng tháng để tránh hai request Preview/Close của cùng một
     * tháng chạy đồng thời trong cùng một instance ứng dụng.
     *
     * Điều này xử lý trường hợp người dùng bấm nút nhiều lần hoặc frontend
     * vô tình gửi hai request gần nhau.
     */
    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
        MonthLocks = new(StringComparer.OrdinalIgnoreCase);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var result = await db.MonthlyClosures
            .AsNoTracking()
            .OrderByDescending(x => x.MonthYear)
            .ThenByDescending(x => x.Version)
            .Select(x => new
            {
                x.Id,
                x.MonthYear,
                x.Status,
                x.Version,
                x.ClosedAt,
                x.ClosedBy,
                x.ReopenedAt,
                x.ReopenedBy,
                x.ReopenReason,
                SummaryCount = x.Summaries.Count
            })
            .ToListAsync();

        return Ok(result);
    }

    [HttpPost("{monthYear}/preview")]
    public async Task<IActionResult> Preview(string monthYear)
    {
        if (!TryParseMonth(monthYear, out _, out _))
        {
            return BadRequest("Tháng phải theo định dạng yyyy-MM.");
        }

        var monthLock = MonthLocks.GetOrAdd(
            monthYear,
            static _ => new SemaphoreSlim(1, 1));

        await monthLock.WaitAsync();

        try
        {
            await using var transaction =
                await db.Database.BeginTransactionAsync();

            var closure = await db.MonthlyClosures
                .OrderByDescending(x => x.Version)
                .FirstOrDefaultAsync(x => x.MonthYear == monthYear);

            if (closure?.Status == ClosureStatuses.Closed)
            {
                return Conflict(
                    "Kỳ công đã chốt, không thể ghi đè bản tạm tính.");
            }

            if (closure is null)
            {
                closure = new MonthlyAttendanceClosure
                {
                    Id = Guid.NewGuid(),
                    MonthYear = monthYear,
                    Status = ClosureStatuses.Preview,
                    Version = 1
                };

                db.MonthlyClosures.Add(closure);
            }
            else
            {
                /*
                 * Xóa trực tiếp trong database thay vì tải các entity cũ rồi
                 * dùng RemoveRange. Cách này không giữ bản ghi cũ trong
                 * ChangeTracker và tránh DbUpdateConcurrencyException khi
                 * request bị gửi lặp.
                 */
                await db.MonthlySummaries
                    .Where(x => x.ClosureId == closure.Id)
                    .ExecuteDeleteAsync();
            }

            var built = await summaries.BuildAsync(
                monthYear,
                closure.Id);

            db.MonthlySummaries.AddRange(built);

            closure.Status = ClosureStatuses.Preview;
            closure.ClosedAt = null;
            closure.ClosedBy = null;

            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            var response = await GetClosureResponseAsync(closure.Id);

            return Ok(response);
        }
        finally
        {
            monthLock.Release();
        }
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpPost("{monthYear}/reopen")]
    public async Task<IActionResult> Reopen(
        string monthYear,
        [FromBody] ReopenAttendanceMonthDto dto)
    {
        if (!TryParseMonth(monthYear, out _, out _))
        {
            return BadRequest("Tháng phải theo định dạng yyyy-MM.");
        }

        if (string.IsNullOrWhiteSpace(dto.Reason))
        {
            return BadRequest("Phải nhập lý do mở lại kỳ công.");
        }

        var monthLock = MonthLocks.GetOrAdd(
            monthYear,
            static _ => new SemaphoreSlim(1, 1));

        await monthLock.WaitAsync();

        try
        {
            var latest = await db.MonthlyClosures
                .AsNoTracking()
                .Where(x => x.MonthYear == monthYear)
                .OrderByDescending(x => x.Version)
                .FirstOrDefaultAsync();

            if (latest is null ||
                latest.Status != ClosureStatuses.Closed)
            {
                return BadRequest("Chỉ mở lại kỳ công đã chốt.");
            }

            var hasOpenVersion = await db.MonthlyClosures
                .AnyAsync(x =>
                    x.MonthYear == monthYear &&
                    x.Status != ClosureStatuses.Closed);

            if (hasOpenVersion)
            {
                return Conflict(
                    "Tháng này đã có một kỳ công đang mở.");
            }

            var reopened = new MonthlyAttendanceClosure
            {
                Id = Guid.NewGuid(),
                MonthYear = monthYear,
                Version = latest.Version + 1,
                Status = ClosureStatuses.Reopened,
                ReopenedAt = DateTime.UtcNow,
                ReopenedBy = User.Identity?.Name,
                ReopenReason = dto.Reason.Trim()
            };

            db.MonthlyClosures.Add(reopened);
            await db.SaveChangesAsync();

            return Ok(new
            {
                reopened.Id,
                reopened.MonthYear,
                reopened.Status,
                reopened.Version,
                reopened.ReopenedAt,
                reopened.ReopenedBy,
                reopened.ReopenReason
            });
        }
        finally
        {
            monthLock.Release();
        }
    }

    [HttpPost("{monthYear}/close")]
    public async Task<IActionResult> Close(string monthYear)
    {
        if (!TryParseMonth(monthYear, out var first, out var last))
        {
            return BadRequest("Tháng phải theo định dạng yyyy-MM.");
        }

        if (DateOnly.FromDateTime(DateTime.Today) < first.AddMonths(1))
        {
            return BadRequest(
                "Chưa thể chốt công khi tháng chưa kết thúc.");
        }

        var monthLock = MonthLocks.GetOrAdd(
            monthYear,
            static _ => new SemaphoreSlim(1, 1));

        await monthLock.WaitAsync();

        try
        {
            var existing = await db.MonthlyClosures
                .OrderByDescending(x => x.Version)
                .FirstOrDefaultAsync(x => x.MonthYear == monthYear);

            if (existing?.Status == ClosureStatuses.Closed)
            {
                return Conflict("Kỳ công này đã được chốt.");
            }

            /*
             * Không phụ thuộc vào việc người dùng đã bấm quét thiếu
             * check-out hay chưa. Khi chốt tháng, mọi ca đã check-in nhưng
             * chưa check-out đều được đưa về trạng thái chờ xác nhận.
             */
            var unresolvedCheckouts = await db.Attendances
                .Include(x => x.Schedule)
                .Where(x =>
                    x.Schedule.WorkDate >= first &&
                    x.Schedule.WorkDate <= last &&
                    x.CheckInAt != null &&
                    x.CheckOutAt == null &&
                    (x.Status == AttendanceStatuses.Present ||
                     x.Status ==
                        AttendanceStatuses.MissingCheckoutPending))
                .ToListAsync();

            foreach (var record in unresolvedCheckouts.Where(x =>
                         x.Status == AttendanceStatuses.Present))
            {
                record.Status =
                    AttendanceStatuses.MissingCheckoutPending;
            }

            if (unresolvedCheckouts.Count > 0)
            {
                await db.SaveChangesAsync();

                return Conflict(new
                {
                    message =
                        "Còn trường hợp thiếu check-out chờ quản lý " +
                        "xác nhận.",
                    count = unresolvedCheckouts.Count
                });
            }

            var pendingLeave = await db.LeaveRequests.AnyAsync(x =>
                x.StartDate <= last &&
                x.EndDate >= first &&
                x.Status == RequestStatuses.Pending);

            var pendingOvertime = await db.OvertimePlans.AnyAsync(x =>
                x.WorkDate >= first &&
                x.WorkDate <= last &&
                (x.Status == RequestStatuses.Pending ||
                 x.Status == RequestStatuses.ManagerApproved));

            var pendingEventLeave = await db.EventLeaves.AnyAsync(x =>
                x.StartDate <= last &&
                x.EndDate >= first &&
                x.Status == RequestStatuses.Pending);

            var pendingMeetingDecision =
                await db.MeetingParticipants.AnyAsync(x =>
                    x.Meeting.MeetingDate >= first &&
                    x.Meeting.MeetingDate <= last &&
                    x.Response == "Declined" &&
                    x.DeclineAccepted == null);

            if (pendingLeave ||
                pendingOvertime ||
                pendingEventLeave ||
                pendingMeetingDecision)
            {
                return Conflict(new
                {
                    message =
                        "Còn nghiệp vụ chờ xử lý trước khi chốt công.",
                    pendingLeave,
                    pendingOvertime,
                    pendingEventLeave,
                    pendingMeetingDecision
                });
            }

            await using var transaction =
                await db.Database.BeginTransactionAsync();

            var closure = existing ?? new MonthlyAttendanceClosure
            {
                Id = Guid.NewGuid(),
                MonthYear = monthYear,
                Version = 1,
                Status = ClosureStatuses.Preview
            };

            if (existing is null)
            {
                db.MonthlyClosures.Add(closure);
            }
            else
            {
                await db.MonthlySummaries
                    .Where(x => x.ClosureId == closure.Id)
                    .ExecuteDeleteAsync();
            }

            var built = await summaries.BuildAsync(
                monthYear,
                closure.Id);

            db.MonthlySummaries.AddRange(built);

            closure.Status = ClosureStatuses.Closed;
            closure.ClosedAt = DateTime.UtcNow;
            closure.ClosedBy = User.Identity?.Name;

            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            var contracts = built
                .Select(x => new AttendanceSummaryContract(
                    x.EmployeeId,
                    x.DepartmentId,
                    x.StandardShifts,
                    x.RecognizedShifts,
                    x.PaidLeaveShifts,
                    x.EventLeaveShifts,
                    x.UnauthorizedAbsentShifts,
                    x.LateOccurrences,
                    x.LateMinutes,
                    x.EarlyLeaveOccurrences,
                    x.EarlyLeaveMinutes,
                    x.MissingCheckoutRejectedCount,
                    x.OvertimeHours,
                    x.OvertimeNightShifts,
                    x.WeekendHours,
                    x.MeetingPenaltyDays))
                .ToList();

            await publisher.Publish(
                new MonthlyAttendanceClosedEvent(
                    Guid.NewGuid(),
                    DateTime.UtcNow,
                    monthYear,
                    closure.Version,
                    contracts));

            var response = await GetClosureResponseAsync(closure.Id);

            return Ok(response);
        }
        finally
        {
            monthLock.Release();
        }
    }

    private async Task<ClosureResponse?> GetClosureResponseAsync(
        Guid closureId)
    {
        var closure = await db.MonthlyClosures
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == closureId);

        if (closure is null)
        {
            return null;
        }

        var summaryItems = await db.MonthlySummaries
            .AsNoTracking()
            .Where(x => x.ClosureId == closureId)
            .OrderBy(x => x.EmployeeId)
            .Select(x => new MonthlySummaryResponse(
                x.Id,
                x.ClosureId,
                x.EmployeeId,
                x.DepartmentId,
                x.StandardShifts,
                x.RecognizedShifts,
                x.PaidLeaveShifts,
                x.EventLeaveShifts,
                x.UnauthorizedAbsentShifts,
                x.LateOccurrences,
                x.LateMinutes,
                x.EarlyLeaveOccurrences,
                x.EarlyLeaveMinutes,
                x.MissingCheckoutRejectedCount,
                x.OvertimeHours,
                x.OvertimeNightShifts,
                x.WeekendHours,
                x.MeetingPenaltyDays))
            .ToListAsync();

        return new ClosureResponse(
            closure.Id,
            closure.MonthYear,
            closure.Status,
            closure.Version,
            closure.ClosedAt,
            closure.ClosedBy,
            closure.ReopenedAt,
            closure.ReopenedBy,
            closure.ReopenReason,
            summaryItems);
    }

    private static bool TryParseMonth(
        string monthYear,
        out DateOnly first,
        out DateOnly last)
    {
        var valid = DateOnly.TryParseExact(
            monthYear + "-01",
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out first);

        last = valid
            ? first.AddMonths(1).AddDays(-1)
            : default;

        return valid;
    }

    private sealed record ClosureResponse(
        Guid Id,
        string MonthYear,
        string Status,
        int Version,
        DateTime? ClosedAt,
        string? ClosedBy,
        DateTime? ReopenedAt,
        string? ReopenedBy,
        string? ReopenReason,
        IReadOnlyList<MonthlySummaryResponse> Summaries);

    private sealed record MonthlySummaryResponse(
        long Id,
        Guid ClosureId,
        Guid EmployeeId,
        Guid DepartmentId,
        int StandardShifts,
        int RecognizedShifts,
        int PaidLeaveShifts,
        int EventLeaveShifts,
        int UnauthorizedAbsentShifts,
        int LateOccurrences,
        int LateMinutes,
        int EarlyLeaveOccurrences,
        int EarlyLeaveMinutes,
        int MissingCheckoutRejectedCount,
        decimal OvertimeHours,
        int OvertimeNightShifts,
        decimal WeekendHours,
        decimal MeetingPenaltyDays);
}
