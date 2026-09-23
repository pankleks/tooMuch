namespace TooMuch.Core;

/// <summary>
/// Pure enforcement logic – no Win32, no I/O. Fully unit-testable on any OS.
/// Rule: windows != [] for the day => window mode (limit ignored, unlimited inside,
/// immediate lock outside). Else limit mode. force_lock always wins.
/// </summary>
public static class PolicyEvaluator
{
    public static int IsoWeekday(DateTime local) => ((int)local.DayOfWeek == 0) ? 7 : (int)local.DayOfWeek;

    public static bool IsInWindow(TimeOnly now, TimeWindow w)
    {
        if (!TimeOnly.TryParse(w.From, out var from) || !TimeOnly.TryParse(w.To, out var to)) return false;
        return from <= now && now < to;
    }

    public static Decision Evaluate(Policy policy, DateTime localNow, int activeMinToday)
    {
        if (policy.ForceLock)
            return new Decision(State.Locked, Reason.Force, 0, "Locked by parent");

        var wd = IsoWeekday(localNow).ToString();
        if (!policy.Days.TryGetValue(wd, out var day) || day is null)
            return new Decision(State.Locked, Reason.ConfigMissing, 0, "Missing day configuration");

        if (day.Windows is { Count: > 0 })
        {
            var now = TimeOnly.FromDateTime(localNow);
            foreach (var w in day.Windows)
                if (IsInWindow(now, w))
                    return new Decision(State.Ok, Reason.None, int.MaxValue, "Within allowed hours");
            return new Decision(State.Locked, Reason.OutsideWindow, 0, "Outside allowed hours");
        }

        if (day.LimitMin <= 0)
            return new Decision(State.Locked, Reason.Limit, 0, "Daily limit exhausted (0)");

        var remaining = day.LimitMin - activeMinToday;
        if (remaining <= 0)
            return new Decision(State.Locked, Reason.Limit, 0, "Daily limit exhausted");
        if (remaining <= 15)
            return new Decision(State.Warning, Reason.None, remaining, $"{remaining} min left");
        return new Decision(State.Ok, Reason.None, remaining, $"{remaining} min left");
    }
}
