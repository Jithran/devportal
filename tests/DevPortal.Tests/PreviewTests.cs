using DevPortal.Discovery;
using DevPortal.Preview;
using DevPortal.Server;
using DevPortal.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DevPortal.Tests;

public sealed class PreviewTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("devportal-preview-").FullName;
    private readonly PreviewCache cache;

    public PreviewTests() => cache = new PreviewCache(Path.Combine(directory, "previews"), TimeProvider.System);

    [Fact]
    public void Only_healthy_http_endpoints_on_loopback_are_targets_once_per_port()
    {
        var snapshot = Snap(
            Web(5000, "http://localhost:5000/"),
            Web(5000, "http://localhost:5000/"),
            Web(5001, "https://localhost:5001/"),
            Web(3000, "http://127.0.0.1:3000/"),
            Web(8080, "http://192.168.1.20:8080/"),
            Web(5002, "http://localhost:5002/", status: 500),
            Web(5432, null, status: null),
            Web(21, "ftp://localhost:21/"));

        var ports = PreviewService.Targets(snapshot).Select(t => t.Port);

        Assert.Equal([5000, 5001, 3000], ports);
    }

    [Fact]
    public void A_new_process_on_the_same_port_gets_a_new_preview()
    {
        var before = PreviewService.Targets(Snap(Web(5000, "http://localhost:5000/", pid: 10))).Single();
        var after = PreviewService.Targets(Snap(Web(5000, "http://localhost:5000/", pid: 11))).Single();

        Assert.NotEqual(before.Key, after.Key);
    }

    [Fact]
    public void Due_skips_fresh_previews_and_recent_failures_and_caps_each_tick()
    {
        var now = DateTimeOffset.UtcNow;
        var targets = Enumerable.Range(1, 8).Select(i => new PreviewTarget(5000 + i, $"http://localhost:{5000 + i}/", $"key{i}")).ToList();
        Write("key1", age: TimeSpan.FromMinutes(1));
        Write("key2", age: PreviewCache.Ttl + TimeSpan.FromMinutes(1));
        var failures = new Dictionary<(string, string), DateTimeOffset>
        {
            [("key3", PreviewService.Screenshot)] = now.AddMinutes(-1),
            [("key4", PreviewService.Screenshot)] = now - PreviewCache.Ttl - TimeSpan.FromMinutes(1),
        };

        var due = PreviewService.Due(targets, PreviewService.Screenshot, cache, failures, now);

        Assert.Equal(["key2", "key4", "key5", "key6"], due.Select(t => t.Key));
    }

    [Fact]
    public void Trim_keeps_the_newest_files_within_the_cap_and_drops_old_ones()
    {
        for (var i = 0; i < PreviewCache.MaxFiles + 3; i++)
            Write($"key{i}", age: TimeSpan.FromMinutes(i));
        Write("ancient", age: PreviewCache.MaxAge + TimeSpan.FromHours(1));

        cache.Trim();

        Assert.Equal(PreviewCache.MaxFiles, Directory.GetFiles(cache.Directory).Length);
        Assert.NotNull(cache.Find("key0", PreviewService.Screenshot));
        Assert.Null(cache.Find($"key{PreviewCache.MaxFiles}", PreviewService.Screenshot));
    }

    [Fact]
    public async Task Turning_previews_off_deletes_the_cache_without_touching_the_scan()
    {
        Write("key1", age: TimeSpan.Zero);
        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        var service = new PreviewService(settings, catalog: null!, cache, TimeProvider.System);

        await service.TickAsync(CancellationToken.None);

        Assert.False(Directory.Exists(cache.Directory));
    }

    [Fact]
    public void Preview_endpoint_serves_only_cached_images_of_ports_in_the_last_scan()
    {
        var snapshot = Snap(Web(5000, "http://localhost:5000/"), Web(5001, "http://localhost:5001/"));
        var cached = PreviewService.Targets(snapshot).First();
        cache.Store(cached.Key, PreviewService.Screenshot, path => File.WriteAllBytes(path, [1, 2, 3]));

        var served = PortalServer.Preview(5000, new DefaultHttpContext().Response, PreviewService.Screenshot, "image/png", snapshot, cache);
        var notCaptured = PortalServer.Preview(5001, new DefaultHttpContext().Response, PreviewService.Screenshot, "image/png", snapshot, cache);
        var notScanned = PortalServer.Preview(6000, new DefaultHttpContext().Response, PreviewService.Screenshot, "image/png", snapshot, cache);
        var off = PortalServer.Preview(5000, new DefaultHttpContext().Response, PreviewService.Screenshot, "image/png", null, cache);

        Assert.Equal([1, 2, 3], Assert.IsType<FileContentHttpResult>(served).FileContents.ToArray());
        Assert.All([notCaptured, notScanned, off], r => Assert.IsType<NotFound>(r));
    }

    [Theory]
    [InlineData(new[] { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe" }, @"Microsoft\Edge\Application\msedge.exe")]
    [InlineData(new[] { @"Google\Chrome\Application\chrome.exe" }, @"Google\Chrome\Application\chrome.exe")]
    [InlineData(new string[0], null)]
    public void Finds_Edge_before_Chrome_and_nothing_when_neither_is_installed(string[] installed, string? expected)
    {
        var found = PreviewService.FindBrowser(path => installed.Any(path.EndsWith));

        Assert.Equal(expected, found is null ? null : installed.Single(found.EndsWith));
    }

    [Theory]
    [InlineData(255, 255, 255, false, true)]
    [InlineData(18, 18, 24, false, true)]
    [InlineData(255, 255, 255, true, false)]
    public void Blank_captures_are_detected_but_a_page_with_content_is_not(int r, int g, int b, bool withContent, bool expectedBlank)
    {
        using var image = new System.Drawing.Bitmap(480, 300);
        using (var graphics = System.Drawing.Graphics.FromImage(image))
        {
            graphics.Clear(System.Drawing.Color.FromArgb(r, g, b));
            if (withContent)
                graphics.FillRectangle(System.Drawing.Brushes.Navy, 0, 0, 240, 300);
        }

        Assert.Equal(expectedBlank, PreviewService.IsBlank(image));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private void Write(string key, TimeSpan age)
    {
        cache.Store(key, PreviewService.Screenshot, path => File.WriteAllBytes(path, [0]));
        File.SetLastWriteTimeUtc(cache.Find(key, PreviewService.Screenshot)!, DateTime.UtcNow - age);
    }

    private static Instance Web(int port, string? url, int? status = 200, int pid = 1) => new()
    {
        Source = InstanceSource.Host,
        Kind = ServerKind.Node,
        Port = port,
        Url = url,
        StatusCode = status,
        Pid = pid,
    };

    private static Snapshot Snap(params Instance[] instances) =>
        new(DateTimeOffset.UtcNow, null, [new ProjectGroup("app", "repo", null, [new VersionGroup("main", null, null, null, null, instances)])]);
}
