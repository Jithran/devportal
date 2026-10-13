using DevPortal.Native;
using Microsoft.Extensions.Options;

namespace DevPortal.Discovery;

public sealed class InstanceCatalog(
    ProcessInspector processes,
    GitResolver git,
    DockerClient docker,
    HttpProber prober,
    IOptions<PortalOptions> options,
    TimeProvider time)
{
    private const string OtherProject = "Other processes";
    private const string SystemProject = "System";
    private const string StandaloneContainers = "Standalone containers";

    private readonly PortalOptions settings = options.Value;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private Snapshot? cached;

    public async Task<Snapshot> GetAsync(bool includeSystem, CancellationToken cancellationToken)
    {
        var snapshot = await GetFullAsync(cancellationToken);
        return includeSystem ? snapshot : snapshot with { Projects = WithoutSystem(snapshot.Projects) };
    }

    private async Task<Snapshot> GetFullAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cached is null || time.GetUtcNow() - cached.GeneratedAt >= TimeSpan.FromSeconds(settings.CacheSeconds))
                cached = await BuildAsync(cancellationToken);
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Snapshot> BuildAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var (containers, dockerError) = await docker.ListRunningAsync(cancellationToken);
        var dockerPorts = containers.SelectMany(c => c.Ports).Where(p => p.Protocol == "tcp").Select(p => p.HostPort).ToHashSet();

        var listeners = TcpTable.GetListeners()
            .Where(l => !dockerPorts.Contains(l.Port) && !settings.HiddenPorts.Contains(l.Port))
            .GroupBy(l => (l.Pid, l.Port))
            .ToList();

        var details = processes.Inspect(listeners.Select(g => g.Key.Pid).ToHashSet());

        var hostTasks = listeners.Select(group => BuildHostAsync(group.Key.Port, group.Select(l => l.Address).Distinct().ToList(), details[group.Key.Pid], now));
        var dockerTasks = containers.SelectMany(container => BuildContainer(container, now));
        var placed = await Task.WhenAll(hostTasks.Concat(dockerTasks));

        prober.Forget(key => !placed.Any(p => p.Instance.Port == key.Port));

        return new Snapshot(now, dockerError, Group(placed));
    }

    private async Task<Placed> BuildHostAsync(int port, IReadOnlyList<string> addresses, ProcessDetails process, DateTimeOffset now)
    {
        var isSystem = IsSystem(process);
        var repo = isSystem ? null : ResolveRepo(process);
        var probeHost = ProbeHost(addresses);
        var probe = isSystem ? null : await prober.ProbeAsync(probeHost, port, $"pid:{process.Pid}");

        var instance = new Instance
        {
            Source = InstanceSource.Host,
            Kind = ServerKindDetector.Detect(process),
            Port = port,
            Addresses = addresses.Order().ToList(),
            Url = BrowserUrl(probe, probeHost, port),
            Pid = process.Pid,
            ProcessName = process.Name,
            AppName = AppName(process),
            CommandLine = process.CommandLine is null ? null : CommandLine.Redact(process.CommandLine),
            Path = process.WorkingDirectory ?? process.ExecutablePath,
            StartTime = process.StartTime,
            UptimeSeconds = process.StartTime is { } start ? (long)(now - start).TotalSeconds : null,
            StatusCode = probe?.StatusCode,
            Title = probe?.Title,
            ProbeError = probe?.Error,
            IsSystem = isSystem,
        };

        if (isSystem)
            return new Placed(new GroupKey(SystemProject, "system", null), new VersionKey(process.Name, null, null, null, process.ExecutablePath), instance);
        if (repo is null)
            return new Placed(new GroupKey(OtherProject, "other", null), new VersionKey(process.Name, null, null, null, process.ExecutablePath), instance);
        return new Placed(
            new GroupKey(repo.RepoName, "repo", repo.MainRoot),
            new VersionKey(repo.Branch ?? repo.Commit ?? "detached", repo.Branch, repo.Commit, repo.WorktreeName, repo.WorktreeRoot),
            instance);
    }

    private IEnumerable<Task<Placed>> BuildContainer(ContainerInfo container, DateTimeOffset now)
    {
        var repo = git.Resolve(container.ComposeWorkingDir);
        var group = container.ComposeProject is { } project
            ? new GroupKey(project, "compose", container.ComposeWorkingDir)
            : new GroupKey(StandaloneContainers, "compose", null);
        var version = repo is null
            ? new VersionKey(container.ComposeProject is null ? "containers" : "compose", null, null, null, container.ComposeWorkingDir)
            : new VersionKey(repo.Branch ?? repo.Commit ?? "detached", repo.Branch, repo.Commit, repo.WorktreeName, repo.WorktreeRoot);

        var template = new Instance
        {
            Source = InstanceSource.Docker,
            Kind = ServerKind.Docker,
            ContainerName = container.Name,
            Image = container.Image,
            ContainerStatus = container.Status,
            ComposeService = container.ComposeService,
            StartTime = container.Created,
            UptimeSeconds = (long)(now - container.Created).TotalSeconds,
        };

        var tcpPorts = container.Ports.Where(p => p.Protocol == "tcp").ToList();
        if (tcpPorts.Count == 0)
            return [Task.FromResult(new Placed(group, version, template))];

        return tcpPorts.Select(async port =>
        {
            var probe = await prober.ProbeAsync("127.0.0.1", port.HostPort, $"container:{container.Id}");
            return new Placed(group, version, template with
            {
                Port = port.HostPort,
                Addresses = [$"{port.HostPort}->{port.ContainerPort}/tcp"],
                Url = BrowserUrl(probe, "127.0.0.1", port.HostPort),
                StatusCode = probe.StatusCode,
                Title = probe.Title,
                ProbeError = probe.Error,
            });
        });
    }

    private GitInfo? ResolveRepo(ProcessDetails process)
    {
        var candidates = new List<string?> { process.WorkingDirectory };
        candidates.AddRange(CommandLine.CandidatePaths(process.CommandLine, process.WorkingDirectory));
        candidates.Add(process.ExecutablePath);
        return candidates.Select(git.Resolve).FirstOrDefault(info => info is not null);
    }

    /// <summary>For generic hosts (dotnet, node, python, …) the entry script or assembly names the app, not the host.</summary>
    private static string AppName(ProcessDetails process)
    {
        var entry = CommandLine.CandidatePaths(process.CommandLine, process.WorkingDirectory)
            .FirstOrDefault(path => Path.HasExtension(path) && !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        return ServerKindDetector.Detect(process) != ServerKind.Unknown && entry is not null
            ? Path.GetFileNameWithoutExtension(entry)
            : process.Name;
    }

    /// <summary>Session 0 holds services only; as a normal user DevPortal cannot read their paths, but it can see the session.</summary>
    private bool IsSystem(ProcessDetails process) =>
        process.Pid is 0 or 4
        || process.SessionId == 0
        || settings.SystemProcesses.Contains(process.Name, StringComparer.OrdinalIgnoreCase)
        || (process.ExecutablePath?.StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase) ?? false);

    private static string ProbeHost(IReadOnlyList<string> addresses)
    {
        if (addresses.Any(a => a is "0.0.0.0" or "127.0.0.1"))
            return "127.0.0.1";
        if (addresses.Any(a => a is "::" or "::1"))
            return "[::1]";
        var address = addresses[0];
        return address.Contains(':') ? $"[{address}]" : address;
    }

    private static string BrowserUrl(ProbeResult? probe, string probeHost, int port)
    {
        var scheme = probe?.Url?.StartsWith("https:", StringComparison.Ordinal) == true ? "https" : "http";
        var host = probeHost is "127.0.0.1" or "[::1]" ? "localhost" : probeHost;
        return $"{scheme}://{host}:{port}/";
    }

    private static List<ProjectGroup> Group(IEnumerable<Placed> placed) =>
        placed
            .GroupBy(p => p.Group)
            .Select(project => new ProjectGroup(
                project.Key.Name,
                project.Key.Kind,
                project.Key.Path,
                project.GroupBy(p => p.Version)
                    .Select(version => new VersionGroup(
                        version.Key.Label,
                        version.Key.Branch,
                        version.Key.Commit,
                        version.Key.Worktree,
                        version.Key.Path,
                        version.Select(p => p.Instance).OrderBy(i => i.Port ?? int.MaxValue).ThenBy(i => i.ContainerName).ToList()))
                    .OrderBy(v => v.Worktree is not null).ThenBy(v => v.Label, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .OrderBy(p => KindOrder(p.Kind)).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int KindOrder(string kind) => kind switch { "repo" => 0, "compose" => 1, "other" => 2, _ => 3 };

    private static List<ProjectGroup> WithoutSystem(IReadOnlyList<ProjectGroup> projects) =>
        projects.Where(p => p.Kind != "system").ToList();

    private sealed record GroupKey(string Name, string Kind, string? Path);

    private sealed record VersionKey(string Label, string? Branch, string? Commit, string? Worktree, string? Path);

    private sealed record Placed(GroupKey Group, VersionKey Version, Instance Instance);
}
