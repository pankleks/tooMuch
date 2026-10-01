using System.Globalization;

namespace TooMuch.Core;

public static class PolicyValidator
{
    public static void Validate(Policy? policy, string deviceId)
    {
        if (policy is null || policy.DeviceId != deviceId)
            throw new InvalidDataException("Policy device identity mismatch.");
        if (policy.Version < 1 || string.IsNullOrWhiteSpace(policy.TimeZone))
            throw new InvalidDataException("Invalid policy version or time zone.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new InvalidDataException("Invalid policy time zone.", ex); }
        if (policy.IdleThresholdSec < 30 || policy.IdleThresholdSec > 3600 ||
            policy.DailyBonusMin < 0 || policy.DailyBonusMin > int.MaxValue - 1440)
            throw new InvalidDataException("Invalid policy activity settings or daily bonus.");
        if (policy.DailyBonusDate is null ||
            (policy.DailyBonusDate.Length > 0 && !DateOnly.TryParseExact(policy.DailyBonusDate,
                "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            throw new InvalidDataException("Invalid daily bonus date.");
        if (policy.Days is null || policy.Days.Count != 7)
            throw new InvalidDataException("Policy must contain all seven weekdays.");
        for (var weekday = 1; weekday <= 7; weekday++)
        {
            if (!policy.Days.TryGetValue(weekday.ToString(CultureInfo.InvariantCulture), out var day) || !IsValidDay(day))
                throw new InvalidDataException($"Invalid policy for weekday {weekday}.");
        }
    }

    public static bool IsValidDay(DayConfig? day)
    {
        if (day is null || day.LimitMin < 0 || day.LimitMin > 1440 || day.Windows is null) return false;
        var intervals = new List<(TimeOnly From, TimeOnly To)>();
        foreach (var window in day.Windows)
        {
            if (window is null || !TryTime(window.From, out var from) || !TryTime(window.To, out var to) || from >= to)
                return false;
            intervals.Add((from, to));
        }
        intervals.Sort((a, b) => a.From.CompareTo(b.From));
        for (var i = 1; i < intervals.Count; i++)
            if (intervals[i].From < intervals[i - 1].To) return false;
        return true;
    }

    private static bool TryTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}
