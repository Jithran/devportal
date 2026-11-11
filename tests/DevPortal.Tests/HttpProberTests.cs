using System.Net;
using System.Net.Sockets;
using System.Text;
using DevPortal.Discovery;
using Microsoft.Extensions.Options;

namespace DevPortal.Tests;

public sealed class HttpProberTests
{
    [Fact]
    public async Task Html_body_that_never_ends_still_yields_status_and_title_within_the_timeout()
    {
        // JetBrains Toolbox answers like this and keeps the connection open, which used to hang the whole page.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await stream.ReadAtLeastAsync(new byte[4096], 1);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\ncontent-type: text/html\r\n\r\n<!doctype html><title>404 Not Found</title>"));
            await Task.Delay(TimeSpan.FromSeconds(30));
        });
        using var prober = new HttpProber(Options.Create(new PortalOptions { ProbeTimeoutMs = 500 }), TimeProvider.System, new MetricsHistory());

        var result = await prober.ProbeAsync("127.0.0.1", port, "test").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal((404, "404 Not Found"), (result.StatusCode, result.Title));
    }

    [Fact]
    public async Task Answered_probe_records_a_response_time_and_a_dead_port_records_a_gap()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await stream.ReadAtLeastAsync(new byte[4096], 1);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\ncontent-length: 0\r\nconnection: close\r\n\r\n"));
        });
        var history = new MetricsHistory();
        using var prober = new HttpProber(Options.Create(new PortalOptions { ProbeTimeoutMs = 500, ProbeCacheSeconds = 0 }), TimeProvider.System, history);

        var ok = await prober.ProbeAsync("127.0.0.1", port, "test");
        listener.Stop();
        await prober.ProbeAsync("127.0.0.1", port, "test"); // stale-while-revalidate: refreshes in the background
        while (history.Responses(port, "test").Count < 2)
            await Task.Delay(50).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(ok.ElapsedMs > 0);
        var samples = history.Responses(port, "test");
        Assert.True(samples[0] > 0);
        Assert.Null(samples[^1]);
    }
}
