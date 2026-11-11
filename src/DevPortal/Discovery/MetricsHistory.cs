using System.Collections.Concurrent;

namespace DevPortal.Discovery;

/// <summary>Fixed-size ring: the oldest sample is overwritten, so memory stays flat however long the portal runs.</summary>
public sealed class Ring<T>(int capacity)
{
    private readonly T[] buffer = new T[capacity];
    private readonly object gate = new();
    private int next;
    private int count;

    public void Add(T value)
    {
        lock (gate)
        {
            buffer[next] = value;
            next = (next + 1) % buffer.Length;
            count = Math.Min(count + 1, buffer.Length);
        }
    }

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<T> ToList()
    {
        lock (gate)
        {
            var start = (next - count + buffer.Length) % buffer.Length;
            return Enumerable.Range(0, count).Select(i => buffer[(start + i) % buffer.Length]).ToList();
        }
    }
}

/// <summary>
/// Recent response times per (port, owner) and recent scan durations. A null sample is a probe that got no HTTP
/// response: it keeps its slot (a gap in the sparkline) but never counts as a latency. Keyed by owner so a new
/// process on a reused port starts with a clean history. CPU and memory are kept per owner (process or container).
/// </summary>
public sealed class MetricsHistory(int capacity = MetricsHistory.DefaultCapacity)
{
    public const int DefaultCapacity = 60;

    private readonly ConcurrentDictionary<(int Port, string Owner), Ring<double?>> responses = new();
    private readonly Ring<double> scans = new(capacity);
    private readonly ConcurrentDictionary<string, (Ring<double> Cpu, Ring<long> Memory)> resources = new();

    public void RecordResponse(int port, string owner, double? ms) =>
        responses.GetOrAdd((port, owner), _ => new Ring<double?>(capacity)).Add(ms);

    public IReadOnlyList<double?> Responses(int port, string owner) =>
        responses.TryGetValue((port, owner), out var ring) ? ring.ToList() : [];

    public void RecordScan(double ms) => scans.Add(ms);

    public IReadOnlyList<double> Scans() => scans.ToList();

    public void Retain(IReadOnlySet<(int Port, string Owner)> live)
    {
        foreach (var key in responses.Keys.Where(k => !live.Contains(k)))
            responses.TryRemove(key, out _);
    }

    /// <summary>Only real readings are kept: a missing CPU delta (first sample, stale baseline) is not a gap but "not yet".</summary>
    public void RecordResources(string owner, double? cpuPercent, long? memoryBytes)
    {
        var rings = resources.GetOrAdd(owner, _ => (new Ring<double>(capacity), new Ring<long>(capacity)));
        if (cpuPercent is { } cpu)
            rings.Cpu.Add(cpu);
        if (memoryBytes is { } memory)
            rings.Memory.Add(memory);
    }

    public IReadOnlyList<double> Cpu(string owner) => resources.TryGetValue(owner, out var rings) ? rings.Cpu.ToList() : [];

    public IReadOnlyList<long> Memory(string owner) => resources.TryGetValue(owner, out var rings) ? rings.Memory.ToList() : [];

    public void RetainResources(IReadOnlySet<string> live)
    {
        foreach (var owner in resources.Keys.Where(k => !live.Contains(k)))
            resources.TryRemove(owner, out _);
    }
}
