using AreTheyDeadOrNot.Core.Services;

namespace AreTheyDeadOrNot.Core.UnitTests;

public sealed class InMemoryWindowCalculatorTests
{
    [Fact]
    public void GetMonthDayKeys_HandlesMidMonthWindow()
    {
        var today = new DateOnly(2026, 11, 13);
        var keys = InMemoryWindowCalculator.GetMonthDayKeys(today, radiusDays: 3);

        Assert.Equal(new short[] { 1110, 1111, 1112, 1113, 1114, 1115, 1116 }, keys);
    }

    [Fact]
    public void GetMonthDayKeys_HandlesYearBoundaryWrapAround()
    {
        var today = new DateOnly(2026, 12, 31);
        var keys = InMemoryWindowCalculator.GetMonthDayKeys(today, radiusDays: 2);

        Assert.Equal(new short[] { 1229, 1230, 1231, 101, 102 }, keys);
    }

    [Fact]
    public void GetMonthDayKeys_HandlesLeapYearFebruary()
    {
        var today = new DateOnly(2024, 2, 28);
        var keys = InMemoryWindowCalculator.GetMonthDayKeys(today, radiusDays: 1);

        Assert.Equal(new short[] { 227, 228, 229 }, keys);
    }
}
