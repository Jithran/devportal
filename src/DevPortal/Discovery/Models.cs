namespace DevPortal.Discovery;

public enum InstanceSource { Host, Docker }

public enum ServerKind { Unknown, Dotnet, Node, Python, Php, Java, Docker }

public sealed record Listener(int Port, string Address, int Pid);

public sealed record ProcessDetails(
    int Pid,
    string Name,
    string? CommandLine,
    string? ExecutablePath,
    string? WorkingDirectory,
    DateTimeOffset? StartTime,
    int? SessionId = null);

public sealed record GitInfo(
    string RepoName,
    string MainRoot,
    string WorktreeRoot,
    string? WorktreeName,
    string? Branch,
    string? Commit);

public sealed record ProbeResult(int? StatusCode, string? Title, string? Url, string? Error, double? ElapsedMs = null);

public sealed record PublishedPort(int HostPort, int ContainerPort, string Protocol, string? BindAddress = null);

public sealed record ContainerInfo(
    string Id,
    string Name,
    string Image,
    string State,
    string Status,
    DateTimeOffset Created,
    string? ComposeProject,
    string? ComposeService,
    string? ComposeWorkingDir,
    IReadOnlyList<PublishedPort> Ports);

public sealed record Instance
{
    public required InstanceSource Source { get; init; }
    public required ServerKind Kind { get; init; }
    public int? Port { get; init; }
    public IReadOnlyList<string> Addresses { get; init; } = [];
    public string? Url { get; init; }
    public int? Pid { get; init; }
    public string? ProcessName { get; init; }
    public string? AppName { get; init; }
    public string? CommandLine { get; init; }
    public string? Path { get; init; }
    public DateTimeOffset? StartTime { get; init; }
    public long? UptimeSeconds { get; init; }
    public int? StatusCode { get; init; }
    public string? Title { get; init; }
    public string? ProbeError { get; init; }
    public double? ResponseMs { get; init; }

    /// <summary>Oldest first; null is a probe that got no HTTP response. Empty when nothing ever answered.</summary>
    public IReadOnlyList<double?> ResponseHistory { get; init; } = [];

    /// <summary>Of the listening process or the container as a whole, so every port of one owner repeats it. Percent of one core.</summary>
    public double? CpuPercent { get; init; }
    public long? MemoryBytes { get; init; }
    public IReadOnlyList<double> CpuHistory { get; init; } = [];
    public IReadOnlyList<long> MemoryHistory { get; init; } = [];
    public bool IsSystem { get; init; }
    public string? ContainerId { get; init; }
    public string? ContainerName { get; init; }
    public string? Image { get; init; }
    public string? ContainerStatus { get; init; }
    public string? ComposeProject { get; init; }
    public string? ComposeService { get; init; }
    public ServiceInfo Service { get; init; } = ServiceRecognizer.Unknown;
    public string? BindAddress { get; init; }
    public bool ExposedToNetwork { get; init; }
}

public sealed record VersionGroup(string Label, string? Branch, string? Commit, string? Worktree, string? Path, IReadOnlyList<Instance> Instances);

/// <summary>HasProject is false for groups that only collect leftovers (system, other processes, standalone containers).</summary>
public sealed record ProjectGroup(string Name, string Kind, string? Path, IReadOnlyList<VersionGroup> Versions, bool HasProject = true);

public sealed record Snapshot(
    DateTimeOffset GeneratedAt,
    string? DockerError,
    IReadOnlyList<ProjectGroup> Projects,
    double ScanMs = 0,
    IReadOnlyList<double>? ScanHistory = null);
