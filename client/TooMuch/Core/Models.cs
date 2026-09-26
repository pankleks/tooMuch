using System.Text.Json.Serialization;

namespace TooMuch.Core;

public sealed class TimeWindow
{
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
}

public sealed class DayConfig
{
    [JsonPropertyName("limit_min")] public int LimitMin { get; set; } = 1440;
    [JsonPropertyName("windows")] public List<TimeWindow> Windows { get; set; } = new();
}

public sealed class Policy
{
    [JsonPropertyName("time_zone")] public string TimeZone { get; set; } = "Europe/Warsaw";
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("version")] public int Version { get; set; } = 0;
    [JsonPropertyName("force_lock")] public bool ForceLock { get; set; }
    [JsonPropertyName("idle_threshold_sec")] public int IdleThresholdSec { get; set; } = 180;
    [JsonPropertyName("count_only_active")] public bool CountOnlyActive { get; set; } = true;
    [JsonPropertyName("days")] public Dictionary<string, DayConfig> Days { get; set; } = new();
}

public enum State { Ok, Warning, Locked }

public enum Reason { None, Force, Limit, OutsideWindow, ConfigMissing }

public sealed record Decision(State State, Reason Reason, int RemainingMin, string Message);
