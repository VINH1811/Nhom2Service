using System.Security.Cryptography;
using System.Text;

namespace Nhom2Service.Data;

public static class DemoDataFactory
{
    public const int ParentCount = 5;
    public const int LeafPerParent = 5;
    public const int EmployeesPerLeaf = 17;

    public static Guid StableGuid(string value) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes(value)));

    public static Guid LeafDepartmentId(int parent, int leaf) =>
        StableGuid($"leaf-{parent}-{leaf}");

    public static Guid EmployeeId(int parent, int leaf, int employee) =>
        StableGuid($"employee-{parent}-{leaf}-{employee}");

    public static int Ordinal(int parent, int leaf, int employee) =>
        ((parent - 1) * LeafPerParent + (leaf - 1)) * EmployeesPerLeaf + employee;

    public static IEnumerable<(
        int parent,
        int leaf,
        int employee,
        int ordinal,
        Guid employeeId,
        Guid departmentId)> ActiveEmployees()
    {
        for (var parent = 1; parent <= ParentCount; parent++)
        for (var leaf = 1; leaf <= LeafPerParent; leaf++)
        for (var employee = 1; employee <= EmployeesPerLeaf; employee++)
        {
            var ordinal = Ordinal(parent, leaf, employee);
            yield return (
                parent,
                leaf,
                employee,
                ordinal,
                EmployeeId(parent, leaf, employee),
                LeafDepartmentId(parent, leaf));
        }
    }

    public static List<DateOnly> Weekdays(int year, int month)
    {
        var result = new List<DateOnly>();
        for (var day = new DateOnly(year, month, 1); day.Month == month; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                result.Add(day);
        }

        return result;
    }
}
