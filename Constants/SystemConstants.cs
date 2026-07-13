namespace Nhom2Service.Constants;

public static class Roles
{
    public const string Admin = "Admin";
    public const string HR = "HR";
    public const string Manager = "Manager";
    public const string Employee = "Employee";
    public const string AdminHr = Admin + "," + HR;
    public const string AdminHrManager = Admin + "," + HR + "," + Manager;
}

public static class ScheduleTypes
{
    public const string Regular = "Regular";
    public const string Overtime = "Overtime";
    public const string Weekend = "Weekend";
    public const string PaidLeave = "PaidLeave";
    public const string EventLeave = "EventLeave";
}

public static class AttendanceStatuses
{
    public const string Present = "Present";
    public const string Absent = "Absent";
    public const string PaidLeave = "PaidLeave";
    public const string EventLeave = "EventLeave";
    public const string MissingCheckoutPending = "MissingCheckoutPending";
    public const string MissingCheckoutApproved = "MissingCheckoutApproved";
    public const string MissingCheckoutRejected = "MissingCheckoutRejected";
}

public static class RequestStatuses
{
    public const string Pending = "Pending";
    public const string ManagerApproved = "ManagerApproved";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

public static class ClosureStatuses
{
    public const string Open = "Open";
    public const string Preview = "Preview";
    public const string Closed = "Closed";
    public const string Reopened = "Reopened";
}
