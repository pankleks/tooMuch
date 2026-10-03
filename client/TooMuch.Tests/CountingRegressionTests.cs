using System.Net;
using System.Text.Json;
using TooMuch.Client;
using TooMuch.Core;
using Xunit;

namespace TooMuch.Tests;

public class CountingRegressionTests
{
    private static readonly DateTime Saturday = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0)]
    [InlineData(3600)]
    public async Task LocalGamingCountsPersistsReportsAndReachesLimitDespiteMissingOrStaleWtsInput(int idleSeconds)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tm-counting-" + Guid.NewGuid());
        var handler = new CaptureHandler();
        var now = Saturday;
        var cfg = new AgentConfig { DeviceId = "child", DataDir = dir, ServerUrl = "http://example.test" };
        try
        {
            using (var agent = new Agent(cfg, () => now, httpHandler: handler))
            {
                agent.Policy.Days["6"] = new DayConfig { LimitMin = 5 };
                Assert.True(agent.Policy.CountOnlyActive); // Same default as the affected PCs.
                var clock = new UsageClock();
                var sleep = new BlockedSleep();
                var sleepCalls = 0;
                (Decision Decision, bool Counting) tick = default;
                for (int second = 0; second <= 300; second++)
                {
                    now = Saturday.AddSeconds(second);
                    var active = LocalChildCounts(agent, now, idleSeconds, unlocked: true);
                    tick = UsageAccounting.Tick(agent, clock, TimeSpan.FromSeconds(second), active);
                    sleep.Tick(TimeSpan.FromSeconds(second), tick.Decision.State == State.Locked,
                        tick.Decision.State == State.Locked, () => sleepCalls++, ex => throw ex);
                    if (second == 120)
                    {
                        Assert.Equal(120, agent.ActiveSeconds);
                        Assert.True(tick.Counting);
                        await agent.SendHeartbeatAsync(false, tick.Counting, CancellationToken.None);
                        AssertHeartbeat(handler, minutes: 2, counting: true, locked: false);
                    }
                }
                Assert.NotNull(tick.Decision);
                Assert.Equal(300, agent.ActiveSeconds);
                Assert.Equal(State.Locked, tick.Decision.State);
                Assert.Equal(Reason.Limit, tick.Decision.Reason);
                Assert.False(tick.Counting);
                await agent.SendHeartbeatAsync(true, tick.Counting, CancellationToken.None);
                AssertHeartbeat(handler, minutes: 5, counting: false, locked: true);
                for (int second = 301; second <= 600; second++)
                {
                    now = Saturday.AddSeconds(second);
                    tick = UsageAccounting.Tick(agent, clock, TimeSpan.FromSeconds(second), false);
                    sleep.Tick(TimeSpan.FromSeconds(second), true, false, () => sleepCalls++, ex => throw ex);
                    if (second < 600) Assert.Equal(0, sleepCalls);
                }
                Assert.Equal(1, sleepCalls); // Sleep starts five minutes after enforcement, not during play.
                Assert.Equal(300, agent.ActiveSeconds);
            }
            using var restarted = new Agent(cfg, () => now);
            Assert.Equal(300, restarted.ActiveSeconds);
            Assert.Equal(5, restarted.ActiveMinToday);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LocalUsageExcludesLockedAndOtherUsersAndDoesNotChargeSleepGap()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tm-counting-" + Guid.NewGuid());
        try
        {
            using var agent = new Agent(new AgentConfig { DeviceId = "child", DataDir = dir }, () => Saturday);
            agent.Policy.Days["6"] = new DayConfig { LimitMin = 180 };
            var clock = new UsageClock();
            (Decision Decision, bool Counting) tick = default;
            for (int second = 0; second <= 60; second++)
                tick = UsageAccounting.Tick(agent, clock, TimeSpan.FromSeconds(second),
                    LocalChildCounts(agent, Saturday, 0, unlocked: true));
            Assert.Equal(60, agent.ActiveSeconds);
            Assert.True(tick.Counting);

            for (int second = 61; second <= 120; second++)
                tick = UsageAccounting.Tick(agent, clock, TimeSpan.FromSeconds(second),
                    LocalChildCounts(agent, Saturday, 0, unlocked: false));
            Assert.False(tick.Counting);
            Assert.Equal(60, agent.ActiveSeconds);

            // The selected child is absent; another account must never count.
            var otherUser = SessionGuard.ScanSessions(new[] { 2 }, _ => false, _ => true, ex => throw ex);
            tick = UsageAccounting.Tick(agent, clock, TimeSpan.FromSeconds(121), otherUser.Unlocked.Count > 0);
            Assert.False(tick.Counting);
            Assert.Equal(60, agent.ActiveSeconds);

            for (int second = 122; second <= 182; second++)
                tick = UsageAccounting.Tick(agent, clock, TimeSpan.FromSeconds(second),
                    LocalChildCounts(agent, Saturday, 0, unlocked: true));
            Assert.Equal(120, agent.ActiveSeconds);
            Assert.True(tick.Counting);

            // Hours asleep aren't charged, even if the next scan already sees an unlocked child.
            clock.Reset(); // The service resets on power notifications.
            tick = UsageAccounting.Tick(agent, clock, TimeSpan.FromHours(8), true);
            Assert.Equal(120, agent.ActiveSeconds);
            UsageAccounting.Tick(agent, clock, TimeSpan.FromHours(8) + TimeSpan.FromSeconds(1), true);
            Assert.Equal(121, agent.ActiveSeconds);
            UsageAccounting.Tick(agent, clock, TimeSpan.FromHours(16), true); // Missed notification / long loop gap.
            Assert.Equal(121, agent.ActiveSeconds);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static bool LocalChildCounts(Agent agent, DateTime now, int idleSeconds, bool unlocked)
    {
        var scan = SessionGuard.ScanSessions(new[] { 1, 2 }, id => id == 1,
            id => id == 1 && unlocked, ex => throw ex);
        return scan.Unlocked.Any(id => SessionGuard.IsCountingSession(id,
            agent.Policy.CountOnlyActive, agent.Policy.IdleThresholdSec, _ => 0,
            _ => (now.ToFileTimeUtc(), idleSeconds == 0 ? 0 : now.AddSeconds(-idleSeconds).ToFileTimeUtc()),
            ex => throw ex));
    }

    private static void AssertHeartbeat(CaptureHandler handler, int minutes, bool counting, bool locked)
    {
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal(minutes, payload.RootElement.GetProperty("active_min").GetDouble());
        Assert.Equal(counting, payload.RootElement.GetProperty("counting").GetBoolean());
        Assert.Equal(locked, payload.RootElement.GetProperty("locked").GetBoolean());
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
