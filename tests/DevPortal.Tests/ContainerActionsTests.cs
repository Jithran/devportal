using System.Net;
using System.Net.Sockets;
using DevPortal.Discovery;
using DevPortal.Server;
using DevPortal.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DevPortal.Tests;

public sealed class ContainerActionsTests : IDisposable
{
    private const string Id = "0123456789ab";
    private readonly string directory = Directory.CreateTempSubdirectory("devportal-actions-").FullName;
    private readonly List<(string Id, ContainerAction Action)> calls = [];
    private string? dockerError;

    private static Snapshot SnapshotWith(string id) => new(DateTimeOffset.UnixEpoch, null,
    [
        new ProjectGroup("web", "compose", null,
        [
            new VersionGroup("main", null, null, null, null,
            [
                new Instance { Source = InstanceSource.Docker, Kind = ServerKind.Docker, ContainerId = id, ContainerName = "web-1" },
                new Instance { Source = InstanceSource.Host, Kind = ServerKind.Node, Pid = 4 },
            ]),
        ]),
    ]);

    private SettingsStore Settings(bool enabled)
    {
        var store = new SettingsStore(Path.Combine(directory, $"{Guid.NewGuid()}.json"));
        store.Save(new UserSettings { ContainerActionsEnabled = enabled });
        return store;
    }

    private ContainerActions Actions(bool enabled = true, TimeProvider? clock = null) => new(Settings(enabled), () => SnapshotWith(Id),
        (id, action, _) =>
        {
            calls.Add((id, action));
            return Task.FromResult(dockerError);
        }, clock ?? TimeProvider.System);

    private static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;

    private static Task<IResult> Post(ContainerActions actions, string id = Id, string action = "stop", string? proof = null) =>
        actions.HandleAsync(id, action, proof ?? actions.Token, "tester", CancellationToken.None);

    [Fact]
    public async Task Setting_off_is_404_and_never_reaches_docker()
    {
        var actions = Actions(enabled: false);

        Assert.Equal(404, Status(await Post(actions)));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Bad_or_missing_proof_is_403()
    {
        var actions = Actions();

        Assert.Equal(403, Status(await Post(actions, proof: "nope")));
        Assert.Equal(403, Status(await actions.HandleAsync(Id, "stop", null, "tester", CancellationToken.None)));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Container_outside_the_last_scan_is_404()
    {
        var actions = Actions();

        Assert.Equal(404, Status(await Post(actions, id: "ffffffffffff")));
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("kill")]
    [InlineData("exec")]
    public async Task Only_stop_and_restart_exist(string action)
    {
        var actions = Actions();

        Assert.Equal(404, Status(await Post(actions, action: action)));
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("stop", ContainerAction.Stop)]
    [InlineData("restart", ContainerAction.Restart)]
    public async Task Known_container_is_acted_on(string action, ContainerAction expected)
    {
        var result = await Post(Actions(), action: action);

        Assert.Equal(204, Status(result));
        Assert.Equal([(Id, expected)], calls);
    }

    [Fact]
    public async Task Docker_failure_is_502()
    {
        dockerError = "Docker answered 500";

        Assert.Equal(502, Status(await Post(Actions())));
    }

    [Fact]
    public async Task Rate_limit_allows_a_burst_then_429_until_the_window_passes()
    {
        var clock = new ManualClock();
        var actions = Actions(clock: clock);

        for (var i = 0; i < ContainerActions.MaxActions; i++)
            Assert.Equal(204, Status(await Post(actions)));
        Assert.Equal(429, Status(await Post(actions)));

        clock.Now += ContainerActions.Window + TimeSpan.FromSeconds(1);
        Assert.Equal(204, Status(await Post(actions)));
    }

    [Fact]
    public async Task Http_guard_rejects_foreign_origins_and_a_disabled_setting()
    {
        var store = Settings(enabled: false);
        store.Save(store.Current with { Port = FreePort() });
        await using var server = new PortalServer(store);
        await server.StartAsync();
        using var http = new HttpClient();
        var url = $"http://localhost:{server.Port}/api/containers/{Id}/stop";
        var own = $"http://localhost:{server.Port}";

        Assert.Equal(HttpStatusCode.NotFound, (await Send(http, url, own, "x")).StatusCode);

        store.Save(store.Current with { ContainerActionsEnabled = true });
        var proof = ExtractProof(await http.GetStringAsync($"http://localhost:{server.Port}/api/config"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(http, url, "https://evil.example", proof)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(http, url, null, proof)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(http, url, own, "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(http, url, own, proof)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.PostAsync($"http://localhost:{server.Port}/api/instances", null)).StatusCode);
    }

    [Fact]
    public async Task Config_withholds_the_proof_while_actions_are_off()
    {
        var store = Settings(enabled: false);
        store.Save(store.Current with { Port = FreePort() });
        await using var server = new PortalServer(store);
        await server.StartAsync();
        using var http = new HttpClient();

        var config = await http.GetStringAsync($"http://localhost:{server.Port}/api/config");

        Assert.Contains("\"containerActionsEnabled\":false", config);
        Assert.DoesNotContain("actionToken", config);
    }

    private static Task<HttpResponseMessage> Send(HttpClient http, string url, string? origin, string proof)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Headers = { { "X-DevPortal-Token", proof } } };
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        return http.SendAsync(request);
    }

    private static string ExtractProof(string config) => System.Text.Json.JsonDocument.Parse(config).RootElement.GetProperty("actionToken").GetString()!;

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose() => Directory.Delete(directory, true);
}
