using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace DevPortal.Discovery;

public sealed partial class HttpProber : IDisposable
{
    private const int MaxBodyBytes = 64 * 1024;
    private readonly HttpClient http;
    private readonly PortalOptions options;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<(string Host, int Port, string Owner), (DateTimeOffset At, ProbeResult Result)> cache = new();
    private readonly ConcurrentDictionary<(string Host, int Port, string Owner), Task<ProbeResult>> inflight = new();

    public HttpProber(IOptions<PortalOptions> options, TimeProvider time)
    {
        this.options = options.Value;
        this.time = time;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(this.options.ProbeTimeoutMs),
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(this.options.ProbeTimeoutMs) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DevPortal/1.0");
    }

    /// <param name="host">Address to connect to (loopback form of the bound address).</param>
    /// <param name="owner">Identity of whatever owns the port, so a new process on the same port is re-probed.</param>
    public async Task<ProbeResult> ProbeAsync(string host, int port, string owner)
    {
        if (options.NonHttpPorts.Contains(port))
            return new ProbeResult(null, null, null, "not HTTP");

        var key = (host, port, owner);
        if (!cache.TryGetValue(key, out var cached))
            return await RefreshAsync(key);

        // Stale-while-revalidate: a silent non-HTTP port costs a full timeout, which must not stall the page.
        if (time.GetUtcNow() - cached.At >= TimeSpan.FromSeconds(options.ProbeCacheSeconds))
            _ = RefreshAsync(key);
        return cached.Result;
    }

    private Task<ProbeResult> RefreshAsync((string Host, int Port, string Owner) key) =>
        inflight.GetOrAdd(key, k => Task.Run(async () =>
        {
            try
            {
                var result = await ProbeUncachedAsync(k.Host, k.Port, CancellationToken.None);
                cache[k] = (time.GetUtcNow(), result);
                return result;
            }
            finally
            {
                inflight.TryRemove(k, out _);
            }
        }));

    public void Forget(Func<(string Host, int Port, string Owner), bool> isGone)
    {
        foreach (var key in cache.Keys.Where(isGone))
            cache.TryRemove(key, out _);
    }

    private async Task<ProbeResult> ProbeUncachedAsync(string host, int port, CancellationToken cancellationToken)
    {
        var http1 = await TryAsync($"http://{host}:{port}/", cancellationToken);
        if (http1.StatusCode is not null)
            return http1;

        var https = await TryAsync($"https://{host}:{port}/", cancellationToken);
        return https.StatusCode is not null ? https : http1;
    }

    private async Task<ProbeResult> TryAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("text/html,*/*;q=0.8");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var title = IsHtml(response) ? await ReadTitleAsync(response, cancellationToken) : null;
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            return new ProbeResult((int)response.StatusCode, title, finalUrl, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or OperationCanceledException)
        {
            return new ProbeResult(null, null, null, ex is TaskCanceledException ? "timeout" : "no HTTP response");
        }
    }

    private static bool IsHtml(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) ?? false;

    private static async Task<string?> ReadTitleAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxBodyBytes];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
            total += read;

        var html = Encoding.UTF8.GetString(buffer, 0, total);
        return new[] { TitlePattern(), MetaTitlePattern(), HeadingPattern() }
            .Select(pattern => pattern.Match(html))
            .Where(match => match.Success)
            .Select(match => Clean(match.Groups["text"].Value))
            .FirstOrDefault(text => text.Length > 0);
    }

    private static string Clean(string raw) =>
        WebUtility.HtmlDecode(Whitespace().Replace(Tags().Replace(raw, " "), " ")).Trim();

    [GeneratedRegex(@"<title[^>]*>(?<text>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"<meta[^>]+(?:property|name)=[""'](?:og:title|application-name)[""'][^>]*content=[""'](?<text>[^""']*)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTitlePattern();

    [GeneratedRegex(@"<h1[^>]*>(?<text>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public void Dispose() => http.Dispose();
}
