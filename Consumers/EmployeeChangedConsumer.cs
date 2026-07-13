using MassTransit;
using Microsoft.EntityFrameworkCore;
using Nhom2Service.Data;
using Nhom2Service.Entities;
using WLPRO.HRM.Contracts;

namespace Nhom2Service.Consumers;

public sealed class EmployeeChangedConsumer(AppDbContext db) : IConsumer<EmployeeChangedEvent>
{
    public async Task Consume(ConsumeContext<EmployeeChangedEvent> context)
    {
        var message = context.Message;
        var today = DateOnly.FromDateTime(DateTime.Today);

        if (message.EmploymentStatus != "Active")
        {
            var future = await db.WorkSchedules
                .Where(x => x.EmployeeId == message.EmployeeId && x.WorkDate > today)
                .ToListAsync();
            foreach (var item in future) item.IsCancelled = true;
            await db.SaveChangesAsync();
            return;
        }

        var shifts = await db.Shifts
            .Where(x => x.Code == "MORNING" || x.Code == "AFTERNOON")
            .ToListAsync();
        var start = message.HireDate > today ? message.HireDate : today;
        var end = start.AddMonths(2);
        var futureSchedules = await db.WorkSchedules
            .Where(x => x.EmployeeId == message.EmployeeId &&
                        x.WorkDate >= start && x.WorkDate <= end)
            .ToListAsync();

        // Khi điều chuyển, mọi lịch tương lai phải chuyển sang phòng mới.
        foreach (var schedule in futureSchedules) schedule.DepartmentId = message.DepartmentId;
        var existing = futureSchedules.Select(x => new { x.WorkDate, x.ShiftId }).ToHashSet();

        for (var day = start; day <= end; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            foreach (var shift in shifts)
            {
                if (existing.Contains(new { WorkDate = day, ShiftId = shift.Id })) continue;
                db.WorkSchedules.Add(new WorkSchedule
                {
                    Id = Guid.NewGuid(),
                    EmployeeId = message.EmployeeId,
                    DepartmentId = message.DepartmentId,
                    WorkDate = day,
                    ShiftId = shift.Id
                });
            }
        }
        await db.SaveChangesAsync();
    }
}
