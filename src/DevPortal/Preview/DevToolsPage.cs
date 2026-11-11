using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DevPortal.Preview;

/// <summary>
/// Minimal Chrome DevTools Protocol session over the browser's loopback debugging port: navigate, wait until the page has
/// stopped loading and painting, take a PNG. Needed because headless <c>--screenshot</c> fires before script-rendered
/// pages (Blazor, React) have booted, and its virtual-time budget starves the real I/O those pages wait for.
/// </summary>
internal sealed class DevToolsPage : IDisposable
{
    internal static readonly TimeSpan NavigationBudget = TimeSpan.FromSeconds(6);
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(800);

    private readonly ClientWebSocket socket = new();
    private Task<JsonDocument>? pending;
    private int nextId;

    public static async Task<DevToolsPage> ConnectAsync(HttpClient http, string profile, CancellationToken cancellationToken)
    {
        var portFile = Path.Combine(profile, "DevToolsActivePort");
        while (!File.Exists(portFile))
            await Task.Delay(100, cancellationToken);
        var port = (await ReadSharedAsync(portFile, cancellationToken)).Split('\n')[0].Trim();

        using var targets = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", cancellationToken));
        var page = targets.RootElement.EnumerateArray().First(t => t.GetProperty("type").GetString() == "page");

        var session = new DevToolsPage();
        await session.socket.ConnectAsync(new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!), cancellationToken);
        return session;
    }

    /// <summary>The PNG of the page at <paramref name="url"/> after it settled, or after <see cref="NavigationBudget"/> at the latest.</summary>
    public async Task<byte[]> CaptureAsync(string url, int width, int height, CancellationToken cancellationToken)
    {
        await CallAsync("Page.enable", null, cancellationToken);
        await CallAsync("Page.setLifecycleEventsEnabled", new { enabled = true }, cancellationToken);
        await CallAsync("Emulation.setDeviceMetricsOverride", new { width, height, deviceScaleFactor = 1, mobile = false }, cancellationToken);
        await CallAsync("Page.navigate", new { url }, cancellationToken);
        await WaitUntilSettledAsync(cancellationToken);

        using var shot = await CallAsync("Page.captureScreenshot", new { format = "png" }, cancellationToken);
        return Convert.FromBase64String(shot.RootElement.GetProperty("result").GetProperty("data").GetString()!);
    }

    /// <summary>
    /// Settled = the page reported network idle and then stayed quiet (no further navigation, paint or load event) for
    /// <see cref="QuietPeriod"/>. A script redirect after the first idle, as Blazor login guards do, restarts the wait.
    /// </summary>
    private async Task WaitUntilSettledAsync(CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var idle = false;
        while (DateTime.UtcNow - started < NavigationBudget)
        {
            var wait = idle ? QuietPeriod : NavigationBudget - (DateTime.UtcNow - started);
            using var message = await NextAsync(wait, cancellationToken);
            if (message is null)
                return;
            if (!message.RootElement.TryGetProperty("method", out var method))
                continue;
            if (method.GetString() == "Page.frameNavigated")
                idle = false;
            else if (method.GetString() == "Page.lifecycleEvent" && message.RootElement.GetProperty("params").GetProperty("name").GetString() == "networkIdle")
                idle = true;
        }
    }

    private async Task<JsonDocument> CallAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = ++nextId;
        var request = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { } });
        await socket.SendAsync(request, WebSocketMessageType.Text, true, cancellationToken);
        while (true)
        {
            var message = (await NextAsync(null, cancellationToken))!;
            if (message.RootElement.TryGetProperty("id", out var answered) && answered.GetInt32() == id)
            {
                if (message.RootElement.TryGetProperty("error", out var error))
                    throw new InvalidOperationException($"DevTools {method} failed: {error.GetProperty("message").GetString()}");
                return message;
            }
            message.Dispose();
        }
    }

    // One receive stays pending across a timeout: cancelling a ClientWebSocket receive would abort the whole socket.
    private async Task<JsonDocument?> NextAsync(TimeSpan? wait, CancellationToken cancellationToken)
    {
        pending ??= ReceiveAsync(cancellationToken);
        if (wait is { } timeout && await Task.WhenAny(pending, Task.Delay(timeout, cancellationToken)) != pending)
            return null;
        var message = await pending;
        pending = null;
        return message;
    }

    private async Task<JsonDocument> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var whole = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new IOException("The browser closed the DevTools connection.");
            whole.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(whole.ToArray());
    }

    // The browser still has the port file open for writing for a moment.
    private static async Task<string> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public void Dispose() => socket.Dispose();
}
