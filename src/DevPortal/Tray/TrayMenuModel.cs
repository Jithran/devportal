using DevPortal.Server;

namespace DevPortal.Tray;

public enum TrayCommand
{
    OpenPortal,
    StartServer,
    RestartServer,
    CheckForUpdates,
    InstallUpdate,
    ReleaseNotes,
    Settings,
    ToggleAutostart,
    About,
    Quit,
}

public sealed record TrayState(
    string Version,
    ServerStatus Server,
    int Port,
    string? ServerError,
    InstanceSummary? Summary,
    string? AvailableUpdate,
    bool Autostart,
    bool UpdateBusy = false);

public sealed record TrayMenuItem(string Text, TrayCommand? Command = null, bool Enabled = true, bool Checked = false, bool IsDefault = false)
{
    public static readonly TrayMenuItem Separator = new("-");

    public bool IsSeparator => Text == "-";
}

/// <summary>Turns tray state into menu items and a tooltip. No WinForms here, so it is unit-tested directly.</summary>
public static class TrayMenuModel
{
    private const int MaxTooltipLength = 127;

    public static IReadOnlyList<TrayMenuItem> Build(TrayState state)
    {
        var running = state.Server == ServerStatus.Running;
        var items = new List<TrayMenuItem>
        {
            new($"DevPortal v{state.Version}", Enabled: false),
            new(StatusLine(state), Enabled: false),
        };
        if (running && state.Summary is { } summary)
            items.Add(new(SummaryLine(summary), Enabled: false));

        items.Add(TrayMenuItem.Separator);
        items.Add(new("Open portal", TrayCommand.OpenPortal, Enabled: running, IsDefault: true));
        items.Add(state.Server switch
        {
            ServerStatus.Running => new("Restart server", TrayCommand.RestartServer),
            ServerStatus.Starting => new("Starting…", Enabled: false),
            _ => new("Start server", TrayCommand.StartServer),
        });

        items.Add(TrayMenuItem.Separator);
        items.Add(state.AvailableUpdate is { } version
            ? new($"Install update v{version} and restart…", TrayCommand.InstallUpdate, Enabled: !state.UpdateBusy)
            : new("Check for updates…", TrayCommand.CheckForUpdates, Enabled: !state.UpdateBusy));
        items.Add(new("Release notes", TrayCommand.ReleaseNotes));
        items.Add(new("Settings…", TrayCommand.Settings));
        items.Add(new("Start with Windows", TrayCommand.ToggleAutostart, Checked: state.Autostart));
        items.Add(new("About DevPortal", TrayCommand.About));

        items.Add(TrayMenuItem.Separator);
        items.Add(new("Quit DevPortal", TrayCommand.Quit));
        return items;
    }

    public static string Tooltip(TrayState state)
    {
        var text = state.Server switch
        {
            ServerStatus.Running when state.Summary is { } summary => $"DevPortal on :{state.Port} · {SummaryLine(summary)}",
            ServerStatus.Running => $"DevPortal on :{state.Port}",
            ServerStatus.Starting => "DevPortal is starting",
            _ => $"DevPortal stopped{(state.ServerError is null ? "" : $": {state.ServerError}")}",
        };
        if (state.AvailableUpdate is not null)
            text += $" · update v{state.AvailableUpdate}";
        return text.Length <= MaxTooltipLength ? text : text[..(MaxTooltipLength - 1)] + "…";
    }

    private static string StatusLine(TrayState state) => state.Server switch
    {
        ServerStatus.Running => $"● Running on localhost:{state.Port}",
        ServerStatus.Starting => $"◌ Starting on localhost:{state.Port}",
        _ => $"○ Stopped{(state.ServerError is null ? "" : $": {state.ServerError}")}",
    };

    private static string SummaryLine(InstanceSummary summary) =>
        $"{summary.Instances} {(summary.Instances == 1 ? "instance" : "instances")} · {summary.Projects} {(summary.Projects == 1 ? "project" : "projects")}";
}
