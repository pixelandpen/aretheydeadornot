namespace AreTheyDeadOrNot.Core.Services;

public static class InMemoryWindowCalculator
{
    /// <summary>
    /// Computes the set of MMDD keys (e.g. 1113 for Nov 13) for [today - radiusDays, today + radiusDays].
    /// Handles month and year boundaries seamlessly (e.g. Dec 30 -> Jan 2).
    /// </summary>
    public static IReadOnlyList<short> GetMonthDayKeys(DateOnly today, int radiusDays = 3)
    {
        var clampedRadius = Math.Clamp(radiusDays, 0, 14);
        var keys = new List<short>(clampedRadius * 2 + 1);
        var seen = new HashSet<short>();

        for (var offset = -clampedRadius; offset <= clampedRadius; offset++)
        {
            var date = today.AddDays(offset);
            var key = (short)(date.Month * 100 + date.Day);
            if (seen.Add(key))
            {
                keys.Add(key);
            }
        }

        return keys;
    }
}
