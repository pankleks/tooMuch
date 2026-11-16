namespace TooMuch.Core;

public sealed record ChildStatus(
    string State,
    string Reason,
    string Mode,
    double UsedSeconds,
    double? RemainingSeconds,
    string? AccessUntil,
    DateTimeOffset UpdatedAtUtc)
{
    public static ChildStatus Create(Policy policy, DateTime localNow, double usedSeconds, DateTimeOffset updatedAtUtc)
    {
        var decision = PolicyEvaluator.Evaluate(policy, localNow, (int)(usedSeconds / 60));
        var weekday = PolicyEvaluator.IsoWeekday(localNow).ToString();
        if (policy.Days is null || !policy.Days.TryGetValue(weekday, out var day) || !PolicyValidator.IsValidDay(day))
            return new("Blocked", decision.Message, "Schedule", usedSeconds, null, null, updatedAtUtc);

        if (policy.ForceLock)
            return new("Blocked", "Blocked by parent", "Manual lock", usedSeconds, null, null, updatedAtUtc);

        if (day.Windows.Count > 0)
        {
            if (PolicyEvaluator.CurrentWindowEnd(day, TimeOnly.FromDateTime(localNow)) is not { } end)
                return new("Blocked", "Outside allowed hours", "Allowed hours", usedSeconds, null, null, updatedAtUtc);
            return new("Available", "Within allowed hours", "Allowed hours", usedSeconds,
                Math.Max(0, (end.ToTimeSpan() - localNow.TimeOfDay).TotalSeconds),
                end.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), updatedAtUtc);
        }

        var limitMin = PolicyEvaluator.EffectiveDailyLimitMin(policy, localNow, day);
        var remaining = Math.Max(0, limitMin * 60d - usedSeconds);
        return decision.State == global::TooMuch.Core.State.Locked
            ? new("Blocked", "Daily time limit reached", "Daily limit", usedSeconds, 0, null, updatedAtUtc)
            : new("Available", "Daily limit", "Daily limit", usedSeconds, remaining, null, updatedAtUtc);
    }
}
