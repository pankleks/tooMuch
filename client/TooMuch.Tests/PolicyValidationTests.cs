using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TooMuch.Client;
using TooMuch.Core;
using Xunit;

namespace TooMuch.Tests;

public class PolicyValidationTests
{
    private static Policy ValidPolicy()
    {
        var policy = new Policy { DeviceId = "test", Version = 1 };
        for (var i = 1; i <= 7; i++) policy.Days[i.ToString()] = new DayConfig { LimitMin = 120 };
        return policy;
    }

    [Fact]
    public void ValidPolicyAllowsAdjacentWindowsAndLegacyMissingBonus()
    {
        var policy = ValidPolicy();
        policy.Days["1"].Windows = new() {
            new() { From = "08:00", To = "10:00" },
            new() { From = "10:00", To = "12:00" },
        };
        PolicyValidator.Validate(policy, "test");
        var legacy = JsonNode.Parse(JsonSerializer.Serialize(policy))!.AsObject();
        legacy.Remove("daily_bonus_date");
        legacy.Remove("daily_bonus_min");
        PolicyValidator.Validate(JsonSerializer.Deserialize<Policy>(legacy.ToJsonString()), "test");
    }

    private static string InvalidJson(string defect)
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(ValidPolicy()))!.AsObject();
        switch (defect)
        {
            case "null-policy": return "null";
            case "null-days": json["days"] = null; break;
            case "missing-day": json["days"]!.AsObject().Remove("7"); break;
            case "null-day": json["days"]!["1"] = null; break;
            case "null-windows": json["days"]!["1"]!["windows"] = null; break;
            case "missing-limit": json["days"]!["1"]!.AsObject().Remove("limit_min"); break;
            case "missing-force-lock": json.Remove("force_lock"); break;
            case "bad-limit": json["days"]!["1"]!["limit_min"] = -1; break;
            case "bad-zone": json["time_zone"] = "Not/A/TimeZone"; break;
            case "null-zone": json["time_zone"] = null; break;
            case "bad-id": json["device_id"] = "other"; break;
            case "bad-idle": json["idle_threshold_sec"] = 0; break;
            case "bad-bonus": json["daily_bonus_min"] = int.MaxValue; break;
            case "bad-date": json["daily_bonus_date"] = "2026-02-30"; break;
            case "bad-time": json["days"]!["1"]!["windows"] = JsonNode.Parse("[{\"from\":\"8:00\",\"to\":\"10:00\"}]"); break;
            case "null-window": json["days"]!["1"]!["windows"] = JsonNode.Parse("[null]"); break;
            case "overlap": json["days"]!["1"]!["windows"] = JsonNode.Parse("[{\"from\":\"08:00\",\"to\":\"10:00\"},{\"from\":\"09:00\",\"to\":\"11:00\"}]"); break;
            case "overnight": json["days"]!["1"]!["windows"] = JsonNode.Parse("[{\"from\":\"22:00\",\"to\":\"01:00\"}]"); break;
            default: throw new ArgumentException(defect);
        }
        return json.ToJsonString();
    }

    [Theory]
    [InlineData("null-policy")]
    [InlineData("null-days")]
    [InlineData("missing-day")]
    [InlineData("null-day")]
    [InlineData("null-windows")]
    [InlineData("missing-limit")]
    [InlineData("missing-force-lock")]
    [InlineData("bad-limit")]
    [InlineData("bad-zone")]
    [InlineData("null-zone")]
    [InlineData("bad-id")]
    [InlineData("bad-idle")]
    [InlineData("bad-bonus")]
    [InlineData("bad-date")]
    [InlineData("bad-time")]
    [InlineData("null-window")]
    [InlineData("overlap")]
    [InlineData("overnight")]
    public async Task InvalidResponsePreservesValidPolicyAndCache(string defect)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tm-validation-" + Guid.NewGuid());
        var handler = new PolicyHandler { Body = JsonSerializer.Serialize(ValidPolicy()) };
        try
        {
            using var agent = new Agent(new AgentConfig { DeviceId = "test", DataDir = dir, ServerUrl = "http://example.test" }, httpHandler: handler);
            await agent.PollConfigAsync(CancellationToken.None);
            var accepted = agent.Policy;
            var cache = File.ReadAllText(Path.Combine(dir, "policy.json"));
            handler.Body = InvalidJson(defect);
            var error = await Record.ExceptionAsync(() => agent.PollConfigAsync(CancellationToken.None));
            Assert.True(error is JsonException or InvalidDataException, error?.ToString());
            Assert.Same(accepted, agent.Policy);
            Assert.True(agent.HasPolicy);
            Assert.Equal(cache, File.ReadAllText(Path.Combine(dir, "policy.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("null-days")]
    [InlineData("bad-zone")]
    [InlineData("null-windows")]
    [InlineData("missing-force-lock")]
    public void CorruptCacheFailsClosedWithoutCrashing(string defect)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tm-cache-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "policy.json"), InvalidJson(defect));
            using var agent = new Agent(new AgentConfig { DeviceId = "test", DataDir = dir });
            Assert.False(agent.HasPolicy);
            Assert.Equal(State.Locked, PolicyEvaluator.Evaluate(agent.Policy, agent.LocalNow, 0).State);
            Assert.Equal("Blocked", ChildStatus.Create(agent.Policy, agent.LocalNow, 0, DateTimeOffset.UtcNow).State);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task InvalidFirstResponseLeavesTheClientBlocked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tm-first-policy-" + Guid.NewGuid());
        try
        {
            using var agent = new Agent(new AgentConfig { DeviceId = "test", DataDir = dir, ServerUrl = "http://example.test" },
                httpHandler: new PolicyHandler { Body = InvalidJson("null-days") });
            await Assert.ThrowsAsync<InvalidDataException>(() => agent.PollConfigAsync(CancellationToken.None));
            Assert.False(agent.HasPolicy);
            Assert.Equal(State.Locked, PolicyEvaluator.Evaluate(agent.Policy, agent.LocalNow, 0).State);
            Assert.False(File.Exists(Path.Combine(dir, "policy.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NullScheduleFailsClosedInEvaluatorAndStatusAndDoesNotGenerateWarnings(bool nullDays)
    {
        var policy = ValidPolicy();
        if (nullDays) policy.Days = null!;
        else policy.Days["1"].Windows = null!;
        var now = new DateTime(2026, 9, 21, 10, 0, 0);
        Assert.Equal(State.Locked, PolicyEvaluator.Evaluate(policy, now, 0).State);
        Assert.Equal("Blocked", ChildStatus.Create(policy, now, 0, DateTimeOffset.UtcNow).State);
        Assert.Null(TimeWarning.Evaluate(policy, now, 0));
    }

    private sealed class PolicyHandler : HttpMessageHandler
    {
        public string Body { get; set; } = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, System.Text.Encoding.UTF8, "application/json") });
    }
}
