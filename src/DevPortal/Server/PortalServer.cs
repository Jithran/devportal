using System.Net;
using System.Text.Json.Serialization;
using DevPortal.Discovery;
using DevPortal.Preview;
using DevPortal.Security;
using DevPortal.Settings;

namespace DevPortal.Server;

public enum ServerStatus { Stopped, Starting, Running }

public sealed record InstanceSummary(int Instances, int Projects)
{
    public static InstanceSummary From(Snapshot snapshot) => new(
        snapshot.Projects.Sum(p => p.Versions.Sum(v => v.Instances.Count)),
        snapshot.Projects.Count(p => p.Kind is "repo" or "compose"));
}

/// <summary>
/// Hosts the read-only web portal inside the tray process. Start, stop and restart are in-process calls from the tray;
/// none of them is reachable over HTTP.
/// </summary>
public sealed class PortalServer(SettingsStore settings) : IAsyncDisposable
{
    internal static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim gate = new(1, 1);
    private WebApplication? app;

    public event Action? StateChanged;

    public ServerStatus Status { get; private set; }

    public int Port { get; private set; } = settings.Current.Port;

    public string? Error { get; private set; }

    public string Url => $"http://localhost:{Port}/";

    public Task StartAsync() => Exclusive(StartCoreAsync);

    public Task StopAsync() => Exclusive(StopCoreAsync);

    public Task RestartAsync() => Exclusive(async () =>
    {
        await StopCoreAsync();
        await StartCoreAsync();
    });

    public async Task<InstanceSummary?> GetSummaryAsync(CancellationToken cancellationToken)
    {
        if (app is not { } running || Status != ServerStatus.Running)
            return null;
        var snapshot = await running.Services.GetRequiredService<InstanceCatalog>().GetAsync(false, cancellationToken);
        return InstanceSummary.From(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        gate.Dispose();
    }

    private async Task Exclusive(Func<Task> action)
    {
        await gate.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task StartCoreAsync()
    {
        if (app is not null)
            return;

        Port = settings.Current.Port;
        Error = null;
        SetStatus(ServerStatus.Starting);

        var candidate = Build(Port, settings);
        try
        {
            await candidate.StartAsync();
            app = candidate;
            AppLog.Info($"Portal listening on 127.0.0.1:{Port} and [::1]:{Port}");
            SetStatus(ServerStatus.Running);
        }
        catch (Exception ex)
        {
            await candidate.DisposeAsync();
            Error = ex is IOException ? $"port {Port} is in use" : ex.Message;
            AppLog.Error($"Portal failed to start on port {Port}", ex);
            SetStatus(ServerStatus.Stopped);
        }
    }

    private async Task StopCoreAsync()
    {
        if (app is not { } running)
            return;
        app = null;
        await running.StopAsync();
        await running.DisposeAsync();
        AppLog.Info("Portal stopped");
        SetStatus(ServerStatus.Stopped);
    }

    private void SetStatus(ServerStatus status)
    {
        Status = status;
        StateChanged?.Invoke();
    }

    internal static WebApplication Build(int port, SettingsStore settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new AppLogLoggerProvider());

        // The default 30s drain kept the port closed that long before an update or restart whenever a request hung.
        builder.WebHost.UseShutdownTimeout(ShutdownTimeout);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Loopback, port);
            kestrel.Listen(IPAddress.IPv6Loopback, port);
        });

        builder.Services.Configure<PortalOptions>(builder.Configuration.GetSection(PortalOptions.Section));
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<ProcessInspector>();
        builder.Services.AddSingleton<GitResolver>();
        builder.Services.AddSingleton<DockerClient>();
        builder.Services.AddSingleton<MetricsHistory>();
        builder.Services.AddSingleton<ResourceSampler>();
        builder.Services.AddSingleton<HttpProber>();
        builder.Services.AddSingleton<InstanceCatalog>();
        builder.Services.AddSingleton(new PreviewCache(Path.Combine(Path.GetDirectoryName(settings.FilePath)!, "previews"), TimeProvider.System));
        builder.Services.AddHostedService<PreviewService>();

        var app = builder.Build();

        app.UseMiddleware<LocalOnlyGuard>(port);

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/api/instances", async (InstanceCatalog catalog, bool? all, CancellationToken cancellationToken) =>
            Results.Ok(await catalog.GetAsync(all ?? false, cancellationToken)));

        app.MapGet("/api/config", (SettingsStore store) =>
        {
            var current = store.Current;
            return Results.Ok(new PortalConfig(AppInfo.Version, AppInfo.RepositoryUrl, current.RefreshSeconds, current.ShowSystemByDefault, current.Theme, current.ScreenshotsEnabled));
        });

        // Serves only what the background loop already captured, and only for a port in the last scan: never a capture on request.
        app.MapGet("/api/preview/{port:int}", (int port, HttpResponse response, SettingsStore store, InstanceCatalog catalog, PreviewCache cache) =>
            Preview(port, response, PreviewService.Screenshot, "image/png", Previewable(store, catalog), cache));
        app.MapGet("/api/preview/{port:int}/icon", (int port, HttpResponse response, SettingsStore store, InstanceCatalog catalog, PreviewCache cache) =>
            Preview(port, response, PreviewService.Icon, "image/x-icon", Previewable(store, catalog), cache));

        app.MapGet("/health", () => Results.Ok(new { status = "ok", version = AppInfo.Version }));

        return app;
    }

    private static Snapshot? Previewable(SettingsStore store, InstanceCatalog catalog) => store.Current.ScreenshotsEnabled ? catalog.Latest : null;

    /// <param name="snapshot">The last scan, or null while previews are off.</param>
    internal static IResult Preview(int port, HttpResponse response, string kind, string contentType, Snapshot? snapshot, PreviewCache cache)
    {
        if (snapshot is null)
            return Results.NotFound();
        var target = PreviewService.Targets(snapshot).FirstOrDefault(t => t.Port == port);
        if (target is null || cache.Find(target.Key, kind) is not { } path)
            return Results.NotFound();
        try
        {
            var bytes = File.ReadAllBytes(path);
            // The page asks again with a new query each minute; in between the same image comes from the browser cache.
            response.Headers.CacheControl = "private, max-age=60";
            return Results.Bytes(bytes, contentType);
        }
        catch (IOException)
        {
            return Results.NotFound();
        }
    }

    private sealed record PortalConfig(string Version, string RepositoryUrl, int RefreshSeconds, bool ShowSystemByDefault, ThemePreference Theme, bool ScreenshotsEnabled);
}
