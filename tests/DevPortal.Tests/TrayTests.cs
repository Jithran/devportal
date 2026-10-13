using DevPortal.Server;
using DevPortal.Tray;

namespace DevPortal.Tests;

public sealed class TrayTests
{
    private static readonly TrayState Running = new("1.0.0", ServerStatus.Running, 7800, null, new InstanceSummary(14, 4), null, Autostart: true);

    public static IEnumerable<object[]> MenuCases =>
    [
        [Running, new TrayCommand?[]
        {
            null, null, null, null, TrayCommand.OpenPortal, TrayCommand.RestartServer, null, TrayCommand.CheckForUpdates,
            TrayCommand.ReleaseNotes, TrayCommand.Settings, TrayCommand.ToggleAutostart, TrayCommand.About, null, TrayCommand.Quit,
        }],
        [Running with { Server = ServerStatus.Stopped, ServerError = "port 7800 is in use", Summary = null, AvailableUpdate = "1.1.0" }, new TrayCommand?[]
        {
            null, null, null, TrayCommand.OpenPortal, TrayCommand.StartServer, null, TrayCommand.InstallUpdate,
            TrayCommand.ReleaseNotes, TrayCommand.Settings, TrayCommand.ToggleAutostart, TrayCommand.About, null, TrayCommand.Quit,
        }],
    ];

    [Theory]
    [MemberData(nameof(MenuCases))]
    public void Menu_offers_the_actions_that_fit_the_server_and_update_state(object state, TrayCommand?[] expected)
    {
        var items = TrayMenuModel.Build((TrayState)state);

        Assert.Equal(expected, items.Select(i => i.Command));
    }

    [Fact]
    public void Stopped_server_shows_why_and_disables_open_portal()
    {
        var items = TrayMenuModel.Build(Running with { Server = ServerStatus.Stopped, ServerError = "port 7800 is in use", Summary = null });

        Assert.Equal("○ Stopped: port 7800 is in use", items[1].Text);
        Assert.False(items.Single(i => i.Command == TrayCommand.OpenPortal).Enabled);
    }

    [Fact]
    public void Running_menu_shows_status_counts_and_autostart_check()
    {
        var items = TrayMenuModel.Build(Running);

        Assert.Equal(["DevPortal v1.0.0", "● Running on localhost:7800", "14 instances · 4 projects"], items.Take(3).Select(i => i.Text));
        Assert.True(items.Single(i => i.Command == TrayCommand.ToggleAutostart).Checked);
        Assert.True(items.Single(i => i.Command == TrayCommand.OpenPortal).IsDefault);
    }

    [Fact]
    public void Tooltip_never_exceeds_the_notify_icon_limit()
    {
        var tooltip = TrayMenuModel.Tooltip(Running with { Server = ServerStatus.Stopped, ServerError = new string('x', 300) });

        Assert.Equal(127, tooltip.Length);
    }

    public static IEnumerable<object[]> Commands => Enum.GetValues<TrayCommand>().Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task Every_command_runs_its_own_action(object command)
    {
        var actions = new RecordingActions();

        await TrayCommands.ExecuteAsync((TrayCommand)command, actions);

        Assert.Equal([command.ToString()!], actions.Calls);
    }

    private sealed class RecordingActions : ITrayActions
    {
        public List<string> Calls { get; } = [];

        public void OpenPortal() => Calls.Add(nameof(TrayCommand.OpenPortal));
        public Task StartServerAsync() => Record(nameof(TrayCommand.StartServer));
        public Task RestartServerAsync() => Record(nameof(TrayCommand.RestartServer));
        public Task CheckForUpdatesAsync() => Record(nameof(TrayCommand.CheckForUpdates));
        public Task InstallUpdateAsync() => Record(nameof(TrayCommand.InstallUpdate));
        public void ShowReleaseNotes() => Calls.Add(nameof(TrayCommand.ReleaseNotes));
        public Task ShowSettingsAsync() => Record(nameof(TrayCommand.Settings));
        public void ToggleAutostart() => Calls.Add(nameof(TrayCommand.ToggleAutostart));
        public void ShowAbout() => Calls.Add(nameof(TrayCommand.About));
        public Task QuitAsync() => Record(nameof(TrayCommand.Quit));

        private Task Record(string name)
        {
            Calls.Add(name);
            return Task.CompletedTask;
        }
    }
}
