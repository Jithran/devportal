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
            await stream.ReadAsync(new byte[4096]);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\ncontent-type: text/html\r\n\r\n<!doctype html><title>404 Not Found</title>"));
            await Task.Delay(TimeSpan.FromSeconds(30));
        });
        using var prober = new HttpProber(Options.Create(new PortalOptions { ProbeTimeoutMs = 500 }), TimeProvider.System);

        var result = await prober.ProbeAsync("127.0.0.1", port, "test").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal((404, "404 Not Found"), (result.StatusCode, result.Title));
    }
}
