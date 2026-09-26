using TooMuch.Core;
using Xunit;

namespace TooMuch.Tests;

public class ChildStatusTests
{
    private static readonly DateTime Monday = new(2026, 9, 21, 16, 0, 0);

    [Fact]
    public void DailyLimitShowsRemainingAndBlockedStatus()
    {
        var policy = new Policy { Days = new() { ["1"] = new() { LimitMin = 90 } } };
        var available = ChildStatus.Create(policy, Monday, 30 * 60, DateTimeOffset.UnixEpoch);
        Assert.Equal("Available", available.State);
        Assert.Equal(60 * 60, available.RemainingSeconds);
        Assert.Equal(30 * 60, available.UsedSeconds);

        var blocked = ChildStatus.Create(policy, Monday, 90 * 60, DateTimeOffset.UnixEpoch);
        Assert.Equal("Blocked", blocked.State);
        Assert.Equal("Daily time limit reached", blocked.Reason);
    }

    [Fact]
    public void WindowShowsTimeUntilCombinedAdjacentWindowEnds()
    {
        var policy = new Policy { Days = new() { ["1"] = new() { Windows = new()
        {
            new TimeWindow { From = "16:00", To = "17:00" },
            new TimeWindow { From = "17:00", To = "18:00" }
        } } } };
        var status = ChildStatus.Create(policy, Monday.AddMinutes(50), 5000, DateTimeOffset.UnixEpoch);
        Assert.Equal("Available", status.State);
        Assert.Equal("18:00", status.AccessUntil);
        Assert.Equal(70 * 60, status.RemainingSeconds);
    }

    [Fact]
    public void ForceLockIsShownAsBlockedByParent()
    {
        var policy = new Policy { ForceLock = true, Days = new() { ["1"] = new() { LimitMin = 90 } } };
        var status = ChildStatus.Create(policy, Monday, 0, DateTimeOffset.UnixEpoch);
        Assert.Equal("Blocked", status.State);
        Assert.Equal("Blocked by parent", status.Reason);
    }
}
