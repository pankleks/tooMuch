using System.ComponentModel;
using TooMuch.Client;
using Xunit;

namespace TooMuch.Tests;

public class SessionGuardTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(179, true)]
    [InlineData(180, false)]
    [InlineData(300, false)]
    public void IdleThresholdUsesLastInputTime(int idleSeconds, bool expected)
    {
        var lastInput = DateTime.UtcNow.ToFileTimeUtc();
        Assert.Equal(expected, SessionGuard.IsWithinIdleThreshold(
            lastInput + idleSeconds * TimeSpan.TicksPerSecond, lastInput, 180));
    }

    [Fact]
    public void InvalidSessionTimestampsDoNotCountAsActivity()
    {
        Assert.False(SessionGuard.IsWithinIdleThreshold(100, 0, 180));
        Assert.False(SessionGuard.IsWithinIdleThreshold(100, 101, 180));
        Assert.False(SessionGuard.IsWithinIdleThreshold(100, 100, 0));
    }

    [Fact]
    public void DisabledActiveOnlyCountingDoesNotQueryNativeIdleInformation()
    {
        Assert.True(SessionGuard.IsCountingSession(-1, false, 180));
    }

    [Fact]
    public void FailedLockQueryRetainsChildForEnforcementAndContinuesScanning()
    {
        var errors = new List<Exception>();
        var scan = SessionGuard.ScanSessions(new[] { 1, 2, 3, 4 }, id => id != 4,
            id => id == 1 ? throw new Win32Exception(5) : id == 2, errors.Add);
        Assert.Equal(new[] { 1, 2, 3 }, scan.ActiveChild);
        Assert.Equal(new[] { 2 }, scan.Unlocked);
        Assert.Single(errors);
    }

    [Fact]
    public void FailedIdentityQueryDoesNotTargetUnknownUserOrSkipOtherChildren()
    {
        var errors = new List<Exception>();
        var scan = SessionGuard.ScanSessions(new[] { 1, 2, 3 },
            id => id == 1 ? throw new Win32Exception(5) : id == 3, _ => true, errors.Add);
        Assert.Equal(new[] { 3 }, scan.ActiveChild);
        Assert.Equal(new[] { 3 }, scan.Unlocked);
        Assert.Single(errors);
    }
}
