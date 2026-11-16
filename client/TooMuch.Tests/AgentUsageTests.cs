using System.Globalization;
using System.Net;
using System.Text.Json;
using TooMuch.Client;
using TooMuch.Core;
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

    static string Today() => Agent.DateKey(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw")));
    static string UsageFile(string dir) => Path.Combine(dir, $"usage-{Today()}.json");

    [Fact]
    public void AddSeconds_Increments_And_Persists()
    {
        var dir = NewDir();
        try
        {
            using (var a = new Agent(Cfg(dir)))
            {
                Assert.Equal(0, a.ActiveMinToday);
                a.AddSeconds(60);
                a.AddSeconds(60);
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
    public void ReloadUsage_Reads_Legacy_Minute_File()
    {
        var dir = NewDir();
        try
        {
            using var a = new Agent(Cfg(dir));
            Assert.Equal(0, a.ActiveMinToday);
            Directory.CreateDirectory(dir);
            File.WriteAllText(UsageFile(dir), """{"active_min":5}""");
            a.ReloadUsage();
            Assert.Equal(5, a.ActiveMinToday);
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
            a.AddSeconds(60);
            File.WriteAllText(UsageFile(dir), "not-json{{{");
            a.ReloadUsage();
            Assert.Equal(1, a.ActiveMinToday);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Lost_Usage_File_Is_Restored_From_Server()
    {
        var dir = NewDir();
        var policy = new Policy { DeviceId = "d", Version = 3, DailyBonusDate = Today(), TodayActiveMin = 42 };
        for (var i = 1; i <= 7; i++) policy.Days[i.ToString()] = new DayConfig { LimitMin = 120 };
        var handler = new PolicyHandler(JsonSerializer.Serialize(policy));
        try
        {
            // A power cut left an empty usage file: the agent starts from zero.
            Directory.CreateDirectory(dir);
            File.WriteAllText(UsageFile(dir), "");
            using var a = new Agent(Cfg(dir), httpHandler: handler);
            Assert.Equal(0, a.ActiveMinToday);

            await a.PollConfigAsync(CancellationToken.None);
            Assert.Contains("v=0", handler.LastUrl); // first poll must not get 304
            a.TickDayRollover();
            Assert.Equal(42, a.ActiveMinToday);
            using (var restarted = new Agent(Cfg(dir))) Assert.Equal(42, restarted.ActiveMinToday);

            // Server value never lowers local usage.
            a.AddSeconds(5 * 60);
            a.TickDayRollover();
            Assert.Equal(47, a.ActiveMinToday);

            await a.PollConfigAsync(CancellationToken.None);
            Assert.Contains("v=3", handler.LastUrl);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DateKey_Uses_Gregorian_Calendar_Regardless_Of_Culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            Assert.Equal("2026-11-16", Agent.DateKey(new DateTime(2026, 11, 16)));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    private sealed class PolicyHandler(string body) : HttpMessageHandler
    {
        public string LastUrl { get; private set; } = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
