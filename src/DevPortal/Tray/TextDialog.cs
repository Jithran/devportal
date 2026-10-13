using System.Diagnostics;
using System.Drawing;

namespace DevPortal.Tray;

/// <summary>Read-only text with an optional link and an optional accept button: about, release notes and the update prompt.</summary>
public sealed class TextDialog : Form
{
    public TextDialog(string title, string heading, string body, string? linkText = null, string? linkUrl = null, string? acceptText = null, Icon? icon = null)
    {
        Text = title;
        Icon = icon;
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(560, 420);
        MinimumSize = new Size(400, 300);
        Padding = new Padding(12);

        var headingLabel = new Label
        {
            Text = heading,
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 11f, FontStyle.Bold),
            Padding = new Padding(0, 0, 0, 8),
        };

        var bodyBox = new TextBox
        {
            Text = body.ReplaceLineEndings("\r\n"),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9.5f),
            TabStop = false,
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 10, 0, 0),
        };

        if (acceptText is not null)
        {
            var accept = new Button { Text = acceptText, DialogResult = DialogResult.OK, AutoSize = true };
            buttons.Controls.Add(accept);
            AcceptButton = accept;
        }
        var close = new Button { Text = acceptText is null ? "Close" : "Later", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(close);
        CancelButton = close;
        if (acceptText is null)
            AcceptButton = close;

        if (linkText is not null && linkUrl is not null)
        {
            var link = new LinkLabel { Text = linkText, AutoSize = true, Margin = new Padding(0, 8, 16, 0) };
            link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(linkUrl) { UseShellExecute = true });
            buttons.Controls.Add(link);
        }

        Controls.Add(bodyBox);
        Controls.Add(buttons);
        Controls.Add(headingLabel);
    }
}
