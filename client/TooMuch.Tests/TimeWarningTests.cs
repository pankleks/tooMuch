using TooMuch.Core;
using Xunit;

namespace TooMuch.Tests;

public class TimeWarningTests
{
    private static readonly DateTime Monday = new(2026, 9, 21, 16, 0, 0);
    private static Policy Policy() => new() { Days = new() { ["1"] = new() { LimitMin = 60 } } };

    [Fact]
    public void DailyThresholdAndShortenedLimitUseActualRemainingTime()
    {
        var policy = Policy();
        Assert.Null(TimeWarning.Evaluate(policy, Monday, 49 * 60));
        var ten = TimeWarning.Evaluate(policy, Monday, 50 * 60)!;
        Assert.Equal(10, ten.Minutes);
        Assert.Equal(ten.Key, TimeWarning.Evaluate(policy, Monday, 51 * 60)!.Key);
        policy.Days["1"].LimitMin = 53;
        Assert.Equal(3, TimeWarning.Evaluate(policy, Monday, 50 * 60)!.Minutes);
        Assert.Null(TimeWarning.Evaluate(policy, Monday, 53 * 60));
    }

    [Fact]
    public void WindowWarnsBeforeEndRegardlessOfUsage()
    {
        var policy = Policy();
        policy.Days["1"].Windows.Add(new() { From = "16:00", To = "17:00" });
        Assert.Null(TimeWarning.Evaluate(policy, Monday.AddMinutes(49), 99999));
        Assert.Equal(10, TimeWarning.Evaluate(policy, Monday.AddMinutes(50), 99999)!.Minutes);
        Assert.Equal(1, TimeWarning.Evaluate(policy, Monday.AddMinutes(59.5), 99999)!.Minutes);
        Assert.Null(TimeWarning.Evaluate(policy, Monday.AddHours(1), 0));
        policy.ForceLock = true;
        Assert.Null(TimeWarning.Evaluate(policy, Monday.AddMinutes(50), 0));
    }

    [Fact]
    public void AdjacentWindowsDoNotGiveFalseWarning()
    {
        var policy = Policy();
        policy.Days["1"].Windows.AddRange(new[] {
            new TimeWindow { From = "16:00", To = "17:00" },
            new TimeWindow { From = "17:00", To = "18:00" }
        });
        Assert.Null(TimeWarning.Evaluate(policy, Monday.AddMinutes(50), 0));
        Assert.Equal(10, TimeWarning.Evaluate(policy, Monday.AddMinutes(110), 0)!.Minutes);
    }
}
