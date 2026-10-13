using System.Diagnostics;
using System.Drawing;
using DevPortal.Desktop;
using DevPortal.Server;
using DevPortal.Settings;
using DevPortal.Updates;

namespace DevPortal.Tray;

public sealed class TrayApp : ApplicationContext, ITrayActions
{
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(6);

    private readonly PortalServer server;
    private readonly SettingsStore settings;
    private readonly UpdateService updates;
    private readonly Autostart autostart;
    private readonly ContextMenuStrip menu = new();
    private readonly SynchronizationContext ui;
    private readonly Icon runningIcon = LoadIcon("devportal.ico");
    private readonly Icon stoppedIcon = LoadIcon("devportal-stopped.ico");
    private readonly NotifyIcon notifyIcon;
    private readonly System.Windows.Forms.Timer summaryTimer = new() { Interval = (int)SummaryInterval.TotalMilliseconds };
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = (int)UpdateInterval.TotalMilliseconds };

    private InstanceSummary? summary;
    private AvailableUpdate? availableUpdate;
    private bool updateBusy;
    private Form? openDialog;

    public TrayApp(PortalServer server, SettingsStore settings, UpdateService updates, Autostart autostart)
    {
        this.server = server;
        this.settings = settings;
        this.updates = updates;
        this.autostart = autostart;
        ui = SynchronizationContext.Current ?? throw new InvalidOperationException("TrayApp must be created on the UI thread.");

        notifyIcon = new NotifyIcon { Icon = stoppedIcon, Text = AppInfo.Name, ContextMenuStrip = menu };
        notifyIcon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenPortal(); };
        notifyIcon.BalloonTipClicked += async (_, _) => { if (availableUpdate is not null) await Run(TrayCommand.InstallUpdate); };
        menu.Opening += (_, _) => RebuildMenu();
        server.StateChanged += () => ui.Post(_ => OnServerStateChanged(), null);
        summaryTimer.Tick += async (_, _) => await RefreshSummaryAsync();
        updateTimer.Tick += async (_, _) => await BackgroundUpdateCheckAsync();

        RebuildMenu();
        notifyIcon.Visible = true;
        AppLog.Info($"Tray icon shown (v{AppInfo.Version}, pid {Environment.ProcessId}, session {Process.GetCurrentProcess().SessionId})");

        ui.Post(async _ => await StartAsync(), null);
    }

    public void OpenWhenActivated() => ui.Post(_ => OpenPortal(), null);

    private TrayState State => new(
        AppInfo.Version, server.Status, server.Port, server.Error, summary, availableUpdate?.Version, autostart.IsEnabled, updateBusy);

    private async Task StartAsync()
    {
        await server.StartAsync();
        summaryTimer.Start();
        updateTimer.Start();
        await RefreshSummaryAsync();
        await BackgroundUpdateCheckAsync();
    }

    public void OpenPortal()
    {
        if (server.Status != ServerStatus.Running)
            return;
        Process.Start(new ProcessStartInfo(server.Url) { UseShellExecute = true });
    }

    public Task StartServerAsync() => RestartServerAsync();

    public async Task RestartServerAsync()
    {
        summary = null;
        await server.RestartAsync();
        await RefreshSummaryAsync();
    }

    public async Task CheckForUpdatesAsync()
    {
        var result = await CheckAsync();
        switch (result.Outcome)
        {
            case UpdateCheckOutcome.UpToDate:
                MessageBox.Show($"DevPortal v{AppInfo.Version} is the latest version.", "DevPortal updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
            case UpdateCheckOutcome.Available:
                await InstallUpdateAsync();
                break;
            case UpdateCheckOutcome.NotInstalled:
                MessageBox.Show($"This copy of DevPortal was not installed by the DevPortal installer, so it cannot update itself.\n\nReleases: {AppInfo.RepositoryUrl}/releases",
                    "DevPortal updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
            default:
                MessageBox.Show($"Could not check for updates.\n\n{result.Error}", "DevPortal updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
        }
    }

    public async Task InstallUpdateAsync()
    {
        if (availableUpdate is not { } update)
            return;

        var notes = string.IsNullOrWhiteSpace(update.NotesMarkdown) ? "No release notes." : update.NotesMarkdown;
        using (var dialog = new TextDialog("DevPortal update", $"DevPortal v{update.Version} is available (you have v{AppInfo.Version})", notes,
                   "View on GitHub", AppInfo.ReleaseUrl(update.Version), "Install and restart", runningIcon))
        {
            if (ShowModal(dialog) != DialogResult.OK)
                return;
        }

        updateBusy = true;
        try
        {
            notifyIcon.Text = "DevPortal: downloading update…";
            await updates.DownloadAsync(update);
            AppLog.Info($"Applying update v{update.Version}");
            await server.StopAsync();
            notifyIcon.Visible = false;
            updates.ApplyAndRestart(update);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Update to v{update.Version} failed", ex);
            notifyIcon.Visible = true;
            MessageBox.Show($"The update could not be installed.\n\n{ex.Message}", "DevPortal updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (server.Status == ServerStatus.Stopped)
                await server.StartAsync();
        }
        finally
        {
            updateBusy = false;
            UpdateIcon();
        }
    }

    public void ShowReleaseNotes()
    {
        var changelog = Changelog.Read();
        var section = Changelog.Section(changelog, AppInfo.Version);
        using var dialog = new TextDialog("DevPortal release notes", $"What's new in v{AppInfo.Version}",
            section.Length > 0 ? section : changelog, "All releases", $"{AppInfo.RepositoryUrl}/releases", icon: runningIcon);
        ShowModal(dialog);
    }

    public async Task ShowSettingsAsync()
    {
        var before = settings.Current;
        var boundPort = server.Status == ServerStatus.Running ? server.Port : -1;
        using var dialog = new SettingsForm(before, boundPort, new LoopbackPortAvailability(), runningIcon);
        if (ShowModal(dialog) != DialogResult.OK || dialog.Result == before)
            return;

        settings.Save(dialog.Result);
        AppLog.Info($"Settings saved to {settings.FilePath}");
        if (dialog.Result.Port != server.Port || server.Status != ServerStatus.Running)
        {
            await RestartServerAsync();
            if (server.Status != ServerStatus.Running)
                MessageBox.Show($"DevPortal could not start on port {server.Port}: {server.Error}.\n\nChoose another port in Settings.",
                    "DevPortal", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        if (dialog.Result.CheckForUpdates && !before.CheckForUpdates)
            await BackgroundUpdateCheckAsync();
    }

    public void ToggleAutostart()
    {
        if (autostart.IsEnabled)
            autostart.Disable();
        else
            autostart.Enable(Autostart.LaunchPath(Environment.ProcessPath!));
    }

    public void ShowAbout()
    {
        var body = string.Join(Environment.NewLine,
            "Lists every web instance running on this PC, grouped by project and branch.",
            "Listens on localhost only; the web page is read-only.",
            "",
            $"Portal:    {server.Url}",
            $"Installed: {(updates.IsInstalled ? "yes (updates through GitHub releases)" : "no (development build)")}",
            $"Program:   {AppContext.BaseDirectory}",
            $"Settings:  {settings.FilePath}",
            $"Log:       {AppLog.FilePath}");
        using var dialog = new TextDialog("About DevPortal", $"DevPortal v{AppInfo.Version}", body, "GitHub", AppInfo.RepositoryUrl, icon: runningIcon);
        ShowModal(dialog);
    }

    public async Task QuitAsync()
    {
        AppLog.Info("Quit from the tray");
        summaryTimer.Stop();
        updateTimer.Stop();
        notifyIcon.Visible = false;
        await server.StopAsync();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            notifyIcon.Dispose();
            menu.Dispose();
            summaryTimer.Dispose();
            updateTimer.Dispose();
            runningIcon.Dispose();
            stoppedIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task<UpdateCheckResult> CheckAsync()
    {
        updateBusy = true;
        try
        {
            var result = await updates.CheckAsync();
            if (result.Outcome == UpdateCheckOutcome.Available)
                availableUpdate = result.Update;
            else if (result.Outcome == UpdateCheckOutcome.UpToDate)
                availableUpdate = null;
            AppLog.Info($"Update check: {result.Outcome} (current v{AppInfo.Version}{(result.Update is { } u ? $", available v{u.Version}" : "")})");
            return result;
        }
        finally
        {
            updateBusy = false;
            UpdateIcon();
        }
    }

    private async Task BackgroundUpdateCheckAsync()
    {
        if (!settings.Current.CheckForUpdates || updateBusy)
            return;
        var known = availableUpdate?.Version;
        var result = await CheckAsync();
        if (result.Update is { } update && update.Version != known)
            notifyIcon.ShowBalloonTip(10_000, $"DevPortal v{update.Version} is available", "Click to see what's new and install it.", ToolTipIcon.Info);
    }

    private async Task RefreshSummaryAsync()
    {
        try
        {
            summary = await server.GetSummaryAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Instance summary failed", ex);
            summary = null;
        }
        UpdateIcon();
    }

    private void OnServerStateChanged()
    {
        UpdateIcon();
        if (server.Status == ServerStatus.Stopped && server.Error is { } error)
            notifyIcon.ShowBalloonTip(10_000, "DevPortal is stopped", $"Could not start: {error}. Choose another port in Settings.", ToolTipIcon.Warning);
    }

    private void UpdateIcon()
    {
        var state = State;
        notifyIcon.Icon = state.Server == ServerStatus.Running ? runningIcon : stoppedIcon;
        notifyIcon.Text = TrayMenuModel.Tooltip(state);
    }

    private void RebuildMenu()
    {
        menu.Items.Clear();
        foreach (var item in TrayMenuModel.Build(State))
        {
            if (item.IsSeparator)
            {
                menu.Items.Add(new ToolStripSeparator());
                continue;
            }
            var menuItem = new ToolStripMenuItem(item.Text) { Enabled = item.Enabled, Checked = item.Checked };
            if (item.IsDefault)
                menuItem.Font = new Font(menuItem.Font, FontStyle.Bold);
            if (item.Command is { } command)
                menuItem.Click += async (_, _) => await Run(command);
            menu.Items.Add(menuItem);
        }
    }

    private async Task Run(TrayCommand command)
    {
        try
        {
            await TrayCommands.ExecuteAsync(command, this);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Tray command {command} failed", ex);
            MessageBox.Show(ex.Message, "DevPortal", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UpdateIcon();
        }
    }

    private DialogResult ShowModal(Form dialog)
    {
        if (openDialog is not null)
        {
            openDialog.Activate();
            return DialogResult.Cancel;
        }
        openDialog = dialog;
        try
        {
            return dialog.ShowDialog();
        }
        finally
        {
            openDialog = null;
        }
    }

    private static Icon LoadIcon(string name)
    {
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded icon {name} is missing.");
        return new Icon(stream, SystemInformation.SmallIconSize);
    }
}
