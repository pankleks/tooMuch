using Microsoft.Win32;
using TooMuch.Client;
using TooMuch.Core;
using TooMuch.Win32;

namespace TooMuch;

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
            Console.Error.WriteLine("Missing DeviceId/Token. Run install.ps1 as administrator.");
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
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\TooMuch");
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
        var lastWatchdog = DateTime.MinValue;
        var wasLocked = false;
        Console.WriteLine($"tooMuch service: {cfg.DeviceId} @ {cfg.ServerUrl}");

        while (!cts.IsCancellationRequested)
        {
            try
            {
                agent.TickDayRollover();
                // NOTE: no active-minute counting here. GetLastInputInfo in
                // session 0 never sees user input, so with count_only_active
                // nothing would ever accumulate. Counting lives in the tray
                // (user session); usage is reloaded from disk for heartbeat.

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
                    agent.ReloadUsage(); // counted by the tray in user sessions
                    try { await agent.SendHeartbeatAsync(decision.State == State.Locked, cts.Token); }
                    catch (Exception ex) { Console.WriteLine("heartbeat failed: " + ex.Message); }
                }

                // child can kill their own tray: respawn it in active sessions
                if (DateTime.Now - lastWatchdog >= TimeSpan.FromSeconds(15))
                {
                    lastWatchdog = DateTime.Now;
                    try
                    {
                        var exe = Environment.ProcessPath;
                        if (!string.IsNullOrEmpty(exe)) SessionGuard.EnsureTray(exe);
                    }
                    catch { }
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
            Text = "tooMuch",
            Icon = SystemIcons.Shield,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show time", null, (_, _) =>
            MessageBox.Show($"Active today: {agent.ActiveMinToday} min", "tooMuch"));
        icon.ContextMenuStrip = menu;

        var timer = new System.Windows.Forms.Timer { Interval = 15000 };
        var overlays = new List<Form>(); // one fullscreen overlay per monitor
        var lockedNow = false; // Alt+F4 guard below: user close cancelled while locked
        var lastMinute = DateTime.Now;
        timer.Tick += async (_, _) =>
        {
            try
            {
                agent.TickDayRollover();
                // active-minute counting lives here: this process runs in the
                // user session, where GetLastInputInfo reflects real activity
                // (the SYSTEM loop in session 0 would see infinite idle).
                if (DateTime.Now - lastMinute >= TimeSpan.FromMinutes(1))
                {
                    lastMinute = DateTime.Now;
                    var idle = Native.IdleTime();
                    var idleThreshold = TimeSpan.FromSeconds(Math.Max(30, agent.Policy.IdleThresholdSec));
                    if (!agent.Policy.CountOnlyActive || idle < idleThreshold) agent.AddActiveMinute();
                }
                try { await agent.PollConfigAsync(CancellationToken.None); } catch { }
                var d = PolicyEvaluator.Evaluate(agent.Policy, DateTime.Now, agent.ActiveMinToday);
                lockedNow = d.State == State.Locked;
                icon.Text = d.State switch
                {
                    State.Locked => "tooMuch: LOCKED",
                    State.Warning => $"tooMuch: {d.RemainingMin} min left",
                    _ => d.RemainingMin == int.MaxValue ? "tooMuch: within allowed hours" : $"tooMuch: {d.RemainingMin} min left",
                };
                if (d.RemainingMin is 15 or 5 or 1)
                    icon.ShowBalloonTip(10000, "tooMuch", d.Message, ToolTipIcon.Warning);
                if (d.State == State.Locked && overlays.Count != Screen.AllScreens.Length)
                {
                    // (re)build when locked: covers every monitor, incl. hot-plugged ones.
                    // Dispose bypasses the FormClosing guard (only Close() raises it).
                    foreach (var o in overlays) { try { o.Dispose(); } catch { } }
                    overlays.Clear();
                    foreach (var screen in Screen.AllScreens)
                    {
                        var overlay = new Form
                        {
                            Text = "tooMuch – locked",
                            FormBorderStyle = FormBorderStyle.None,
                            StartPosition = FormStartPosition.Manual,
                            Location = screen.Bounds.Location,
                            Size = screen.Bounds.Size,
                            TopMost = true,
                            ShowInTaskbar = false,
                            BackColor = System.Drawing.Color.DarkRed,
                        };
                        var label = new Label
                        {
                            Dock = DockStyle.Fill,
                            TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                            ForeColor = System.Drawing.Color.White,
                            Font = new System.Drawing.Font("Segoe UI", 20),
                            Text = $"Computer locked\n{d.Message}\nAsk your parent.",
                        };
                        overlay.Controls.Add(label);
                        overlay.FormClosing += (_, e) => { if (lockedNow) e.Cancel = true; };
                        overlay.Show();
                        overlays.Add(overlay);
                    }
                }
                else if (d.State != State.Locked && overlays.Count > 0)
                {
                    foreach (var o in overlays) { try { o.Close(); } catch { } o.Dispose(); }
                    overlays.Clear();
                }
            }
            catch { }
        };
        timer.Start();
        Application.Run();
        return 0;
    }
}
