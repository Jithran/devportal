namespace DevPortal.Tray;

public interface ITrayActions
{
    void OpenPortal();
    Task StartServerAsync();
    Task RestartServerAsync();
    Task CheckForUpdatesAsync();
    Task InstallUpdateAsync();
    void ShowReleaseNotes();
    Task ShowSettingsAsync();
    void ToggleAutostart();
    void ShowAbout();
    Task QuitAsync();
}

public static class TrayCommands
{
    public static Task ExecuteAsync(TrayCommand command, ITrayActions actions)
    {
        switch (command)
        {
            case TrayCommand.OpenPortal: actions.OpenPortal(); break;
            case TrayCommand.StartServer: return actions.StartServerAsync();
            case TrayCommand.RestartServer: return actions.RestartServerAsync();
            case TrayCommand.CheckForUpdates: return actions.CheckForUpdatesAsync();
            case TrayCommand.InstallUpdate: return actions.InstallUpdateAsync();
            case TrayCommand.ReleaseNotes: actions.ShowReleaseNotes(); break;
            case TrayCommand.Settings: return actions.ShowSettingsAsync();
            case TrayCommand.ToggleAutostart: actions.ToggleAutostart(); break;
            case TrayCommand.About: actions.ShowAbout(); break;
            case TrayCommand.Quit: return actions.QuitAsync();
            default: throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }
        return Task.CompletedTask;
    }
}
