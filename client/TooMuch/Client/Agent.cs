using System.Net.Http.Json;
using System.Text.Json;
using TooMuch.Core;

namespace TooMuch.Client;

public sealed class AgentConfig
{
    public string ServerUrl { get; set; } = "http://raspberrypi.local:3020";
    public string DeviceId { get; set; } = "";
    public string Token { get; set; } = "";
    public string ChildSid { get; set; } = "";
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
    private readonly Func<DateTime> utcNow;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public Policy Policy { get; private set; } = new();
    public double ActiveSeconds { get; private set; }
    public int ActiveMinToday => (int)(ActiveSeconds / 60);
    public bool HasPolicy => Policy.DeviceId == _cfg.DeviceId && Policy.Days.Count == 7;
    public DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(utcNow(),
        TimeZoneInfo.FindSystemTimeZoneById(Policy.TimeZone));
    public string TodayKey { get; private set; } = "";

    public Agent(AgentConfig cfg, Func<DateTime>? utcNow = null)
    {
        _cfg = cfg;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
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
            {
                var cached = JsonSerializer.Deserialize<Policy>(File.ReadAllText(PolicyPath), Json);
                if (cached?.DeviceId == _cfg.DeviceId) Policy = cached;
            }
        }
        catch (JsonException) { /* Missing policy fails closed in PolicyEvaluator. */ }
        TodayKey = DateKey(LocalNow);
        ReloadUsage();
    }

    private void SavePolicy()
    {
        var tmp = PolicyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Policy));
        File.Move(tmp, PolicyPath, overwrite: true);
    }

    private void SaveUsage()
    {
        var tmp = UsagePath(TodayKey) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new { active_min = ActiveMinToday, active_seconds = ActiveSeconds }));
        File.Move(tmp, UsagePath(TodayKey), overwrite: true);
    }

    public void TickDayRollover()
    {
        var key = DateKey(LocalNow);
        if (key != TodayKey)
        {
            SaveUsage();
            TodayKey = key;
            ActiveSeconds = 0;
            ReloadUsage();
        }
    }

    /// <summary>
    /// Read persisted usage, including minute-only files from older clients.
    /// Only the service writes usage. Never reduce the in-memory counter.
    /// </summary>
    public void ReloadUsage()
    {
        try
        {
            var up = UsagePath(TodayKey);
            if (File.Exists(up) && JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(up)) is { } m)
            {
                var seconds = m.TryGetValue("active_seconds", out var s) ? s : m.GetValueOrDefault("active_min") * 60;
                if (double.IsFinite(seconds) && seconds >= 0) ActiveSeconds = Math.Max(ActiveSeconds, seconds);
            }
        }
        catch { }
    }

    public void AddActiveMinute()
    {
        // Compatibility helper for whole-minute callers.
        ReloadUsage();
        ActiveSeconds += 60;
        SaveUsage();
    }

    public void AddSeconds(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        ActiveSeconds += seconds;
        SaveUsage();
    }

    public async Task<bool> PollConfigAsync(CancellationToken ct)
    {
        var url = $"{_cfg.ServerUrl.TrimEnd('/')}/api/config/{_cfg.DeviceId}?v={Policy.Version}&tz={Uri.EscapeDataString(Policy.TimeZone)}&date={Uri.EscapeDataString(TodayKey)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Device-Token", _cfg.Token);
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotModified) return false;
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("bad device token");
        res.EnsureSuccessStatusCode();
        var p = await res.Content.ReadFromJsonAsync<Policy>(Json, ct);
        if (p is null) return false;
        if (p.DeviceId != _cfg.DeviceId) throw new InvalidDataException("Policy device identity mismatch.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(p.TimeZone);
        Policy = p;
        SavePolicy();
        return true;
    }

    public async Task SendHeartbeatAsync(bool locked, bool counting, CancellationToken ct)
    {
        var url = $"{_cfg.ServerUrl.TrimEnd('/')}/api/heartbeat";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("X-Device-Token", _cfg.Token);
        req.Content = JsonContent.Create(new { device_id = _cfg.DeviceId, date = TodayKey, active_min = (double)ActiveMinToday, locked, counting });
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
