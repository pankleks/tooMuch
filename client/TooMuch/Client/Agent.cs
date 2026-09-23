using System.Net.Http.Json;
using System.Text.Json;
using TooMuch.Core;

namespace TooMuch.Client;

public sealed class AgentConfig
{
    public string ServerUrl { get; set; } = "http://raspberrypi.local:3020";
    public string DeviceId { get; set; } = "";
    public string Token { get; set; } = "";
    public string DataDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TooMuch");
}

/// <summary>
/// Polling loop: GET config (30s, 10s in warning) + heartbeat 60s + local usage cache.
/// Enforcement (lock) is done by the caller based on returned Decision.
/// </summary>
public sealed class Agent : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly AgentConfig _cfg;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public Policy Policy { get; private set; } = new();
    public int ActiveMinToday { get; private set; }
    public string TodayKey { get; private set; } = DateKey(DateTime.Now);

    public Agent(AgentConfig cfg)
    {
        _cfg = cfg;
        Directory.CreateDirectory(_cfg.DataDir);
        LoadCache();
    }

    public static string DateKey(DateTime d) => d.ToString("yyyy-MM-dd");

    private string PolicyPath => Path.Combine(_cfg.DataDir, "policy.json");
    private string UsagePath(string date) => Path.Combine(_cfg.DataDir, $"usage-{date}.json");

    private void LoadCache()
    {
        try
        {
            if (File.Exists(PolicyPath))
                Policy = JsonSerializer.Deserialize<Policy>(File.ReadAllText(PolicyPath), Json) ?? new Policy();
            var up = UsagePath(TodayKey);
            if (File.Exists(up) && JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(up)) is { } m
                && m.TryGetValue("active_min", out var v)) ActiveMinToday = v;
        }
        catch { /* corrupt cache -> start clean, fail open until first poll */ }
    }

    private void SavePolicy()
    {
        var tmp = PolicyPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Policy));
        File.Move(tmp, PolicyPath, overwrite: true);
    }

    private void SaveUsage()
    {
        var tmp = UsagePath(TodayKey) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new { active_min = ActiveMinToday }));
        File.Move(tmp, UsagePath(TodayKey), overwrite: true);
    }

    public void TickDayRollover()
    {
        var key = DateKey(DateTime.Now);
        if (key != TodayKey) { TodayKey = key; ActiveMinToday = 0; SaveUsage(); }
    }

    public void AddActiveMinute()
    {
        ActiveMinToday++;
        SaveUsage();
    }

    public async Task<bool> PollConfigAsync(CancellationToken ct)
    {
        var url = $"{_cfg.ServerUrl.TrimEnd('/')}/api/config/{_cfg.DeviceId}?v={Policy.Version}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Device-Token", _cfg.Token);
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotModified) return false;
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("bad device token");
        res.EnsureSuccessStatusCode();
        var p = await res.Content.ReadFromJsonAsync<Policy>(Json, ct);
        if (p is null) return false;
        Policy = p;
        SavePolicy();
        return true;
    }

    public async Task SendHeartbeatAsync(bool locked, CancellationToken ct)
    {
        var url = $"{_cfg.ServerUrl.TrimEnd('/')}/api/heartbeat";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("X-Device-Token", _cfg.Token);
        req.Content = JsonContent.Create(new { device_id = _cfg.DeviceId, date = TodayKey, active_min = (double)ActiveMinToday, locked });
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
