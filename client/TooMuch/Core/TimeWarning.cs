namespace TooMuch.Core;

public sealed record TimeWarning(string Key, int Minutes)
{
    public static TimeWarning? Evaluate(Policy policy, DateTime now, double usedSeconds)
    {
        if (policy.ForceLock || !policy.Days.TryGetValue(PolicyEvaluator.IsoWeekday(now).ToString(), out var day)) return null;
        double remaining;
        string rule;
        if (day.Windows.Count > 0)
        {
            var window = day.Windows.FirstOrDefault(w => PolicyEvaluator.IsInWindow(TimeOnly.FromDateTime(now), w));
            if (window == null) return null;
            var end = TimeOnly.Parse(window.To);
            // Adjacent windows do not actually disconnect the session at their shared boundary.
            foreach (var next in day.Windows.OrderBy(w => TimeOnly.Parse(w.From)))
                if (TimeOnly.Parse(next.From) <= end && TimeOnly.Parse(next.To) > end)
                    end = TimeOnly.Parse(next.To);
            remaining = (end.ToTimeSpan() - now.TimeOfDay).TotalMinutes;
            rule = $"window-end:{end:HH:mm}";
        }
        else
        {
            remaining = day.LimitMin - usedSeconds / 60;
            rule = $"limit:{day.LimitMin}";
        }
        if (remaining <= 0 || remaining > 10) return null;
        return new TimeWarning($"{now:yyyy-MM-dd}:{rule}", (int)Math.Ceiling(remaining));
    }
}
