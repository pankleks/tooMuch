using System.Drawing;
using System.Text.Json;

namespace TooMuch.Client;

internal sealed record MessageDialogPayload(string Title, string Text);

internal sealed class MessageDialog : Form
{
    private readonly System.Windows.Forms.Timer expiryTimer;
    private bool confirmed;

    private MessageDialog(MessageDialogPayload payload, uint timeoutSeconds)
    {
        Text = string.IsNullOrWhiteSpace(payload.Title) ? "Message from parent" : payload.Title;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(28, 28, 28);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 11f, FontStyle.Regular);
        ClientSize = new Size(680, 420);
        MinimumSize = new Size(500, 320);
        Padding = new Padding(20);

        var message = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            DetectUrls = false,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            BackColor = BackColor,
            ForeColor = ForeColor,
            Font = new Font("Segoe UI", 18f, FontStyle.Regular, GraphicsUnit.Point),
            Text = payload.Text,
            TabStop = true,
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0),
            BackColor = BackColor,
        };
        var ok = new Button
        {
            Text = "OK",
            Size = new Size(120, 44),
            Margin = Padding.Empty,
            Font = new Font("Segoe UI", 12f, FontStyle.Regular),
            DialogResult = DialogResult.OK,
        };
        ok.Click += (_, _) =>
        {
            confirmed = true;
            Close();
        };
        buttons.Controls.Add(ok);
        Controls.Add(message);
        Controls.Add(buttons);
        AcceptButton = ok;

        Shown += (_, _) =>
        {
            Activate();
            BringToFront();
            message.Select(0, 0);
        };

        expiryTimer = new System.Windows.Forms.Timer { Interval = (int)Math.Clamp((ulong)timeoutSeconds * 1000, 1, int.MaxValue) };
        expiryTimer.Tick += (_, _) => Close();
        Shown += (_, _) => expiryTimer.Start();
        FormClosed += (_, _) => expiryTimer.Dispose();
    }

    public static int Run(string encodedPayload, uint timeoutSeconds)
    {
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedPayload));
            var payload = JsonSerializer.Deserialize<MessageDialogPayload>(json);
            if (payload is null) return 2;
            using var dialog = new MessageDialog(payload, timeoutSeconds);
            Application.Run(dialog);
            return dialog.confirmed ? 1 : 0;
        }
        catch { return 2; }
    }
}
