using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace DevPortal.Discovery;

public sealed class DockerClient : IDisposable
{
    private readonly HttpClient http;

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
        http = new HttpClient(handler) { BaseAddress = new Uri("http://docker/"), Timeout = TimeSpan.FromSeconds(3) };
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

    private static ContainerInfo Parse(JsonElement element)
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
                    port.GetProperty("Type").GetString() ?? "tcp"))
                .DistinctBy(port => (port.HostPort, port.Protocol))
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

    public void Dispose() => http.Dispose();
}
