using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TooMuch.Core;

namespace TooMuch.Client;

internal sealed class SessionMessages
{
    private readonly AgentConfig config;
    private readonly Action<Exception> log;
    private Task messages = Task.CompletedTask;
    private Task warning = Task.CompletedTask;
    private DateTime nextPoll;
    private readonly HashSet<string> warned;
    private readonly HashSet<string> confirmed;
    private readonly HashSet<string> attempted = new();
    private string WarningFile => Path.Combine(config.DataDir, "warnings.json");
    private string ConfirmedFile => Path.Combine(config.DataDir, "confirmed-messages.json");

    public SessionMessages(AgentConfig config, Action<Exception> log)
    {
        this.config = config;
        this.log = log;
        warned = Read(WarningFile);
        confirmed = Read(ConfirmedFile);
    }

    private HashSet<string> Read(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(file)) ?? new() : new(); }
        catch (Exception ex) { log(ex); return new(); }
    }
    private static void Save(string file, HashSet<string> values)
    {
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(values));
        File.Move(tmp, file, true);
    }

    public void Tick(Agent agent, IReadOnlyList<int> sessions, CancellationToken ct)
    {
        if (sessions.Count == 0) return;
        var alert = TimeWarning.Evaluate(agent.Policy, agent.LocalNow, agent.ActiveSeconds);
        if (alert != null && warning.IsCompleted && !warned.Contains(alert.Key))
        {
            var session = sessions[0];
            warning = Task.Run(() => {
                try
                {
                    if (ct.IsCancellationRequested || !SessionGuard.UnlockedSessions(config.ChildSid).Contains(session)) return;
                    SessionGuard.ShowMessage(session, "Pozostały czas", $"Pozostało minut: {alert.Minutes}.\nSesja zostanie rozłączona po upływie czasu. Zapisz swoją pracę.", 60);
                    warned.RemoveWhere(k => !k.StartsWith(agent.TodayKey + ":", StringComparison.Ordinal));
                    warned.Add(alert.Key);
                    Save(WarningFile, warned);
                }
                catch (Exception ex) { log(ex); }
            });
        }
        if (messages.IsCompleted && DateTime.UtcNow >= nextPoll)
        {
            nextPoll = DateTime.UtcNow.AddSeconds(10);
            messages = Task.Run(() => Receive(ct));
        }
    }

    private sealed class Envelope { public List<Message> Messages { get; set; } = new(); }
    private sealed class Message
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; set; }
    }
    private async Task Receive(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Add("X-Device-Token", config.Token);
            var url = $"{config.ServerUrl.TrimEnd('/')}/api/devices/{Uri.EscapeDataString(config.DeviceId)}/messages";
            var envelope = await http.GetFromJsonAsync<Envelope>(url, ct);
            var pending = envelope?.Messages ?? new();
            attempted.IntersectWith(pending.Select(m => m.Id));
            foreach (var message in pending)
            {
                if (ct.IsCancellationRequested) return;
                if (confirmed.Contains(message.Id)) { await Ack("confirmed"); continue; }
                if (attempted.Contains(message.Id)) continue;
                var sessions = SessionGuard.UnlockedSessions(config.ChildSid);
                var remaining = (message.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds;
                if (sessions.Count == 0 || remaining < 1) return;
                await Ack("delivered");
                // Recheck after HTTP: the child may have locked or left the session.
                if (!SessionGuard.UnlockedSessions(config.ChildSid).Contains(sessions[0])) return;
                remaining = (message.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds;
                if (remaining < 1) continue;
                var ok = SessionGuard.ShowMessage(sessions[0], "Wiadomość od rodzica", message.Text,
                    (uint)Math.Clamp(remaining, 1, 60));
                attempted.Add(message.Id);
                if (ok)
                {
                    confirmed.Add(message.Id);
                    if (confirmed.Count > 200) confirmed.RemoveWhere(id => !pending.Any(m => m.Id == id));
                    Save(ConfirmedFile, confirmed);
                    await Ack("confirmed");
                }
                // One dialog per poll leaves room for automatic time warnings.
                break;

                async Task Ack(string status)
                {
                    using var response = await http.PostAsJsonAsync($"{url}/{message.Id}", new { status }, ct);
                    response.EnsureSuccessStatusCode();
                }
            }
        }
        catch (Exception ex) { if (!ct.IsCancellationRequested) log(ex); }
    }
}
