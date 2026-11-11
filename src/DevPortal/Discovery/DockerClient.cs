using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace DevPortal.Discovery;

public enum ContainerAction { Stop, Restart }

public sealed class DockerClient : IDisposable
{
    private readonly HttpClient http;
    private readonly HttpClient actions;

    public DockerClient(IOptions<PortalOptions> options)
    {
        var pipeName = options.Value.DockerPipe;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(1000, cancellationToken);
                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }
            },
        };
        http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://docker/"), Timeout = TimeSpan.FromSeconds(3) };
        // A stop waits up to the container's grace period (10s by default) before the engine kills it.
        actions = new HttpClient(handler) { BaseAddress = http.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<(IReadOnlyList<ContainerInfo> Containers, string? Error)> ListRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await http.GetStreamAsync("containers/json", cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return (document.RootElement.EnumerateArray().Select(Parse).ToList(), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or TaskCanceledException or UnauthorizedAccessException or JsonException)
        {
            return ([], $"Docker unavailable: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>Null on success, otherwise the reason. 304 means the container was already in that state, which is what was asked.</summary>
    public async Task<string?> ActAsync(string id, ContainerAction action, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await actions.PostAsync($"containers/{id}/{action.ToString().ToLowerInvariant()}", null, cancellationToken);
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotModified ? null : $"Docker answered {(int)response.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or TaskCanceledException or UnauthorizedAccessException)
        {
            return $"Docker unavailable: {ex.GetBaseException().Message}";
        }
    }

    /// <summary>One-shot: the engine returns its last reading at once instead of waiting a second for a delta, so CPU is
    /// computed from two consecutive scans. Null on any failure; a gone container is not worth a log line.</summary>
    public async Task<ContainerStats?> GetStatsAsync(string id, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await using var stream = await http.GetStreamAsync($"containers/{id}/stats?stream=false&one-shot=true", cts.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            return ParseStats(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Memory excludes the page cache (inactive_file), as <c>docker stats</c> does: cgroup v2 then v1 naming.</summary>
    internal static ContainerStats ParseStats(JsonElement root)
    {
        CpuCounter? cpu = null;
        if (root.TryGetProperty("cpu_stats", out var c)
            && c.TryGetProperty("cpu_usage", out var usage) && usage.TryGetProperty("total_usage", out var total)
            && c.TryGetProperty("system_cpu_usage", out var system))
        {
            var cores = c.TryGetProperty("online_cpus", out var online) && online.GetInt32() > 0
                ? online.GetInt32()
                : usage.TryGetProperty("percpu_usage", out var perCpu) && perCpu.ValueKind == JsonValueKind.Array ? Math.Max(1, perCpu.GetArrayLength()) : 1;
            cpu = new CpuCounter(total.GetDouble(), system.GetDouble(), cores);
        }

        long? memory = null;
        if (root.TryGetProperty("memory_stats", out var m) && m.TryGetProperty("usage", out var used))
        {
            var cache = m.TryGetProperty("stats", out var s)
                ? s.TryGetProperty("inactive_file", out var v2) ? v2.GetInt64() : s.TryGetProperty("total_inactive_file", out var v1) ? v1.GetInt64() : 0
                : 0;
            memory = Math.Max(0, used.GetInt64() - cache);
        }

        return new ContainerStats(cpu, memory);
    }

    internal static ContainerInfo Parse(JsonElement element)
    {
        var labels = element.TryGetProperty("Labels", out var l) && l.ValueKind == JsonValueKind.Object
            ? l.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString())
            : [];

        var ports = element.TryGetProperty("Ports", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray()
                .Where(port => port.TryGetProperty("PublicPort", out _))
                .Select(port => new PublishedPort(
                    port.GetProperty("PublicPort").GetInt32(),
                    port.GetProperty("PrivatePort").GetInt32(),
                    port.GetProperty("Type").GetString() ?? "tcp",
                    port.TryGetProperty("IP", out var ip) ? ip.GetString() : null))
                .GroupBy(port => (port.HostPort, port.Protocol))
                .Select(group => group.FirstOrDefault(port => ServiceRecognizer.IsAllInterfaces(port.BindAddress)) ?? group.First())
                .OrderBy(port => port.HostPort)
                .ToList()
            : [];

        var name = element.GetProperty("Names").EnumerateArray().Select(n => n.GetString()?.TrimStart('/')).FirstOrDefault() ?? "";

        return new ContainerInfo(
            element.GetProperty("Id").GetString()![..12],
            name,
            element.GetProperty("Image").GetString() ?? "",
            element.GetProperty("State").GetString() ?? "",
            element.GetProperty("Status").GetString() ?? "",
            DateTimeOffset.FromUnixTimeSeconds(element.GetProperty("Created").GetInt64()),
            labels.GetValueOrDefault("com.docker.compose.project"),
            labels.GetValueOrDefault("com.docker.compose.service"),
            labels.GetValueOrDefault("com.docker.compose.project.working_dir"),
            ports);
    }

    public void Dispose()
    {
        http.Dispose();
        actions.Dispose();
    }
}
