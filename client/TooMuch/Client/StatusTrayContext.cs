using System.Drawing;
using TooMuch.Core;

namespace TooMuch.Client;

internal sealed class StatusTrayContext : ApplicationContext
{
    private readonly Icon trayIcon;
    private readonly NotifyIcon icon;
    private readonly StatusWindow window = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private bool polling;

    public StatusTrayContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show remaining time", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            ?? (Icon)SystemIcons.Application.Clone();
        icon = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "TooMuch: Connecting to service...",
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.DoubleClick += (_, _) => ShowWindow();
        timer.Tick += async (_, _) => await RefreshStatus();
        timer.Start();
    }

    private void ShowWindow()
    {
        window.Show();
        window.Activate();
    }

    private async Task RefreshStatus()
    {
        if (polling) return;
        polling = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var status = await StatusPipe.Read(timeout.Token);
            if (status is null) throw new InvalidDataException("The service has not published a status yet.");
            window.UpdateStatus(status);
            icon.Text = Clip("TooMuch: " + window.TooltipText);
        }
        catch
        {
            window.ShowUnavailable();
            icon.Text = "TooMuch: Service unavailable";
        }
        finally { polling = false; }
    }

    private static string Clip(string text) => text.Length <= 63 ? text : text[..60] + "...";

    protected override void ExitThreadCore()
    {
        timer.Stop();
        timer.Dispose();
        icon.Visible = false;
        icon.Dispose();
        trayIcon.Dispose();
        window.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class StatusWindow : Form
{
    private readonly Label state = new();
    private readonly Label remaining = new();
    private readonly Label used = new();
    private readonly Label detail = new();
    private readonly Label updated = new();

    public string TooltipText { get; private set; } = "Connecting...";

    public StatusWindow()
    {
        Text = "TooMuch — Remaining time";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(340, 190);
        BackColor = Color.FromArgb(28, 28, 28);
        ForeColor = Color.White;

        state.SetBounds(20, 16, 300, 26);
        state.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        remaining.SetBounds(20, 48, 300, 38);
        remaining.Font = new Font("Segoe UI", 22, FontStyle.Bold);
        used.SetBounds(20, 96, 300, 22);
        detail.SetBounds(20, 121, 300, 22);
        updated.SetBounds(20, 155, 300, 18);
        updated.ForeColor = Color.Silver;
        updated.Font = new Font("Segoe UI", 8);
        Controls.AddRange(new Control[] { state, remaining, used, detail, updated });
    }

    public void UpdateStatus(ChildStatus status)
    {
        state.Text = status.State == "Blocked" ? status.Reason : status.Mode;
        state.ForeColor = status.State == "Blocked" ? Color.OrangeRed : Color.LightGreen;
        remaining.Text = status.State == "Blocked" ? "Access blocked" : FormatDuration(status.RemainingSeconds ?? 0) + " left";
        used.Text = "Used today: " + FormatDuration(status.UsedSeconds);
        detail.Text = status.AccessUntil is null ? status.Reason : "Available until " + status.AccessUntil;
        updated.Text = "Updated " + status.UpdatedAtUtc.ToLocalTime().ToString("HH:mm:ss");
        TooltipText = status.State == "Blocked" ? "Access blocked" : FormatDuration(status.RemainingSeconds ?? 0) + " left";
    }

    public void ShowUnavailable()
    {
        state.Text = "Service unavailable";
        state.ForeColor = Color.OrangeRed;
        remaining.Text = "Time status unavailable";
        used.Text = "";
        detail.Text = "The service may be offline. Ask a parent to check this PC.";
        updated.Text = "";
        TooltipText = "Service unavailable";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    private static string FormatDuration(double seconds)
    {
        var minutes = Math.Max(0, (int)Math.Ceiling(seconds / 60));
        var hours = minutes / 60;
        var rest = minutes % 60;
        if (hours == 0) return $"{rest}m";
        return rest == 0 ? $"{hours}h" : $"{hours}h {rest}m";
    }
}
