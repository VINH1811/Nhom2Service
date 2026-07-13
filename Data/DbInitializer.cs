using Microsoft.EntityFrameworkCore;
using Nhom2Service.Constants;
using Nhom2Service.Entities;
using Nhom2Service.Services;

namespace Nhom2Service.Data;

public sealed class DbInitializer(AppDbContext db, IConfiguration config, AttendanceSummaryService summary)
{
    public async Task InitializeAsync()
    {
        if (config.GetValue("Database:ResetOnStart", false)) await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        if (!await db.Shifts.AnyAsync())
        {
            db.Shifts.AddRange(
                new Shift { Code = "MORNING", Name = "Ca sáng", StartTime = new(8, 0), EndTime = new(12, 0) },
                new Shift { Code = "AFTERNOON", Name = "Ca chiều", StartTime = new(13, 30), EndTime = new(17, 30) },
                new Shift { Code = "EVENING", Name = "Ca tối", StartTime = new(18, 0), EndTime = new(22, 0), IsOvertimeShift = true });
            await db.SaveChangesAsync();
        }
        if (config.GetValue("DemoData:Enabled", true) && !await db.WorkSchedules.AnyAsync()) await SeedDemoAsync();
    }

    private async Task SeedDemoAsync()
    {
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        var shifts = await db.Shifts.ToDictionaryAsync(x => x.Code);
        var monthsBack = Math.Clamp(config.GetValue("DemoData:MonthsBack", 12), 1, 12);
        var current = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var now = DateTime.Now;
        var allEmployees = DemoDataFactory.ActiveEmployees().ToList();

        for (var offset = monthsBack; offset >= 0; offset--)
        {
            var month = current.AddMonths(-offset);
            var days = DemoDataFactory.Weekdays(month.Year, month.Month);
            var isCurrent = offset == 0;
            var schedules = new List<WorkSchedule>();
            var records = new List<AttendanceRecord>();

            foreach (var emp in allEmployees)
            {
                var scenario = emp.ordinal % 10;
                var absentDays = scenario == 3 ? 5 : scenario == 4 ? 11 : scenario == 2 ? 2 : 0;
                var paidLeaveDays = scenario == 1 ? 1 : 0;
                var eventLeaveDays = scenario == 7 ? 1 : 0;

                for (var dayIndex = 0; dayIndex < days.Count; dayIndex++)
                {
                    foreach (var shift in new[] { shifts["MORNING"], shifts["AFTERNOON"] })
                    {
                        var day = days[dayIndex];
                        var schedule = new WorkSchedule
                        {
                            Id = DemoDataFactory.StableGuid($"schedule-{emp.employeeId}-{day:yyyyMMdd}-{shift.Code}"),
                            EmployeeId = emp.employeeId, DepartmentId = emp.departmentId,
                            WorkDate = day, ShiftId = shift.Id
                        };
                        if (dayIndex < paidLeaveDays)
                        {
                            schedule.ScheduleType = ScheduleTypes.PaidLeave;
                            schedule.CountsAsPaidWork = true;
                        }
                        else if (dayIndex < eventLeaveDays)
                        {
                            schedule.ScheduleType = ScheduleTypes.EventLeave;
                            schedule.CountsAsPaidWork = true;
                        }
                        schedules.Add(schedule);

                        var shiftHasEnded = !isCurrent || day.ToDateTime(shift.EndTime) <= now;
                        if (!shiftHasEnded || dayIndex < paidLeaveDays || dayIndex < eventLeaveDays) continue;

                        var status = dayIndex < absentDays ? AttendanceStatuses.Absent : AttendanceStatuses.Present;
                        DateTime? checkIn = null;
                        DateTime? checkOut = null;
                        var late = 0;
                        var early = 0;
                        if (status == AttendanceStatuses.Present)
                        {
                            var lateCase = scenario == 8 && dayIndex < 2;
                            var earlyCase = scenario == 8 && (dayIndex == 0 || (dayIndex == 1 && shift.Code == "MORNING"));
                            checkIn = day.ToDateTime(shift.StartTime).AddMinutes(lateCase ? 20 : -(emp.ordinal % 12));
                            checkOut = day.ToDateTime(shift.EndTime).AddMinutes(earlyCase ? -15 : emp.ordinal % 10);
                            late = lateCase ? 10 : 0;
                            early = earlyCase ? 15 : 0;
                        }
                        if (scenario == 5 && dayIndex == 2 && shift.Code == "AFTERNOON")
                        {
                            status = isCurrent
                                ? AttendanceStatuses.MissingCheckoutPending
                                : emp.ordinal % 2 == 0 ? AttendanceStatuses.MissingCheckoutApproved : AttendanceStatuses.MissingCheckoutRejected;
                            checkOut = null;
                        }
                        records.Add(new AttendanceRecord
                        {
                            Id = DemoDataFactory.StableGuid($"attendance-{schedule.Id}"),
                            ScheduleId = schedule.Id, EmployeeId = emp.employeeId,
                            CheckInAt = checkIn, CheckOutAt = checkOut, Status = status,
                            LateMinutes = late, EarlyLeaveMinutes = early
                        });
                    }
                }

                if (scenario == 0 && days.Count > 5)
                {
                    var hourlyDay = days[4];
                    var hourlySchedule = new WorkSchedule
                    {
                        Id = DemoDataFactory.StableGuid($"hourly-ot-{emp.employeeId}-{hourlyDay:yyyyMMdd}"),
                        EmployeeId = emp.employeeId, DepartmentId = emp.departmentId, WorkDate = hourlyDay,
                        ShiftId = shifts["AFTERNOON"].Id, PlannedStartTime = new TimeOnly(17, 30), PlannedEndTime = new TimeOnly(19, 30),
                        ScheduleType = ScheduleTypes.Overtime, CountsAsStandardWork = false
                    };
                    schedules.Add(hourlySchedule);
                    if (!isCurrent || hourlyDay.ToDateTime(new TimeOnly(19, 30)) <= now)
                    {
                        records.Add(new AttendanceRecord
                        {
                            Id = DemoDataFactory.StableGuid($"attendance-{hourlySchedule.Id}"),
                            ScheduleId = hourlySchedule.Id, EmployeeId = emp.employeeId,
                            CheckInAt = hourlyDay.ToDateTime(new TimeOnly(17, 30)), CheckOutAt = hourlyDay.ToDateTime(new TimeOnly(19, 30)),
                            Status = AttendanceStatuses.Present, OvertimeHours = 2, OvertimeKind = "Hourly"
                        });
                    }

                    var day = days[5];
                    var overtimeSchedule = new WorkSchedule
                    {
                        Id = DemoDataFactory.StableGuid($"ot-{emp.employeeId}-{day:yyyyMMdd}"),
                        EmployeeId = emp.employeeId, DepartmentId = emp.departmentId, WorkDate = day,
                        ShiftId = shifts["EVENING"].Id, PlannedStartTime = new TimeOnly(18, 0), PlannedEndTime = new TimeOnly(22, 0),
                        ScheduleType = ScheduleTypes.Overtime, CountsAsStandardWork = false
                    };
                    schedules.Add(overtimeSchedule);
                    if (!isCurrent || day.ToDateTime(new TimeOnly(22, 0)) <= now)
                    {
                        records.Add(new AttendanceRecord
                        {
                            Id = DemoDataFactory.StableGuid($"attendance-{overtimeSchedule.Id}"),
                            ScheduleId = overtimeSchedule.Id, EmployeeId = emp.employeeId,
                            CheckInAt = day.ToDateTime(new TimeOnly(18, 0)), CheckOutAt = day.ToDateTime(new TimeOnly(22, 0)),
                            Status = AttendanceStatuses.Present, OvertimeHours = 4, OvertimeKind = "NightShift"
                        });
                    }
                }

                if (scenario == 6)
                {
                    var saturday = new DateOnly(month.Year, month.Month, 1);
                    while (saturday.DayOfWeek != DayOfWeek.Saturday) saturday = saturday.AddDays(1);
                    var weekendSchedule = new WorkSchedule
                    {
                        Id = DemoDataFactory.StableGuid($"weekend-{emp.employeeId}-{saturday:yyyyMMdd}"),
                        EmployeeId = emp.employeeId, DepartmentId = emp.departmentId, WorkDate = saturday,
                        ShiftId = shifts["MORNING"].Id, PlannedStartTime = new TimeOnly(8, 0), PlannedEndTime = new TimeOnly(16, 0),
                        ScheduleType = ScheduleTypes.Weekend, CountsAsStandardWork = false
                    };
                    schedules.Add(weekendSchedule);
                    if (!isCurrent || saturday.ToDateTime(new TimeOnly(16, 0)) <= now)
                    {
                        records.Add(new AttendanceRecord
                        {
                            Id = DemoDataFactory.StableGuid($"attendance-{weekendSchedule.Id}"),
                            ScheduleId = weekendSchedule.Id, EmployeeId = emp.employeeId,
                            CheckInAt = saturday.ToDateTime(new TimeOnly(8, 0)), CheckOutAt = saturday.ToDateTime(new TimeOnly(16, 0)),
                            Status = AttendanceStatuses.Present, OvertimeHours = 8, OvertimeKind = "Weekend"
                        });
                    }
                }
            }

            db.WorkSchedules.AddRange(schedules);
            db.Attendances.AddRange(records);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // Mỗi phòng có một trường hợp từ chối họp để kiểm thử quyết định phạt.
            var penaltyMeetings = new List<Meeting>();
            foreach (var department in allEmployees.GroupBy(x => x.departmentId))
            {
                var person = department.FirstOrDefault(x => x.ordinal % 10 == 9);
                if (person.employeeId == Guid.Empty) continue;
                var meetingDate = days[Math.Min(3, days.Count - 1)];
                penaltyMeetings.Add(new Meeting
                {
                    Id = DemoDataFactory.StableGuid($"penalty-meeting-{department.Key}-{month:yyyyMM}"),
                    DepartmentId = department.Key, Title = "Họp đánh giá tiến độ",
                    MeetingDate = meetingDate, StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0),
                    Location = "Phòng họp nội bộ", Objective = "Đánh giá tiến độ công việc",
                    CreatedBy = "DemoManager",
                    Participants = new List<MeetingParticipant>
                    {
                        new()
                        {
                            EmployeeId = person.employeeId, Response = "Declined",
                            DeclineReason = "Lý do chưa được chấp thuận",
                            DeclineAccepted = isCurrent ? null : false,
                            PenaltyDays = isCurrent ? 0 : 1,
                            DecidedBy = isCurrent ? null : "DemoManager"
                        }
                    }
                });
            }
            db.Meetings.AddRange(penaltyMeetings);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var closure = new MonthlyAttendanceClosure
            {
                Id = DemoDataFactory.StableGuid($"closure-{month:yyyyMM}"), MonthYear = $"{month:yyyy-MM}",
                Status = isCurrent ? ClosureStatuses.Preview : ClosureStatuses.Closed,
                ClosedAt = isCurrent ? null : month.AddMonths(1).ToDateTime(TimeOnly.MinValue),
                ClosedBy = isCurrent ? null : "DemoSeeder"
            };
            db.MonthlyClosures.Add(closure);
            await db.SaveChangesAsync();
            db.MonthlySummaries.AddRange(await summary.BuildAsync(closure.MonthYear, closure.Id));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        db.ChangeTracker.AutoDetectChangesEnabled = true;
        await SeedWorkflowDataAsync(current, shifts);
    }

