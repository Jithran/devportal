using System.Net;
using System.Net.Sockets;
using DevPortal.Server;
using DevPortal.Settings;

namespace DevPortal.Tests;

public sealed class PortalServerTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("devportal-server-").FullName;

    [Fact]
    public async Task Restart_after_a_port_change_moves_the_portal_to_the_new_port()
    {
        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        settings.Save(new UserSettings { Port = FreePort() });
        await using var server = new PortalServer(settings);
        await server.StartAsync();
        var oldPort = server.Port;

        settings.Save(settings.Current with { Port = FreePort() });
        await server.RestartAsync();

        using var http = new HttpClient();
        var health = await http.GetAsync($"http://localhost:{server.Port}/health");
        Assert.Equal(ServerStatus.Running, server.Status);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://localhost:{oldPort}/health"));
    }

    [Fact]
    public async Task Taken_port_leaves_the_server_stopped_with_the_reason()
    {
        using var occupant = new TcpListener(IPAddress.Loopback, 0);
        occupant.Start();
        var port = ((IPEndPoint)occupant.LocalEndpoint).Port;
        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        settings.Save(new UserSettings { Port = port });
        await using var server = new PortalServer(settings);

        await server.StartAsync();

        Assert.Equal(ServerStatus.Stopped, server.Status);
        Assert.Equal($"port {port} is in use", server.Error);
    }

    [Fact]
    public async Task Config_endpoint_is_read_only_and_guarded_like_the_api()
    {
        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        settings.Save(new UserSettings { Port = FreePort(), RefreshSeconds = 9, Theme = ThemePreference.Light });
        await using var server = new PortalServer(settings);
        await server.StartAsync();
        using var http = new HttpClient();
        var url = $"http://localhost:{server.Port}/api/config";

        var config = await http.GetStringAsync(url);
        var post = await http.PostAsync(url, null);
        using var crossSite = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { { "Origin", "https://evil.example" } } };
        var crossSiteResponse = await http.SendAsync(crossSite);

        Assert.Contains("\"refreshSeconds\":9", config);
        Assert.Contains("\"theme\":\"light\"", config);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossSiteResponse.StatusCode);
    }

    [Fact]
    public async Task Stop_does_not_wait_long_for_a_hanging_request()
    {
        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        settings.Save(new UserSettings { Port = FreePort() });
        await using var server = new PortalServer(settings);
        await server.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await client.GetStream().WriteAsync("GET /health HTTP/1.1\r\nHost: local"u8.ToArray());
        await Task.Delay(200);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await server.StopAsync();

        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, PortalServer.ShutdownTimeout + TimeSpan.FromSeconds(3));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
