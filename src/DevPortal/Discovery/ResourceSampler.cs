using DevPortal.Native;

namespace DevPortal.Discovery;

/// <summary>Cumulative CPU time used and the clock it is measured against, in the same unit. Cores scales the ratio to
/// percent of one core: a container's clock is host time summed over all cores, a process's clock is wall time (Cores 1).</summary>
public readonly record struct CpuCounter(double Used, double Clock, int Cores);

public sealed record ContainerStats(CpuCounter? Cpu, long? MemoryBytes);

/// <summary>CpuPercent is percent of one core (can exceed 100, like <c>docker stats</c>); null until a second sample exists.</summary>
public sealed record ResourceSample(double? CpuPercent, long? MemoryBytes);

/// <summary>
/// Samples CPU and memory once per scan. CPU is a delta against the previous scan's counters, so a baseline older than
/// MaxBaselineAge (nobody had the page open) is discarded instead of averaging over the idle stretch. Owners carry the
/// process start time, so a reused PID starts from a clean baseline and history.
/// </summary>
public sealed class ResourceSampler(DockerClient docker, MetricsHistory history, TimeProvider time)
{
    internal static readonly TimeSpan MaxBaselineAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StatsTimeout = TimeSpan.FromMilliseconds(1500);
    private const int DockerConcurrency = 4;

    private readonly Dictionary<string, (CpuCounter Counter, DateTimeOffset At)> baselines = [];

    public static string ProcessOwner(int pid, DateTimeOffset? start) => $"pid:{pid}@{start?.UtcTicks}";

    public static string ContainerOwner(string id) => $"container:{id}";

    /// <summary>Not thread-safe: the catalog calls it under its scan gate.</summary>
    public async Task<IReadOnlyDictionary<string, ResourceSample>> SampleAsync(
        IEnumerable<ProcessDetails> processes, IEnumerable<ContainerInfo> containers, CancellationToken cancellationToken)
    {
        using var slots = new SemaphoreSlim(DockerConcurrency);
        var containerReadings = containers.Select(async container =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                var stats = await docker.GetStatsAsync(container.Id, StatsTimeout, cancellationToken);
                return new Reading(ContainerOwner(container.Id), stats?.Cpu, stats?.MemoryBytes, time.GetUtcNow());
            }
            finally
            {
                slots.Release();
            }
        }).ToList();

        var hostReadings = processes.Where(p => p.Pid is not (0 or 4)).Select(ReadProcess).ToList();
        var readings = hostReadings.Concat(await Task.WhenAll(containerReadings));

        var samples = readings.DistinctBy(r => r.Owner).ToDictionary(r => r.Owner, Record);
        foreach (var gone in baselines.Keys.Where(owner => !samples.ContainsKey(owner)).ToList())
            baselines.Remove(gone);
        history.RetainResources(samples.Keys.ToHashSet());
        return samples;
    }

    internal static double? CpuPercent(CpuCounter previous, DateTimeOffset previousAt, CpuCounter current, DateTimeOffset at)
    {
        if (at - previousAt > MaxBaselineAge)
            return null;
        var used = current.Used - previous.Used;
        var clock = current.Clock - previous.Clock;
        // A counter that went backwards is a restarted container; no elapsed clock means nothing to divide by.
        if (used < 0 || clock <= 0)
            return null;
        return Math.Round(used / clock * current.Cores * 100, 1);
    }

    private ResourceSample Record(Reading reading)
    {
        double? percent = null;
        if (reading.Cpu is { } current)
        {
            if (baselines.TryGetValue(reading.Owner, out var previous))
                percent = CpuPercent(previous.Counter, previous.At, current, reading.At);
            baselines[reading.Owner] = (current, reading.At);
        }
        history.RecordResources(reading.Owner, percent, reading.MemoryBytes);
        return new ResourceSample(percent, reading.MemoryBytes);
    }

    /// <summary>Memory is the working set, Task Manager's default column.</summary>
    private Reading ReadProcess(ProcessDetails details)
    {
        var at = time.GetUtcNow();
        var counters = ProcessCounters.Read(details.Pid);
        var cpu = counters?.CpuTicks is { } ticks ? new CpuCounter(ticks, at.UtcTicks, 1) : (CpuCounter?)null;
        return new Reading(ProcessOwner(details.Pid, details.StartTime), cpu, counters?.WorkingSetBytes, at);
    }

    private sealed record Reading(string Owner, CpuCounter? Cpu, long? MemoryBytes, DateTimeOffset At);
}
