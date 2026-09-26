using TooMuch.Core;
using Xunit;

namespace TooMuch.Tests;

public class UsageClockTests
{
    [Fact]
    public void FilmWithoutInputCountsWhileUnlocked()
    {
        var clock = new UsageClock();
        double seconds = 0;
        for (int i = 0; i <= 120; i++) seconds += clock.Sample(TimeSpan.FromSeconds(i), true);
        Assert.Equal(120, seconds);
    }

    [Fact]
    public void LockedLoggedOutAndParentSessionDoNotCount()
    {
        var clock = new UsageClock();
        clock.Sample(TimeSpan.Zero, true);
        Assert.Equal(1, clock.Sample(TimeSpan.FromSeconds(1), true));
        Assert.Equal(0, clock.Sample(TimeSpan.FromSeconds(2), false));
        Assert.Equal(0, clock.Sample(TimeSpan.FromSeconds(3), false));
        Assert.Equal(0, clock.Sample(TimeSpan.FromSeconds(4), true));
        Assert.Equal(1, clock.Sample(TimeSpan.FromSeconds(5), true));
    }

    [Fact]
    public void SuspendAndSessionSwitchDiscardUnobservedInterval()
    {
        var clock = new UsageClock();
        clock.Sample(TimeSpan.Zero, true);
        Assert.Equal(0, clock.Sample(TimeSpan.FromHours(2), true));
        Assert.Equal(1, clock.Sample(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1), true));
        clock.Reset();
        Assert.Equal(0, clock.Sample(TimeSpan.FromHours(3), true));
    }
}
