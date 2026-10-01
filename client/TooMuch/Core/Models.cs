using System.Text.Json.Serialization;

namespace TooMuch.Core;

public sealed class TimeWindow
{
    [JsonRequired, JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonRequired, JsonPropertyName("to")] public string To { get; set; } = "";
}

public sealed class DayConfig
{
    [JsonRequired, JsonPropertyName("limit_min")] public int LimitMin { get; set; } = 1440;
    [JsonRequired, JsonPropertyName("windows")] public List<TimeWindow> Windows { get; set; } = new();
}

public sealed class Policy
{
    [JsonRequired, JsonPropertyName("time_zone")] public string TimeZone { get; set; } = "Europe/Warsaw";
    [JsonRequired, JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonRequired, JsonPropertyName("version")] public int Version { get; set; } = 0;
    [JsonRequired, JsonPropertyName("force_lock")] public bool ForceLock { get; set; }
    [JsonRequired, JsonPropertyName("idle_threshold_sec")] public int IdleThresholdSec { get; set; } = 180;
    [JsonRequired, JsonPropertyName("count_only_active")] public bool CountOnlyActive { get; set; } = true;
    [JsonPropertyName("daily_bonus_date")] public string DailyBonusDate { get; set; } = "";
    [JsonPropertyName("daily_bonus_min")] public int DailyBonusMin { get; set; }
    [JsonRequired, JsonPropertyName("days")] public Dictionary<string, DayConfig> Days { get; set; } = new();
}

public enum State { Ok, Warning, Locked }

public enum Reason { None, Force, Limit, OutsideWindow, ConfigMissing }

public sealed record Decision(State State, Reason Reason, int RemainingMin, string Message);
