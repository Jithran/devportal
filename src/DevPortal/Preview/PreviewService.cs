using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using DevPortal.Discovery;
using DevPortal.Settings;

namespace DevPortal.Preview;

public sealed record PreviewTarget(int Port, string Url, string Key);

/// <summary>
/// Background loop that screenshots healthy local web endpoints with an installed Edge or Chrome, one at a time.
/// It reads the last scan instead of starting one, so it never slows the scan down, and does nothing while the setting is off.
/// </summary>
public sealed class PreviewService(SettingsStore settings, InstanceCatalog catalog, PreviewCache cache, TimeProvider time) : BackgroundService
{
    public const string Screenshot = "png";
    public const string Icon = "ico";
    internal const int MaxPerTick = 4;
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(10);
    private const int ThumbWidth = 480;
    private const int ThumbHeight = 300;
    private const int CaptureWidth = 1280;
    private const int CaptureHeight = 800;
    private const int MaxIconBytes = 64 * 1024;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
    })
    {
        Timeout = TimeSpan.FromSeconds(3),
        MaxResponseContentBufferSize = MaxIconBytes,
    };

    private readonly ConcurrentDictionary<(string Key, string Kind), DateTimeOffset> failures = new();
    private readonly ConcurrentDictionary<string, int> blanks = new();
    private string? browser;
    private bool browserSearched;

    /// <summary>Healthy http(s) endpoints on loopback, one per port. The only URLs a preview is ever taken of.</summary>
    public static IEnumerable<PreviewTarget> Targets(Snapshot snapshot) =>
        snapshot.Projects
            .SelectMany(p => p.Versions)
            .SelectMany(v => v.Instances)
            .Where(i => i is { Port: not null, StatusCode: >= 200 and < 400 } && IsLocalWeb(i.Url))
            .DistinctBy(i => i.Port)
            .Select(i => new PreviewTarget(i.Port!.Value, i.Url!, $"{i.Url}|{i.Pid}|{i.StartTime:O}|{i.ContainerName}"));

    /// <summary>Targets whose file of this kind is missing or past its TTL and that did not fail within the TTL, capped per tick.</summary>
    internal static IReadOnlyList<PreviewTarget> Due(
        IEnumerable<PreviewTarget> targets, string kind, PreviewCache cache, IReadOnlyDictionary<(string Key, string Kind), DateTimeOffset> failures, DateTimeOffset now) =>
        targets
            .Where(t => !cache.IsFresh(t.Key, kind) && !(failures.TryGetValue((t.Key, kind), out var failed) && now - failed < PreviewCache.Ttl))
            .Take(MaxPerTick)
            .ToList();

    internal static string? FindBrowser(Func<string, bool> exists)
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ];
        string[] browsers = [@"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe"];
        return browsers
            .SelectMany(b => roots.Where(r => r.Length > 0).Select(r => Path.Combine(r, b)))
            .FirstOrDefault(exists);
    }

    /// <summary>
    /// A throwaway profile (no cookies or logins of the user), and every host except loopback made unreachable:
    /// the dead proxy catches IP literals, the resolver rule catches names. Chrome never proxies loopback itself.
    /// </summary>
    internal static IReadOnlyList<string> BrowserArguments(string profile) =>
    [
        "--headless=new",
        $"--user-data-dir={profile}",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-extensions",
        "--disable-sync",
        "--disable-background-networking",
        "--disable-component-update",
        "--disable-default-apps",
        "--disable-gpu",
        "--mute-audio",
        "--hide-scrollbars",
        "--ignore-certificate-errors",
        "--proxy-server=http://127.0.0.1:9",
        "--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE localhost",
        "--remote-debugging-port=0",
        "about:blank",
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Out of the way of startup and the first scans.
            await Task.Delay(TimeSpan.FromSeconds(15), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await TickAsync(stoppingToken);
                await Task.Delay(Interval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!settings.Current.ScreenshotsEnabled)
            {
                cache.Clear();
                failures.Clear();
                blanks.Clear();
                return;
            }
            if (catalog.Latest is not { } snapshot)
                return;

            if (!browserSearched)
            {
                browser = FindBrowser(File.Exists);
                browserSearched = true;
                AppLog.Info(browser is null ? "Previews: no Edge or Chrome found; favicons only." : $"Previews: using {browser}");
            }

            var targets = Targets(snapshot).ToList();
            var now = time.GetUtcNow();
            foreach (var target in Due(targets, Icon, cache, failures, now))
                Record(target, Icon, await FetchIconAsync(target, cancellationToken));
            if (browser is not null)
            {
                // A page that came out blank twice is shown as favicon + title until its process restarts.
                foreach (var target in Due(targets, Screenshot, cache, failures, now).Where(t => blanks.GetValueOrDefault(t.Key) < 2))
                    Record(target, Screenshot, await CaptureAsync(browser, target, cancellationToken));
            }
            cache.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("Preview tick failed", ex);
        }
    }

    private void Record(PreviewTarget target, string kind, bool stored)
    {
        if (stored)
            failures.TryRemove((target.Key, kind), out _);
        else
            failures[(target.Key, kind)] = time.GetUtcNow();
    }

    // shortcut: only /favicon.ico, not <link rel="icon">; parse the page head if many apps turn out to use another path.
    private async Task<bool> FetchIconAsync(PreviewTarget target, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(new Uri(new Uri(target.Url), "/favicon.ico"), cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal) != true)
                return false;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            cache.Store(target.Key, Icon, path => File.WriteAllBytes(path, bytes));
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> CaptureAsync(string executable, PreviewTarget target, CancellationToken cancellationToken)
    {
        var work = Directory.CreateTempSubdirectory("devportal-preview-");
        Process? process = null;
        try
        {
            var profile = Path.Combine(work.FullName, "profile");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in BrowserArguments(profile))
                start.ArgumentList.Add(argument);

            var started = time.GetTimestamp();
            process = Process.Start(start)!;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(CaptureTimeout);
            byte[] png;
            try
            {
                using var page = await DevToolsPage.ConnectAsync(Http, profile, deadline.Token);
                png = await page.CaptureAsync(target.Url, CaptureWidth, CaptureHeight, deadline.Token);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw;
                AppLog.Warn($"Preview of port {target.Port} timed out after {CaptureTimeout.TotalSeconds:0} s; browser killed.");
                return false;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException or System.Net.WebSockets.WebSocketException)
            {
                AppLog.Warn($"Preview of port {target.Port} failed: {ex.Message}");
                return false;
            }

            using var thumb = Thumbnail(png);
            if (IsBlank(thumb))
            {
                // A stale blank from before this fix must not keep being served either.
                cache.Remove(target.Key, Screenshot);
                var count = blanks.AddOrUpdate(target.Key, 1, (_, n) => n + 1);
                AppLog.Warn($"Preview of port {target.Port} is blank (attempt {count}); " + (count < 2 ? "retrying later." : "showing favicon and title."));
                return false;
            }
            blanks.TryRemove(target.Key, out _);
            cache.Store(target.Key, Screenshot, path => thumb.Save(path, ImageFormat.Png));
            AppLog.Info($"Preview of port {target.Port} took {time.GetElapsedTime(started).TotalMilliseconds:0} ms");
            return true;
        }
        finally
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
            process?.Dispose();
            await DeleteAsync(work);
        }
    }

    private static Bitmap Thumbnail(byte[] png)
    {
        using var full = new Bitmap(new MemoryStream(png));
        var thumb = new Bitmap(ThumbWidth, ThumbHeight);
        using var graphics = Graphics.FromImage(thumb);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(full, 0, 0, ThumbWidth, ThumbHeight);
        return thumb;
    }

    /// <summary>All-white or otherwise near-uniform: under 1 % of the sampled pixels differ visibly from the first one.</summary>
    internal static bool IsBlank(Bitmap image)
    {
        var reference = image.GetPixel(0, 0);
        int sampled = 0, different = 0;
        for (var y = 0; y < image.Height; y += 3)
        {
            for (var x = 0; x < image.Width; x += 3)
            {
                var pixel = image.GetPixel(x, y);
                sampled++;
                if (Math.Abs(pixel.R - reference.R) + Math.Abs(pixel.G - reference.G) + Math.Abs(pixel.B - reference.B) > 24)
                    different++;
            }
        }
        return different * 100 < sampled;
    }

    private static async Task DeleteAsync(DirectoryInfo work)
    {
        // Browser helper processes can hold the profile for a moment after the main process exits.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                work.Delete(recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(500);
            }
        }
        AppLog.Warn($"Could not delete {work.FullName}");
    }

    private static bool IsLocalWeb(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
}
