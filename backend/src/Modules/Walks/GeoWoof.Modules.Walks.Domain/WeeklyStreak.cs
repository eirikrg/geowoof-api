using System.Globalization;

namespace GeoWoof.Modules.Walks.Domain;

/// <summary>When a walk happened, for the streak: a week depends on where the walk took place.</summary>
public readonly record struct WalkMoment(DateTimeOffset StartedAt, string TimeZone);

/// <summary>
/// Consecutive ISO weeks (Monday to Sunday) with at least one walk, counting back from this week.
/// A week without walks yet does not break the streak until it ends.
/// </summary>
public static class WeeklyStreak
{
    private const int DaysPerWeek = 7;

    public static int Calculate(IReadOnlyCollection<WalkMoment> walks, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(walks);
        if (walks.Count == 0)
        {
            return 0;
        }

        var weeks = walks.Select(walk => MondayOf(walk.StartedAt, walk.TimeZone)).ToHashSet();

        // "This week" follows the place of the latest walk: it is the best guess of where the user is.
        var latest = walks.MaxBy(walk => walk.StartedAt);
        var week = MondayOf(now, latest.TimeZone);
        if (!weeks.Contains(week))
        {
            week = week.AddDays(-DaysPerWeek);
        }

        var streak = 0;
        while (weeks.Contains(week))
        {
            streak++;
            week = week.AddDays(-DaysPerWeek);
        }

        return streak;
    }

    private static DateOnly MondayOf(DateTimeOffset moment, string timeZone)
    {
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var found) ? found : TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTime(moment, zone).DateTime;
        return DateOnly.FromDateTime(ISOWeek.ToDateTime(ISOWeek.GetYear(local), ISOWeek.GetWeekOfYear(local), DayOfWeek.Monday));
    }
}
