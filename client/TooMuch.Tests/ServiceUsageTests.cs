using TooMuch.Client;
using Xunit;

namespace TooMuch.Tests;

public class ServiceUsageTests
{
    [Fact]
    public void PartialMinuteSurvivesRestartAndMidnightUsesServerTimezone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tm-service-" + Guid.NewGuid());
        var now = new DateTime(2026, 9, 26, 21, 59, 58, DateTimeKind.Utc);
        var cfg = new AgentConfig { DeviceId = "test", DataDir = dir };
        try
        {
            using (var agent = new Agent(cfg, () => now))
            {
                Assert.Equal("2026-09-26", agent.TodayKey);
                agent.AddSeconds(59.5);
                Assert.Equal(0, agent.ActiveMinToday);
            }
            using (var agent = new Agent(cfg, () => now))
            {
                agent.AddSeconds(0.5);
                Assert.Equal(1, agent.ActiveMinToday);
                now = now.AddSeconds(3);
                agent.TickDayRollover();
                Assert.Equal("2026-09-27", agent.TodayKey);
                Assert.Equal(0, agent.ActiveMinToday);
                agent.AddSeconds(120);
            }
            using var restarted = new Agent(cfg, () => now);
            Assert.Equal(2, restarted.ActiveMinToday);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LegacyUsageIsPreservedAndAnotherDevicePolicyIsRejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tm-service-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "usage-2026-09-26.json"), """{"active_min":45}""");
            File.WriteAllText(Path.Combine(dir, "policy.json"), """{"device_id":"other","version":100,"force_lock":false}""");
            using var agent = new Agent(new AgentConfig { DeviceId = "test", DataDir = dir },
                () => new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc));
            Assert.Equal(45, agent.ActiveMinToday);
            Assert.Equal(0, agent.Policy.Version);
        }
        finally { Directory.Delete(dir, true); }
    }
}
