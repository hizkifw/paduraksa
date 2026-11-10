using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.RateLimiting;

public sealed class RateLimitAllocationTests
{
    private const ushort Slot = 7;

    [Fact]
    public void AHitAllocatesNothing()
    {
        Hit state = new(new(RateLimitTable.MinimumCapacity), new(1_000, RateLimitParameters.MaxBurst));
        AllocAssert.Zero(
            state,
            static s =>
            {
                s.Now += 16;
                _ = s.Table.TryAcquire(Slot, 0x42, s.Parameters, s.Now, out _);
            },
            "a bucket hit");
    }

    [Fact]
    public void AMissAllocatesNothing()
    {
        // Fewer calls than entries, so every call claims a slot instead of evicting one.
        Miss state = new(new(RateLimitTable.MinimumCapacity), new(1, 1));
        AllocAssert.Zero(
            state,
            static s =>
            {
                s.Index++;
                _ = s.Table.TryAcquire(Slot, (ulong)s.Index * 0x9E37_79B9_7F4A_7C15UL, s.Parameters, nowMs: 0, out _);
            },
            "a new bucket",
            warmup: 100,
            iterations: 500);
    }

    [Fact]
    public void AnEvictionAllocatesNothing()
    {
        // Every key starts its probe at index 0 of a 16-entry table, and 24 keys are cycled through its 8-slot window:
        // each call has to evict the oldest of them.
        Evict state = new(new(16), new(1, 1));
        AllocAssert.Zero(
            state,
            static s =>
            {
                s.Index++;
                _ = s.Table.TryAcquire(Slot, 0x1000 + ((ulong)s.Index & 15) * 16, s.Parameters, nowMs: 0, out _);
            },
            "an evicted bucket");
    }

    [Fact]
    public void ThePartitionHelpersAllocateNothing()
    {
        Partitions state = new(RateLimitPartition.Shared, "some-header-value", "/api/items");
        AllocAssert.Zero(
            state,
            static s =>
            {
                _ = s.Partition.ForIp(s.IPv4);
                _ = s.Partition.ForIp(s.IPv6);
                _ = s.Partition.ForIpAndPath(s.IPv4, s.Path);
                _ = s.Partition.ForHeader(s.Header);
            },
            "the partition hashes");
    }

    private sealed class Hit(RateLimitTable table, RateLimitParameters parameters)
    {
        public RateLimitTable Table { get; } = table;

        public RateLimitParameters Parameters { get; } = parameters;

        public long Now { get; set; }
    }

    private sealed class Miss(RateLimitTable table, RateLimitParameters parameters)
    {
        public RateLimitTable Table { get; } = table;

        public RateLimitParameters Parameters { get; } = parameters;

        public int Index { get; set; }
    }

    private sealed class Evict(RateLimitTable table, RateLimitParameters parameters)
    {
        public RateLimitTable Table { get; } = table;

        public RateLimitParameters Parameters { get; } = parameters;

        public int Index { get; set; }
    }

    private sealed class Partitions(RateLimitPartition partition, string header, string path)
    {
        public RateLimitPartition Partition { get; } = partition;

        public IpKey IPv4 { get; } = IpKey.Parse("192.0.2.77");

        public IpKey IPv6 { get; } = IpKey.Parse("2001:db8:1:2:3:4:5:6");

        public string Header { get; } = header;

        public string Path { get; } = path;
    }
}
