using DevPortal.Discovery;

namespace DevPortal.Tests;

public sealed class MetricsHistoryTests
{
    [Fact]
    public void Ring_keeps_only_the_newest_samples_oldest_first()
    {
        var ring = new Ring<int>(3);
        for (var i = 1; i <= 5; i++) ring.Add(i);

        Assert.Equal([3, 4, 5], ring.ToList());
    }

    [Fact]
    public void Ring_stays_bounded_under_concurrent_writers()
    {
        var ring = new Ring<int>(60);
        Parallel.For(0, 8, _ => { for (var i = 0; i < 1000; i++) ring.Add(i); });

        Assert.Equal(60, ring.ToList().Count);
    }

    [Fact]
    public void A_gap_keeps_its_slot_and_a_new_owner_starts_clean()
    {
        var history = new MetricsHistory(5);
        history.RecordResponse(3000, "pid:1", 4.2);
        history.RecordResponse(3000, "pid:1", null);
        history.RecordResponse(3000, "pid:2", 9);

        Assert.Equal([4.2, null], history.Responses(3000, "pid:1"));
        Assert.Equal([9d], history.Responses(3000, "pid:2"));
    }

    [Fact]
    public void Retain_drops_history_of_gone_ports_only()
    {
        var history = new MetricsHistory();
        history.RecordResponse(3000, "pid:1", 1);
        history.RecordResponse(4000, "pid:2", 1);

        history.Retain(new HashSet<(int, string)> { (3000, "pid:1") });

        Assert.NotEmpty(history.Responses(3000, "pid:1"));
        Assert.Empty(history.Responses(4000, "pid:2"));
    }
}
