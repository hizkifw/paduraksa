using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.ProxyProtocol;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Net;

/// <summary>The per-address connection table: the cap, the release, the address keys (an IPv6 /64, IPv4-mapped as IPv4), a full
/// table failing open, and the counts staying balanced under concurrency.</summary>
public sealed class ConnectionTableTests
{
    private static ulong Hash(string address) => RateLimitPartition.Shared.ForIp(IpKey.Parse(address));

    [Fact]
    public void TheCapRefusesTheNextConnectionAndAReleaseMakesRoom()
    {
        ConnectionTable table = new(64);
        var hash = Hash("203.0.113.5");

        Assert.Equal(ConnectionSlot.Acquired, table.TryAcquire(hash, 2));
        Assert.Equal(ConnectionSlot.Acquired, table.TryAcquire(hash, 2));
        Assert.Equal(ConnectionSlot.OverCap, table.TryAcquire(hash, 2));
        Assert.Equal(2, table.Count(hash));

        table.Release(hash);
        Assert.Equal(ConnectionSlot.Acquired, table.TryAcquire(hash, 2));
        Assert.Equal(2, table.Count(hash));
    }

    [Fact]
    public void AddressesAreCountedApart()
    {
        ConnectionTable table = new(64);
        Assert.Equal(ConnectionSlot.Acquired, table.TryAcquire(Hash("203.0.113.5"), 1));
        Assert.Equal(ConnectionSlot.Acquired, table.TryAcquire(Hash("203.0.113.6"), 1));
        Assert.Equal(ConnectionSlot.OverCap, table.TryAcquire(Hash("203.0.113.5"), 1));
        Assert.Equal(2, table.TotalCount());
    }

    [Theory]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2:ffff:ffff:ffff:ffff", true)]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:3::1", false)]
    [InlineData("203.0.113.5", "::ffff:203.0.113.5", true)]
    [InlineData("203.0.113.5", "203.0.113.6", false)]
    public void AnIpv6Slash64IsOneAddressAndAnIpv4MappedAddressIsItsIpv4(string first, string second, bool sameKey)
    {
        ConnectionTable table = new(64);
        Assert.Equal(ConnectionSlot.Acquired, table.TryAcquire(Hash(first), 1));
        Assert.Equal(sameKey ? ConnectionSlot.OverCap : ConnectionSlot.Acquired, table.TryAcquire(Hash(second), 1));
    }

    [Fact]
    public void AFullWindowFailsOpenAndLeavesNothingToRelease()
    {
        // The smallest table is one probe window: every key sees the same eight entries.
        ConnectionTable table = new(ConnectionTable.ProbeLength);
        List<ulong> held = [];
        for (var i = 1; held.Count < ConnectionTable.ProbeLength; i++)
        {
            var hash = Hash($"198.51.100.{i}");
            Assert.Equal(ConnectionSlot.Acquired, table.TryAcquire(hash, 4));
            held.Add(hash);
        }

        var newcomer = Hash("192.0.2.77");
        Assert.Equal(ConnectionSlot.Overflow, table.TryAcquire(newcomer, 1));
        Assert.Equal(ConnectionSlot.Overflow, table.TryAcquire(newcomer, 1));
        Assert.Equal(ConnectionTable.ProbeLength, table.TotalCount());

        // An entry that holds nothing any more is taken over: the table doesn't stay full of idle addresses.
        table.Release(held[0]);
        Assert.Equal(ConnectionSlot.Acquired, table.TryAcquire(newcomer, 1));
        Assert.Equal(ConnectionSlot.OverCap, table.TryAcquire(newcomer, 1));
    }

    [Fact]
    public void AnIdleAddressKeepsItsEntryBetweenConnections()
    {
        ConnectionTable table = new(ConnectionTable.ProbeLength);
        var hash = Hash("203.0.113.5");
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(ConnectionSlot.Acquired, table.TryAcquire(hash, 1));
            Assert.Equal(ConnectionSlot.OverCap, table.TryAcquire(hash, 1));
            table.Release(hash);
        }

        Assert.Equal(0, table.TotalCount());
    }

    [Fact]
    public void ReleasingMoreThanWasAcquiredNeverGoesBelowZero()
    {
        ConnectionTable table = new(64);
        var hash = Hash("203.0.113.5");
        table.Release(hash);
        Assert.Equal(ConnectionSlot.Acquired, table.TryAcquire(hash, 1));
        table.Release(hash);
        table.Release(hash);
        Assert.Equal(0, table.Count(hash));
        Assert.Equal(ConnectionSlot.Acquired, table.TryAcquire(hash, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(100)]
    public void TheSizeIsAPowerOfTwoFromOneWindow(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectionTable(capacity));

    [Fact]
    public async Task ThreadsHoldingAndReleasingOneAddressNeverPassTheCapAndEndAtZero()
    {
        ConnectionTable table = new(1_024);
        var hash = Hash("203.0.113.5");
        const int Cap = 5;
        var holding = 0;
        var worst = 0;
        var accepted = 0L;

        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                if (table.TryAcquire(hash, Cap) != ConnectionSlot.Acquired)
                {
                    continue;
                }

                var now = Interlocked.Increment(ref holding);
                int seen;
                while (now > (seen = Volatile.Read(ref worst)) && Interlocked.CompareExchange(ref worst, now, seen) != seen)
                {
                }

                Interlocked.Increment(ref accepted);
                Interlocked.Decrement(ref holding);
                table.Release(hash);
            }
        }, TestContext.Current.CancellationToken)).ToArray();
        await Task.WhenAll(workers);

        Assert.InRange(worst, 1, Cap);
        Assert.True(accepted > 0);
        Assert.Equal(0, table.TotalCount());
        Assert.Equal(0, table.Count(hash));
    }

    [Fact]
    public async Task ThreadsOnManyAddressesLeaveTheTableBalancedWhateverTheOverflow()
    {
        // 16 entries for 200 addresses: most acquisitions overflow, some take over idle entries, and none may unbalance a count.
        ConnectionTable table = new(16);
        var hashes = Enumerable.Range(1, 200).Select(i => Hash($"198.51.100.{i}")).ToArray();

        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            var random = new Random(worker);
            for (var i = 0; i < 20_000; i++)
            {
                var hash = hashes[random.Next(hashes.Length)];
                if (table.TryAcquire(hash, 3) == ConnectionSlot.Acquired)
                {
                    table.Release(hash);
                }
            }
        }, TestContext.Current.CancellationToken)).ToArray();
        await Task.WhenAll(workers);

        Assert.Equal(0, table.TotalCount());
    }

    [Fact]
    public void AcquiringAndReleasingAllocatesNothing()
    {
        ConnectionTable table = new(1_024);
        var held = Hash("203.0.113.5");
        var other = Hash("198.51.100.1");
        Assert.Equal(ConnectionSlot.Acquired, table.TryAcquire(held, 2));

        AllocAssert.Zero((table, held, other), static s =>
        {
            _ = s.table.TryAcquire(s.other, 2);
            _ = s.table.Count(s.other);
            s.table.Release(s.other);
        }, "acquire, count and release");
        AllocAssert.Zero((table, held), static s =>
        {
            _ = s.table.TryAcquire(s.held, 2);
            _ = s.table.TryAcquire(s.held, 2);
            _ = s.table.TryAcquire(s.held, 2);
            s.table.Release(s.held);
            s.table.Release(s.held);
        }, "the refused path");
    }
}
