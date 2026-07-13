using Microsoft.EntityFrameworkCore;
using Nhom2Service.Entities;

namespace Nhom2Service.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<WorkSchedule> WorkSchedules => Set<WorkSchedule>();
    public DbSet<AttendanceRecord> Attendances => Set<AttendanceRecord>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<OvertimePlan> OvertimePlans => Set<OvertimePlan>();
    public DbSet<OvertimeParticipant> OvertimeParticipants => Set<OvertimeParticipant>();
    public DbSet<EventLeaveRequest> EventLeaves => Set<EventLeaveRequest>();
    public DbSet<Meeting> Meetings => Set<Meeting>();
    public DbSet<MeetingParticipant> MeetingParticipants => Set<MeetingParticipant>();
    public DbSet<MonthlyAttendanceClosure> MonthlyClosures => Set<MonthlyAttendanceClosure>();
    public DbSet<MonthlyAttendanceSummary> MonthlySummaries => Set<MonthlyAttendanceSummary>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Shift>().HasIndex(x => x.Code).IsUnique();

        b.Entity<WorkSchedule>()
            .HasIndex(x => new { x.EmployeeId, x.WorkDate, x.ShiftId, x.ScheduleType })
            .IsUnique();
        b.Entity<WorkSchedule>().HasIndex(x => new { x.WorkDate, x.DepartmentId });
        b.Entity<WorkSchedule>().HasOne(x => x.Shift).WithMany()
            .HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);

        b.Entity<AttendanceRecord>().HasIndex(x => x.ScheduleId).IsUnique();
        b.Entity<AttendanceRecord>().Property(x => x.OvertimeHours).HasPrecision(8, 2);
        b.Entity<AttendanceRecord>().HasOne(x => x.Schedule).WithMany()
            .HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Restrict);

        b.Entity<OvertimePlan>().HasMany(x => x.Participants).WithOne(x => x.Plan)
            .HasForeignKey(x => x.OvertimePlanId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<OvertimeParticipant>()
            .HasIndex(x => new { x.OvertimePlanId, x.EmployeeId }).IsUnique();

        b.Entity<Meeting>().HasMany(x => x.Participants).WithOne(x => x.Meeting)
            .HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MeetingParticipant>().Property(x => x.PenaltyDays).HasPrecision(5, 2);
        b.Entity<MeetingParticipant>()
            .HasIndex(x => new { x.MeetingId, x.EmployeeId }).IsUnique();

        b.Entity<MonthlyAttendanceClosure>()
            .HasIndex(x => new { x.MonthYear, x.Version }).IsUnique();
        b.Entity<MonthlyAttendanceClosure>().HasMany(x => x.Summaries).WithOne(x => x.Closure)
            .HasForeignKey(x => x.ClosureId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MonthlyAttendanceSummary>().Property(x => x.OvertimeHours).HasPrecision(8, 2);
        b.Entity<MonthlyAttendanceSummary>().Property(x => x.WeekendHours).HasPrecision(8, 2);
        b.Entity<MonthlyAttendanceSummary>().Property(x => x.MeetingPenaltyDays).HasPrecision(5, 2);
        b.Entity<MonthlyAttendanceSummary>()
            .HasIndex(x => new { x.ClosureId, x.EmployeeId }).IsUnique();

        b.Entity<Notification>().HasIndex(x => new { x.EmployeeId, x.IsRead, x.CreatedAt });
    }
}
