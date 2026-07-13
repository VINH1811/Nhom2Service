using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nhom2Service.Constants;
using Nhom2Service.Data;
using Nhom2Service.Entities;

namespace Nhom2Service.Services;

public sealed class AttendanceSummaryService(AppDbContext db)
{
    public async Task<List<MonthlyAttendanceSummary>> BuildAsync(string monthYear, Guid closureId)
    {
        if (!DateOnly.TryParseExact(
                monthYear + "-01",
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var first))
        {
            throw new ArgumentException("Tháng phải theo yyyy-MM");
        }

        var last = first.AddMonths(1).AddDays(-1);
        var now = DateTime.Now;
        var schedules = await db.WorkSchedules.AsNoTracking()
            .Include(x => x.Shift)
            .Where(x => x.WorkDate >= first && x.WorkDate <= last && !x.IsCancelled)
            .ToListAsync();
        var attendance = await db.Attendances.AsNoTracking()
            .Where(x => x.Schedule.WorkDate >= first && x.Schedule.WorkDate <= last)
            .ToDictionaryAsync(x => x.ScheduleId);
        var penalties = await db.MeetingParticipants.AsNoTracking()
            .Where(x => x.Meeting.MeetingDate >= first && x.Meeting.MeetingDate <= last &&
                        x.DeclineAccepted == false)
            .GroupBy(x => x.EmployeeId)
            .Select(group => new { EmployeeId = group.Key, Days = group.Sum(x => x.PenaltyDays) })
            .ToDictionaryAsync(x => x.EmployeeId, x => x.Days);

        return schedules
            .GroupBy(x => new { x.EmployeeId, x.DepartmentId })
            .Select(group =>
            {
                var standardSchedules = group.Where(x => x.CountsAsStandardWork).ToList();
                var standardRecords = standardSchedules
                    .Where(x => attendance.ContainsKey(x.Id))
                    .Select(x => attendance[x.Id])
                    .ToList();
                var allRecords = group
                    .Where(x => attendance.ContainsKey(x.Id))
                    .Select(x => attendance[x.Id])
                    .ToList();

                var recognizedBeforeMeetingPenalty = standardSchedules.Count(schedule =>
                    schedule.CountsAsPaidWork ||
                    attendance.TryGetValue(schedule.Id, out var record) &&
                    (record.Status == AttendanceStatuses.MissingCheckoutApproved ||
                     record.Status == AttendanceStatuses.Present && record.CheckOutAt is not null));

                var meetingPenaltyDays = penalties.GetValueOrDefault(group.Key.EmployeeId);
                var meetingPenaltyShifts = (int)decimal.Round(
                    meetingPenaltyDays * 2m,
                    0,
                    MidpointRounding.AwayFromZero);

                return new MonthlyAttendanceSummary
                {
                    ClosureId = closureId,
                    EmployeeId = group.Key.EmployeeId,
                    DepartmentId = group.Key.DepartmentId,
                    StandardShifts = standardSchedules.Count,
                    // Phạt cuộc họp làm giảm công được công nhận, vì vậy có thể
                    // ảnh hưởng điều kiện nhận phụ cấp 95% đúng nghiệp vụ đã chốt.
                    RecognizedShifts = Math.Max(
                        0,
                        recognizedBeforeMeetingPenalty - meetingPenaltyShifts),
                    PaidLeaveShifts = standardSchedules.Count(x =>
                        x.ScheduleType == ScheduleTypes.PaidLeave),
                    EventLeaveShifts = standardSchedules.Count(x =>
                        x.ScheduleType == ScheduleTypes.EventLeave),
                    UnauthorizedAbsentShifts = standardSchedules.Count(schedule =>
                        !schedule.CountsAsPaidWork &&
                        IsAttendanceDue(schedule, now) &&
                        (!attendance.TryGetValue(schedule.Id, out var record) ||
                         record.Status is AttendanceStatuses.Absent or
                             AttendanceStatuses.MissingCheckoutRejected)),
                    LateOccurrences = standardRecords.Count(x => x.LateMinutes > 0),
                    LateMinutes = standardRecords.Sum(x => x.LateMinutes),
                    EarlyLeaveOccurrences = standardRecords.Count(x => x.EarlyLeaveMinutes > 0),
                    EarlyLeaveMinutes = standardRecords.Sum(x => x.EarlyLeaveMinutes),
                    MissingCheckoutRejectedCount = standardRecords.Count(x =>
                        x.Status == AttendanceStatuses.MissingCheckoutRejected),
                    OvertimeHours = allRecords
                        .Where(x => x.OvertimeKind == "Hourly")
                        .Sum(x => x.OvertimeHours),
                    OvertimeNightShifts = allRecords.Count(x =>
                        x.OvertimeKind == "NightShift" && x.OvertimeHours > 0),
                    WeekendHours = allRecords
                        .Where(x => x.OvertimeKind == "Weekend")
                        .Sum(x => x.OvertimeHours),
                    MeetingPenaltyDays = meetingPenaltyDays
                };
            })
            .ToList();
    }

    private static bool IsAttendanceDue(WorkSchedule schedule, DateTime now)
    {
        var plannedEnd = schedule.WorkDate.ToDateTime(
            schedule.PlannedEndTime ?? schedule.Shift.EndTime);
        return now > plannedEnd.AddMinutes(schedule.Shift.CheckoutDeadlineMinutes);
    }
}