    private async Task SeedWorkflowDataAsync(DateOnly currentMonth, Dictionary<string, Shift> shifts)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var employees = DemoDataFactory.ActiveEmployees().ToList();
        foreach (var group in employees.GroupBy(x => x.departmentId))
        {
            var people = group.OrderBy(x => x.ordinal).ToList();
            var manager = people[0];
            var leaveEmployee = people[Math.Min(6, people.Count - 1)];
            var weekdays = DemoDataFactory.Weekdays(currentMonth.Year, currentMonth.Month);
            var leaveDate = weekdays.FirstOrDefault(x => x >= today);
            if (leaveDate == default) leaveDate = weekdays.Last();
            var leaveStatus = leaveEmployee.ordinal % 3 == 0 ? RequestStatuses.Pending : RequestStatuses.Approved;
            db.LeaveRequests.Add(new LeaveRequest
            {
                Id = DemoDataFactory.StableGuid($"workflow-leave-{group.Key}-{currentMonth:yyyyMM}"),
                EmployeeId = leaveEmployee.employeeId, DepartmentId = group.Key,
                StartDate = leaveDate, EndDate = leaveDate, RequestedShifts = 2,
                Reason = "Nghỉ phép cá nhân - dữ liệu trình diễn", Status = leaveStatus,
                ApprovedBy = leaveStatus == RequestStatuses.Approved ? "DemoManager" : null,
                DecidedAt = leaveStatus == RequestStatuses.Approved ? DateTime.UtcNow : null
            });
            if (leaveStatus == RequestStatuses.Approved)
            {
                var leaveSchedules = await db.WorkSchedules.Where(x => x.EmployeeId == leaveEmployee.employeeId && x.WorkDate == leaveDate && x.CountsAsStandardWork).ToListAsync();
                foreach (var schedule in leaveSchedules)
                {
                    schedule.ScheduleType = ScheduleTypes.PaidLeave;
                    schedule.CountsAsPaidWork = true;
                }
            }

            var overtimeDate = leaveDate.AddDays(1);
            while (overtimeDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) overtimeDate = overtimeDate.AddDays(1);
            var overtime = new OvertimePlan
            {
                Id = DemoDataFactory.StableGuid($"workflow-overtime-{group.Key}-{currentMonth:yyyyMM}"),
                DepartmentId = group.Key, RequestedByEmployeeId = manager.employeeId,
                WorkDate = overtimeDate, StartTime = new TimeOnly(18, 0), EndTime = new TimeOnly(22, 0),
                Kind = "NightShift", Reason = "Hoàn thành kế hoạch phòng ban - dữ liệu trình diễn",
                Status = RequestStatuses.Approved, ManagerApprovedBy = "DemoManager",
                Participants = people.Take(5).Select(x => new OvertimeParticipant { EmployeeId = x.employeeId }).ToList()
            };
            db.OvertimePlans.Add(overtime);
            foreach (var participant in overtime.Participants)
            {
                if (!await db.WorkSchedules.AnyAsync(x => x.EmployeeId == participant.EmployeeId && x.WorkDate == overtimeDate && x.ShiftId == shifts["EVENING"].Id && x.ScheduleType == ScheduleTypes.Overtime))
                {
                    db.WorkSchedules.Add(new WorkSchedule
                    {
                        Id = DemoDataFactory.StableGuid($"workflow-overtime-schedule-{overtime.Id}-{participant.EmployeeId}"),
                        EmployeeId = participant.EmployeeId, DepartmentId = group.Key, WorkDate = overtimeDate,
                        ShiftId = shifts["EVENING"].Id, PlannedStartTime = overtime.StartTime, PlannedEndTime = overtime.EndTime,
                        ScheduleType = ScheduleTypes.Overtime, CountsAsStandardWork = false,
                        SourceRequestId = overtime.Id, Note = overtime.Reason
                    });
                }
            }

            var meetingDate = overtimeDate.AddDays(1);
            while (meetingDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) meetingDate = meetingDate.AddDays(1);
            var meeting = new Meeting
            {
                Id = DemoDataFactory.StableGuid($"workflow-meeting-{group.Key}-{currentMonth:yyyyMM}"),
                DepartmentId = group.Key, Title = "Họp kế hoạch tháng",
                MeetingDate = meetingDate, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
                Location = "Phòng họp nội bộ", Objective = "Rà soát mục tiêu và tiến độ công việc",
                PresentationRequirement = "Các nhóm chuẩn bị báo cáo ngắn", CreatedBy = "DemoManager",
                Participants = people.Take(10).Select((x, index) => new MeetingParticipant
                {
                    EmployeeId = x.employeeId,
                    Response = index < 7 ? "Accepted" : index == 7 ? "Declined" : "Pending",
                    DeclineReason = index == 7 ? "Có lịch khám bệnh" : null,
                    DeclineAccepted = index == 7 ? true : null
                }).ToList()
            };
            db.Meetings.Add(meeting);
            db.Notifications.AddRange(meeting.Participants.Select(x => new Notification
            {
                EmployeeId = x.EmployeeId, Type = "Meeting", Title = meeting.Title,
                Message = $"Họp lúc {meeting.StartTime:HH\\:mm} ngày {meeting.MeetingDate:dd/MM/yyyy}",
                ReferenceId = meeting.Id, IsRead = x.Response != "Pending"
            }));
        }

        foreach (var departmentId in employees.Select(x => x.departmentId).Distinct().Take(5))
        {
            db.EventLeaves.Add(new EventLeaveRequest
            {
                Id = DemoDataFactory.StableGuid($"event-leave-{departmentId}-{currentMonth:yyyyMM}"),
                DepartmentId = departmentId, Name = "Sự kiện nội bộ phòng ban",
                StartDate = today.AddDays(7), EndDate = today.AddDays(7),
                Reason = "Hoạt động tập thể - dữ liệu trình diễn", Status = RequestStatuses.Pending,
                RequestedBy = "DemoManager"
            });
        }
        await db.SaveChangesAsync();

        var currentClosure = await db.MonthlyClosures.Include(x => x.Summaries)
            .FirstAsync(x => x.MonthYear == $"{currentMonth:yyyy-MM}" && x.Version == 1);
        db.MonthlySummaries.RemoveRange(currentClosure.Summaries);
        db.MonthlySummaries.AddRange(await summary.BuildAsync(currentClosure.MonthYear, currentClosure.Id));
        await db.SaveChangesAsync();
    }
}
