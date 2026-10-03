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

    [Theory]
    [InlineData(0)] // Physical console often has no WTS LastInputTime at all.
    [InlineData(3600)] // Stale WTS timestamps must not pause Minecraft/video screen time either.
    public void LocalUnlockedSessionCountsWithoutReadingUnreliableInputTimes(int idleSeconds)
    {
        var current = DateTime.UtcNow.ToFileTimeUtc();
        var lastInput = idleSeconds == 0 ? 0 : current - idleSeconds * TimeSpan.TicksPerSecond;
        Assert.False(SessionGuard.IsWithinIdleThreshold(current, lastInput, 180)); // Old regression.
        var inputQueries = 0;
        Assert.True(SessionGuard.IsCountingSession(1, true, 180, _ => 0,
            _ => { inputQueries++; return (current, lastInput); }, ex => throw ex));
        Assert.Equal(0, inputQueries);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(179, true)]
    [InlineData(180, false)]
    [InlineData(300, false)]
    public void RemoteSessionsStillHonorValidIdleTimes(int idleSeconds, bool expected)
    {
        var current = DateTime.UtcNow.ToFileTimeUtc();
        Assert.Equal(expected, SessionGuard.IsCountingSession(1, true, 180, _ => 2,
            _ => (current, current - idleSeconds * TimeSpan.TicksPerSecond), ex => throw ex));
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(0, 0)]
    [InlineData(100, 101)]
    public void UnknownRemoteInputDataDoesNotStopVerifiedUnlockedSessionCounting(long current, long lastInput)
    {
        Assert.True(SessionGuard.IsCountingSession(1, true, 180, _ => 2,
            _ => (current, lastInput), ex => throw ex));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedActivityQueriesLogAndKeepVerifiedUnlockedChildCounting(bool protocolFails)
    {
        var errors = new List<Exception>();
        Assert.True(SessionGuard.IsCountingSession(1, true, 180,
            _ => protocolFails ? throw new Win32Exception(5) : (ushort)2,
            _ => throw new Win32Exception(5), errors.Add));
        Assert.IsType<Win32Exception>(Assert.Single(errors));
    }

    [Fact]
    public void DisabledIdleFilteringDoesNotInvokeAnyActivityQueries()
    {
        Assert.True(SessionGuard.IsCountingSession(1, false, 180,
            _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException(), ex => throw ex));
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
