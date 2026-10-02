using TooMuch.Client;
using Xunit;

namespace TooMuch.Tests;

public class BlockedSleepTests
{
    [Fact]
    public void SleepsAfterFiveMinutesEvenWhenChildIsNoLongerActive()
    {
        var timer = new BlockedSleep();
        var calls = 0;
        void Tick(int seconds, bool disconnected) => timer.Tick(TimeSpan.FromSeconds(seconds),
            true, disconnected, () => calls++, ex => throw ex);

        Tick(10, true);
        Tick(100, true); // Signing in again must not postpone sleep.
        Tick(309, false);
        Assert.Equal(0, calls);
        Tick(310, false);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void LockedPolicyWithoutAChildDisconnectDoesNotSleep()
    {
        var timer = new BlockedSleep();
        timer.Tick(TimeSpan.Zero, true, false, () => Assert.Fail("Unexpected sleep"), ex => throw ex);
        timer.Tick(TimeSpan.FromHours(12), true, false, () => Assert.Fail("Unexpected sleep"), ex => throw ex);
    }

    [Fact]
    public void RestoredAccessCancelsDeadlineAndNextLockGetsFullDelay()
    {
        var timer = new BlockedSleep();
        var calls = 0;
        void Tick(int seconds, bool blocked, bool disconnected) => timer.Tick(TimeSpan.FromSeconds(seconds),
            blocked, disconnected, () => calls++, ex => throw ex);

        Tick(0, true, true);
        Tick(299, false, false);
        Tick(300, true, false);
        Tick(600, true, true);
        Tick(899, true, false);
        Assert.Equal(0, calls);
        Tick(900, true, false);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void FailedSleepIsLoggedAndRetriedAfterOneMinute()
    {
        var timer = new BlockedSleep();
        var errors = new List<Exception>();
        var calls = 0;
        var failure = new InvalidOperationException("Sleep unavailable");
        void Tick(int seconds) => timer.Tick(TimeSpan.FromSeconds(seconds), true, seconds == 0,
            () => { calls++; throw failure; }, errors.Add);

        Tick(0);
        Tick(300);
        Tick(359);
        Assert.Equal(1, calls);
        Assert.Same(failure, Assert.Single(errors));
        Tick(360);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ResumeGivesFiveMinutesBeforeSleepingAgain()
    {
        var timer = new BlockedSleep();
        var calls = 0;
        void Tick(int seconds) => timer.Tick(TimeSpan.FromSeconds(seconds), true, seconds == 0,
            () => calls++, ex => throw ex);

        Tick(0);
        Tick(300);
        timer.RestartDelay(TimeSpan.FromHours(8));
        Tick(8 * 3600 + 299);
        Assert.Equal(1, calls);
        Tick(8 * 3600 + 300);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ResumeDoesNotArmSleepWithoutPendingLock()
    {
        var timer = new BlockedSleep();
        timer.RestartDelay(TimeSpan.Zero);
        timer.Tick(TimeSpan.FromMinutes(5), true, false,
            () => Assert.Fail("Unexpected sleep"), ex => throw ex);
    }
}
