using TooMuch.Core;
using Xunit;

namespace TooMuch.Tests;

public class PolicyEvaluatorTests
{
    static Policy BasePolicy()
    {
        var p = new Policy { DeviceId = "pc-test", Version = 1 };
        for (int i = 1; i <= 7; i++)
            p.Days[i.ToString()] = new DayConfig { LimitMin = i <= 5 ? 120 : 300 };
        return p;
    }

    // Monday 2026-09-21 is weekday 1
    static DateTime Mon(double hour) => new DateTime(2026, 9, 21, (int)hour, (int)((hour % 1) * 60), 0);

    [Fact]
    public void Limit_Allows_Below_And_Locks_At_Zero()
    {
        var p = BasePolicy();
        Assert.Equal(State.Ok, PolicyEvaluator.Evaluate(p, Mon(10), 50).State);
        Assert.Equal(State.Warning, PolicyEvaluator.Evaluate(p, Mon(10), 110).State);
        var locked = PolicyEvaluator.Evaluate(p, Mon(10), 120);
        Assert.Equal(State.Locked, locked.State);
        Assert.Equal(Reason.Limit, locked.Reason);
    }

    [Fact]
    public void Zero_Limit_Blocks_All_Day()
    {
        var p = BasePolicy();
        p.Days["1"] = new DayConfig { LimitMin = 0 };
        Assert.Equal(State.Locked, PolicyEvaluator.Evaluate(p, Mon(10), 0).State);
    }

    [Fact]
    public void DailyBonusAddsToOnlyTheMatchingDateLimit()
    {
        var p = BasePolicy();
        p.DailyBonusDate = "2026-09-21";
        p.DailyBonusMin = 30;

        var withinBonus = PolicyEvaluator.Evaluate(p, Mon(10), 140);
        Assert.Equal(State.Warning, withinBonus.State);
        Assert.Equal(10, withinBonus.RemainingMin);
        Assert.Equal(State.Locked, PolicyEvaluator.Evaluate(p, Mon(10), 150).State);
        Assert.Equal(State.Locked, PolicyEvaluator.Evaluate(p, Mon(10).AddDays(1), 120).State);
    }

    [Fact]
    public void DailyBonusCanGrantTimeWhenBaseLimitIsZero()
    {
        var p = BasePolicy();
        p.Days["1"].LimitMin = 0;
        p.DailyBonusDate = "2026-09-21";
        p.DailyBonusMin = 15;

        var decision = PolicyEvaluator.Evaluate(p, Mon(10), 0);
        Assert.Equal(State.Warning, decision.State);
        Assert.Equal(15, decision.RemainingMin);
    }

    [Fact]
    public void Window_Replaces_Limit_Inside_Unlimited_Outside_Locked()
    {
        var p = BasePolicy();
        p.Days["3"] = new DayConfig { LimitMin = 120, Windows = new() { new TimeWindow { From = "16:00", To = "20:00" } } };
        p.DailyBonusDate = "2026-09-23";
        p.DailyBonusMin = 60;
        var wed = new DateTime(2026, 9, 23, 17, 0, 0); // Wednesday
        var inside = PolicyEvaluator.Evaluate(p, wed, 9999);
        Assert.Equal(State.Ok, inside.State); // limit ignored
        var outside = PolicyEvaluator.Evaluate(p, new DateTime(2026, 9, 23, 21, 0, 0), 0);
        Assert.Equal(State.Locked, outside.State);
        Assert.Equal(Reason.OutsideWindow, outside.Reason);
    }

    [Fact]
    public void ForceLock_Wins_Over_Everything()
    {
        var p = BasePolicy();
        p.ForceLock = true;
        Assert.Equal(Reason.Force, PolicyEvaluator.Evaluate(p, Mon(10), 0).Reason);
    }

    [Fact]
    public void Missing_Day_Fails_Closed()
    {
        var p = new Policy { ForceLock = false };
        Assert.Equal(State.Locked, PolicyEvaluator.Evaluate(p, Mon(10), 0).State);
    }
}
