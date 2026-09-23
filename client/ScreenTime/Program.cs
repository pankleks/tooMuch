using Microsoft.Win32;
using ScreenTime.Client;
using ScreenTime.Core;
using ScreenTime.Win32;

namespace ScreenTime;

// Single exe, two modes: --service (SYSTEM, enforcement) and --tray (user, countdown+overlay).
internal static class Program
{
    [STAThread]
    static async Task<int> Main(string[] args)
    {
        var mode = args.FirstOrDefault() switch
        {
            "--service" => "service",
            "--tray" => "tray",
            _ => "tray",
        };
        var cfg = LoadConfig();
        if (string.IsNullOrEmpty(cfg.DeviceId) || string.IsNullOrEmpty(cfg.Token))
        {
            Console.Error.WriteLine("Brak DeviceId/Token. Uruchom install.ps1 jako admin.");
            return 2;
        }
        return mode == "service" ? await RunServiceAsync(cfg) : RunTray(cfg);
    }

    static AgentConfig LoadConfig()
    {
        // 1) HKLM (zapisane przez install.ps1 jako admin), 2) ProgramData fallback
        var cfg = new AgentConfig();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ScreenTime");
            if (key != null)
            {
                cfg.ServerUrl = (key.GetValue("ServerUrl") as string) ?? cfg.ServerUrl;
                cfg.DeviceId = (key.GetValue("DeviceId") as string) ?? "";
                cfg.Token = (key.GetValue("Token") as string) ?? "";
            }
        }
        catch { }
        try
        {
            var f = Path.Combine(cfg.DataDir, "device.json");
            if ((string.IsNullOrEmpty(cfg.DeviceId)) && File.Exists(f))
            {
                var j = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f)).RootElement;
                cfg.ServerUrl = j.TryGetProperty("serverUrl", out var s) ? s.GetString() ?? cfg.ServerUrl : cfg.ServerUrl;
                cfg.DeviceId = j.TryGetProperty("deviceId", out var d) ? d.GetString() ?? "" : cfg.DeviceId;
                cfg.Token = j.TryGetProperty("token", out var t) ? t.GetString() ?? "" : cfg.Token;
            }
        }
        catch { }
        return cfg;
    }

    static async Task<int> RunServiceAsync(AgentConfig cfg)
    {
        using var agent = new Agent(cfg);
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var lastPoll = DateTime.MinValue;
        var lastBeat = DateTime.MinValue;
        var lastMinute = DateTime.Now;
        var wasLocked = false;
        Console.WriteLine($"ScreenTime service: {cfg.DeviceId} @ {cfg.ServerUrl}");

        while (!cts.IsCancellationRequested)
        {
            try
            {
                agent.TickDayRollover();
                // active-only counting
                var idle = Native.IdleTime();
                var idleThreshold = TimeSpan.FromSeconds(Math.Max(30, agent.Policy.IdleThresholdSec));
                if (DateTime.Now - lastMinute >= TimeSpan.FromMinutes(1))
                {
                    lastMinute = DateTime.Now;
                    if (!agent.Policy.CountOnlyActive || idle < idleThreshold) agent.AddActiveMinute();
                }

                var decision = PolicyEvaluator.Evaluate(agent.Policy, DateTime.Now, agent.ActiveMinToday);
                var pollInterval = decision.State == State.Warning ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(30);
                if (DateTime.Now - lastPoll >= pollInterval)
                {
                    lastPoll = DateTime.Now;
                    try { await agent.PollConfigAsync(cts.Token); }
                    catch (Exception ex) { Console.WriteLine("poll failed (offline cache): " + ex.Message); }
                    decision = PolicyEvaluator.Evaluate(agent.Policy, DateTime.Now, agent.ActiveMinToday);
                }

                if (decision.State == State.Locked)
                {
                    Native.LockWorkStation();
                    if (!wasLocked) Console.WriteLine($"LOCKED ({decision.Reason}): {decision.Message}");
                    wasLocked = true;
                    await Task.Delay(TimeSpan.FromSeconds(5), cts.Token); // re-lock loop
                }
                else
                {
                    wasLocked = false;
                    await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                }

                if (DateTime.Now - lastBeat >= TimeSpan.FromMinutes(1))
                {
                    lastBeat = DateTime.Now;
                    try { await agent.SendHeartbeatAsync(decision.State == State.Locked, cts.Token); }
                    catch (Exception ex) { Console.WriteLine("heartbeat failed: " + ex.Message); }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine("loop error: " + ex.Message); await Task.Delay(5000); }
        }
        return 0;
    }

    static int RunTray(AgentConfig cfg)
    {
        ApplicationConfiguration.Initialize();
        using var agent = new Agent(cfg);
        var icon = new NotifyIcon
        {
            Visible = true,
            Text = "ScreenTime",
            Icon = SystemIcons.Shield,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Pokaż czas", null, (_, _) =>
            MessageBox.Show($"Dziś aktywnie: {agent.ActiveMinToday} min", "ScreenTime"));
        icon.ContextMenuStrip = menu;

        var timer = new System.Windows.Forms.Timer { Interval = 15000 };
        Form? overlay = null;
        timer.Tick += async (_, _) =>
        {
            try
            {
                agent.TickDayRollover();
                try { await agent.PollConfigAsync(CancellationToken.None); } catch { }
                var d = PolicyEvaluator.Evaluate(agent.Policy, DateTime.Now, agent.ActiveMinToday);
                icon.Text = d.State switch
                {
                    State.Locked => "ScreenTime: ZABLOKOWANE",
                    State.Warning => $"ScreenTime: zostało {d.RemainingMin} min",
                    _ => d.RemainingMin == int.MaxValue ? "ScreenTime: okno czasowe" : $"ScreenTime: zostało {d.RemainingMin} min",
                };
                if (d.RemainingMin is 15 or 5 or 1)
                    icon.ShowBalloonTip(10000, "ScreenTime", d.Message, ToolTipIcon.Warning);
                if (d.State == State.Locked && overlay is null)
                {
                    overlay = new Form
                    {
                        Text = "ScreenTime – blokada",
                        FormBorderStyle = FormBorderStyle.None,
                        WindowState = FormWindowState.Maximized,
                        TopMost = true,
                        BackColor = System.Drawing.Color.DarkRed,
                    };
                    var label = new Label
                    {
                        Dock = DockStyle.Fill,
                        TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                        ForeColor = System.Drawing.Color.White,
                        Font = new System.Drawing.Font("Segoe UI", 20),
                        Text = $"Komputer zablokowany\n{d.Message}\nZapytaj rodzica.",
                    };
                    overlay.Controls.Add(label);
                    overlay.Show();
                }
                else if (d.State != State.Locked && overlay is not null)
                {
                    overlay.Close(); overlay = null;
                }
            }
            catch { }
        };
        timer.Start();
        Application.Run();
        return 0;
    }
}
