using System.Globalization;

namespace TooMuch.Core;

public sealed record TimeWarning(string Key, int Minutes)
{
    public static TimeWarning? Evaluate(Policy policy, DateTime now, double usedSeconds)
    {
        if (policy.ForceLock || policy.Days is null ||
            !policy.Days.TryGetValue(PolicyEvaluator.IsoWeekday(now).ToString(), out var day) || !PolicyValidator.IsValidDay(day)) return null;
        double remaining;
        string rule;
        if (day.Windows.Count > 0)
        {
            if (PolicyEvaluator.CurrentWindowEnd(day, TimeOnly.FromDateTime(now)) is not { } end) return null;
            remaining = (end.ToTimeSpan() - now.TimeOfDay).TotalMinutes;
            rule = string.Create(CultureInfo.InvariantCulture, $"window-end:{end:HH:mm}");
        }
        else
        {
            var limitMin = PolicyEvaluator.EffectiveDailyLimitMin(policy, now, day);
            remaining = limitMin - usedSeconds / 60;
            rule = $"limit:{limitMin}";
        }
        if (remaining <= 0 || remaining > 10) return null;
        return new TimeWarning(string.Create(CultureInfo.InvariantCulture, $"{now:yyyy-MM-dd}:{rule}"), (int)Math.Ceiling(remaining));
    }
}
