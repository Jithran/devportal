using System.Drawing;
using DevPortal.Settings;

namespace DevPortal.Tray;

/// <summary>
/// Native settings dialog. Settings are deliberately not editable through the web portal, so the HTTP surface stays read-only.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly int boundPort;
    private readonly IPortAvailability ports;
    private readonly NumericUpDown port = Number(UserSettings.MinPort, UserSettings.MaxPort);
    private readonly NumericUpDown refresh = Number(UserSettings.MinRefreshSeconds, UserSettings.MaxRefreshSeconds);
    private readonly CheckBox showSystem = new() { Text = "Show system listeners by default", AutoSize = true };
    private readonly ComboBox theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly CheckBox checkUpdates = new() { Text = "Check for updates automatically", AutoSize = true };
    private readonly Label errors = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(420, 0) };

    /// <param name="boundPort">The port the portal holds now, or -1 when it is stopped.</param>
    public SettingsForm(UserSettings current, int boundPort, IPortAvailability ports, Icon? icon = null)
    {
        this.boundPort = boundPort;
        this.ports = ports;
        Result = current;

        Text = "DevPortal settings";
        Icon = icon;
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        port.Value = current.Port;
        refresh.Value = current.RefreshSeconds;
        showSystem.Checked = current.ShowSystemByDefault;
        theme.Items.AddRange(Enum.GetNames<ThemePreference>());
        theme.SelectedItem = current.Theme.ToString();
        checkUpdates.Checked = current.CheckForUpdates;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(grid, "Port", port, "Listens on 127.0.0.1 and ::1 only. This cannot be changed.");
        AddRow(grid, "Refresh every", refresh, "seconds");
        AddRow(grid, "Theme", theme, "Default for the page; the page's Theme button still overrides it per browser.");
        grid.Controls.Add(showSystem, 1, grid.RowCount++);
        grid.Controls.Add(checkUpdates, 1, grid.RowCount++);
        grid.Controls.Add(errors, 0, grid.RowCount);
        grid.SetColumnSpan(errors, 2);
        grid.RowCount++;

        var save = new Button { Text = "Save", AutoSize = true };
        save.Click += (_, _) => Save();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        AcceptButton = save;
        CancelButton = cancel;

        Controls.Add(grid);
        Controls.Add(buttons);
    }

    public UserSettings Result { get; private set; }

    private void Save()
    {
        var proposed = new UserSettings
        {
            Port = (int)port.Value,
            RefreshSeconds = (int)refresh.Value,
            ShowSystemByDefault = showSystem.Checked,
            Theme = Enum.Parse<ThemePreference>((string)theme.SelectedItem!),
            CheckForUpdates = checkUpdates.Checked,
        };
        var problems = SettingsValidator.Validate(proposed, boundPort, ports);
        if (problems.Count > 0)
        {
            errors.Text = string.Join(Environment.NewLine, problems);
            return;
        }
        Result = proposed;
        DialogResult = DialogResult.OK;
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control input, string hint)
    {
        var row = grid.RowCount++;
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 12, 3) }, 0, row);
        var cell = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
        cell.Controls.Add(input);
        cell.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(300, 0), Margin = new Padding(8, 6, 3, 3) });
        grid.Controls.Add(cell, 1, row);
    }

    private static NumericUpDown Number(int min, int max) => new() { Minimum = min, Maximum = max, Width = 80 };
}
