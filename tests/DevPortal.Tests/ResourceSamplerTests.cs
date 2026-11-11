using System.Diagnostics;
using System.Text.Json;
using DevPortal.Discovery;
using Microsoft.Extensions.Options;

namespace DevPortal.Tests;

public sealed class ResourceSamplerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Container_cpu_is_percent_of_one_core_like_docker_stats()
    {
        // 4 cores for 3 s = 12e9 ns of host time; the container used 1.5e9 ns = half a core.
        var previous = new CpuCounter(10e9, 100e9, 4);
        var current = new CpuCounter(11.5e9, 112e9, 4);

        Assert.Equal(50, ResourceSampler.CpuPercent(previous, T0, current, T0.AddSeconds(3)));
    }

    [Fact]
    public void Process_cpu_divides_cpu_time_by_wall_time()
    {
        var previous = new CpuCounter(TimeSpan.FromSeconds(2).Ticks, T0.UtcTicks, 1);
        var at = T0.AddSeconds(3);
        var current = new CpuCounter(TimeSpan.FromSeconds(5).Ticks, at.UtcTicks, 1);

        Assert.Equal(100, ResourceSampler.CpuPercent(previous, T0, current, at));
    }

    [Fact]
    public void A_counter_reset_or_a_stale_baseline_yields_no_value()
    {
        var previous = new CpuCounter(50e9, 100e9, 2);

        Assert.Null(ResourceSampler.CpuPercent(previous, T0, new CpuCounter(1e9, 106e9, 2), T0.AddSeconds(3)));
        Assert.Null(ResourceSampler.CpuPercent(previous, T0, new CpuCounter(51e9, 100e9, 2), T0.AddSeconds(3)));
        Assert.Null(ResourceSampler.CpuPercent(previous, T0, new CpuCounter(51e9, 200e9, 2), T0 + ResourceSampler.MaxBaselineAge + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Stats_payload_excludes_page_cache_cgroup_v2()
    {
        using var document = JsonDocument.Parse("""
            {"cpu_stats":{"cpu_usage":{"total_usage":123},"system_cpu_usage":456,"online_cpus":8},
             "precpu_stats":{"cpu_usage":{"total_usage":0}},
             "memory_stats":{"usage":104857600,"stats":{"inactive_file":4857600}}}
            """);

        var stats = DockerClient.ParseStats(document.RootElement);

        Assert.Equal(new CpuCounter(123, 456, 8), stats.Cpu);
        Assert.Equal(100_000_000, stats.MemoryBytes);
    }

    [Fact]
    public void Stats_payload_cgroup_v1_and_a_stopped_container()
    {
        using var v1 = JsonDocument.Parse("""
            {"cpu_stats":{"cpu_usage":{"total_usage":1,"percpu_usage":[1,0]},"system_cpu_usage":2},
             "memory_stats":{"usage":3000,"stats":{"total_inactive_file":1000}}}
            """);
        using var stopped = JsonDocument.Parse("""{"cpu_stats":{"cpu_usage":{"total_usage":0}},"memory_stats":{}}""");

        var parsed = DockerClient.ParseStats(v1.RootElement);
        Assert.Equal(2, parsed.Cpu!.Value.Cores);
        Assert.Equal(2000, parsed.MemoryBytes);
        Assert.Equal(new ContainerStats(null, null), DockerClient.ParseStats(stopped.RootElement));
    }

    [Fact]
    public async Task First_sample_has_no_cpu_the_second_has_and_a_dead_pid_yields_nulls()
    {
        using var docker = new DockerClient(Options.Create(new PortalOptions()));
        var history = new MetricsHistory();
        var sampler = new ResourceSampler(docker, history, TimeProvider.System);
        using var self = Process.GetCurrentProcess();
        var me = new ProcessDetails(self.Id, self.ProcessName, null, null, null, self.StartTime);
        var dead = new ProcessDetails(int.MaxValue - 1, "gone", null, null, null, null);
        var owner = ResourceSampler.ProcessOwner(me.Pid, me.StartTime);

        var first = await sampler.SampleAsync([me, dead], [], CancellationToken.None);
        await Task.Delay(50);
        var second = await sampler.SampleAsync([me], [], CancellationToken.None);

        Assert.Null(first[owner].CpuPercent);
        Assert.True(first[owner].MemoryBytes > 0);
        Assert.Equal(new ResourceSample(null, null), first[ResourceSampler.ProcessOwner(dead.Pid, null)]);
        Assert.NotNull(second[owner].CpuPercent);
        Assert.Single(history.Cpu(owner));
        Assert.Equal(2, history.Memory(owner).Count);
        Assert.Empty(history.Memory(ResourceSampler.ProcessOwner(dead.Pid, null)));
    }

    [Fact]
    public void A_reused_pid_is_a_different_owner()
    {
        Assert.NotEqual(ResourceSampler.ProcessOwner(42, T0), ResourceSampler.ProcessOwner(42, T0.AddMinutes(1)));
    }
}
