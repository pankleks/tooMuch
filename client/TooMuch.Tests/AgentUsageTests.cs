using TooMuch.Client;
using Xunit;

namespace TooMuch.Tests;

public class AgentUsageTests
{
    static string NewDir() => Path.Combine(Path.GetTempPath(), "tm-test-" + Guid.NewGuid().ToString("N"));

    static AgentConfig Cfg(string dir) => new()
    {
        ServerUrl = "http://localhost:9",
        DeviceId = "d",
        Token = "t",
        DataDir = dir,
    };

    static string UsageFile(string dir) => Path.Combine(dir, $"usage-{Agent.DateKey(DateTime.Now)}.json");

    [Fact]
    public void AddActiveMinute_Increments_And_Persists()
    {
        var dir = NewDir();
        try
        {
            using (var a = new Agent(Cfg(dir)))
            {
                Assert.Equal(0, a.ActiveMinToday);
                a.AddActiveMinute();
                a.AddActiveMinute();
                Assert.Equal(2, a.ActiveMinToday);
            }
            using (var b = new Agent(Cfg(dir)))
            {
                Assert.Equal(2, b.ActiveMinToday);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReloadUsage_Picks_Up_External_Writer()
    {
        var dir = NewDir();
        try
        {
            using var a = new Agent(Cfg(dir));
            Assert.Equal(0, a.ActiveMinToday);
            // simulate the tray process writing while service holds its own copy
            Directory.CreateDirectory(dir);
            File.WriteAllText(UsageFile(dir), """{"active_min":5}""");
            a.ReloadUsage();
            Assert.Equal(5, a.ActiveMinToday);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AddActiveMinute_Merges_Instead_Of_Clobbering()
    {
        var dir = NewDir();
        try
        {
            using var a = new Agent(Cfg(dir));
            a.AddActiveMinute();
            a.AddActiveMinute();
            Assert.Equal(2, a.ActiveMinToday);
            // another writer moved on meanwhile: merge must continue from 5, not 2
            File.WriteAllText(UsageFile(dir), """{"active_min":5}""");
            a.AddActiveMinute();
            Assert.Equal(6, a.ActiveMinToday);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReloadUsage_Ignores_Corrupt_File()
    {
        var dir = NewDir();
        try
        {
            using var a = new Agent(Cfg(dir));
            a.AddActiveMinute();
            File.WriteAllText(UsageFile(dir), "not-json{{{");
            a.ReloadUsage();
            Assert.Equal(1, a.ActiveMinToday);
        }
        finally { Directory.Delete(dir, true); }
    }
}
