using System.ComponentModel;
using TooMuch.Client;
using Xunit;

namespace TooMuch.Tests;

public class SessionGuardTests
{
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
