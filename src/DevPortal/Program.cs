using DevPortal.Desktop;
using DevPortal.Server;
using DevPortal.Settings;
using DevPortal.Tray;
using DevPortal.Updates;
using Velopack;

namespace DevPortal;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        AppLog.Enable(AppLog.DefaultPath);
        var restartedByUpdate = false;
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => new Autostart().Enable(Autostart.LaunchPath(Environment.ProcessPath!)))
            .OnBeforeUninstallFastCallback(_ => new Autostart().Disable())
            .OnRestarted(_ => restartedByUpdate = true)
            .Run();

        using var instance = SingleInstance.TryAcquire(restartedByUpdate ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        if (instance is null)
        {
            SingleInstance.ActivateExisting();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => AppLog.Error("Unhandled UI exception", e.Exception);

        var settings = new SettingsStore(SettingsStore.DefaultPath);
        settings.Load();
        var server = new PortalServer(settings);

        using var context = CreateTrayOnUiThread(server, settings);
        instance.OnActivated(context.OpenWhenActivated);
        Application.Run(context);
        Task.Run(() => server.DisposeAsync().AsTask()).GetAwaiter().GetResult();
    }

    private static TrayApp CreateTrayOnUiThread(PortalServer server, SettingsStore settings)
    {
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        return new TrayApp(server, settings, new UpdateService(), new Autostart());
    }
}
