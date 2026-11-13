using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Tests.Proxy.RateLimiting;

public sealed class RateLimitPartitionTests
{
    private static readonly IpKey Client = IpKey.Parse("203.0.113.7");
    private static readonly IpKey OtherClient = IpKey.Parse("203.0.113.8");
    private static readonly IpKey ClientMapped = IpKey.Parse("::ffff:203.0.113.7");
    private static readonly IpKey IPv6 = IpKey.Parse("2001:db8:1:2::1");
    private static readonly IpKey IPv6SameSubnet = IpKey.Parse("2001:db8:1:2:ffff:ffff:ffff:ffff");
    private static readonly IpKey IPv6OtherSubnet = IpKey.Parse("2001:db8:1:3::1");

    [Fact]
    public void TheSameKeyHashesTheSameWithinAProcess()
    {
        RateLimitPartition partition = new(1);

        Assert.Equal(partition.ForIp(Client), partition.ForIp(Client));
        Assert.Equal(partition.ForIpAndPath(Client, "/api/items"), partition.ForIpAndPath(Client, "/api/items"));
        Assert.Equal(partition.ForHeader("token"), partition.ForHeader("token"));

        // The process's own partitioner is one instance with one seed.
        Assert.Equal(RateLimitPartition.Shared.ForIp(Client), RateLimitPartition.Shared.ForIp(Client));
    }

    [Fact]
    public void AnIPv6ClientIsKeyedByItsSixtyFour()
    {
        RateLimitPartition partition = new(1);

        Assert.Equal(partition.ForIp(IPv6), partition.ForIp(IPv6SameSubnet));
        Assert.NotEqual(partition.ForIp(IPv6), partition.ForIp(IPv6OtherSubnet));
        Assert.NotEqual(partition.ForIpAndPath(IPv6, "/"), partition.ForIpAndPath(IPv6OtherSubnet, "/"));
    }

    [Fact]
    public void AnIPv4ClientIsKeyedByItsWholeAddress()
    {
        RateLimitPartition partition = new(1);

        Assert.NotEqual(partition.ForIp(Client), partition.ForIp(OtherClient));
        // The same client on a dual-mode listener, which reports TCP peers as ::ffff:a.b.c.d.
        Assert.Equal(partition.ForIp(Client), partition.ForIp(ClientMapped));
    }

    [Fact]
    public void APathAndAHeaderArePartOfTheKey()
    {
        RateLimitPartition partition = new(1);

        Assert.NotEqual(partition.ForIpAndPath(Client, "/api"), partition.ForIpAndPath(Client, "/api/v1"));
        Assert.Equal(partition.ForIpAndPath(Client, "/api"), partition.ForIpAndPath(Client, "/API"));
        Assert.NotEqual(partition.ForHeader("a/b"), partition.ForHeader("a"));
        Assert.NotEqual(partition.ForHeader(string.Empty), partition.ForHeader("a"));
    }

    [Fact]
    public void TheThreeKindsAreDomainSeparated()
    {
        RateLimitPartition partition = new(1);
        var ip = partition.ForIp(Client);

        // A header that spells the client's address is not the client, and neither is a path of that text.
        Assert.NotEqual(ip, partition.ForHeader("203.0.113.7"));
        Assert.NotEqual(ip, partition.ForIpAndPath(Client, "203.0.113.7"));
        Assert.NotEqual(partition.ForHeader("203.0.113.7"), partition.ForIpAndPath(Client, "203.0.113.7"));
        Assert.NotEqual(partition.ForHeader(string.Empty), partition.ForIpAndPath(Client, string.Empty));
        Assert.NotEqual(ip, partition.ForIpAndPath(Client, string.Empty));
    }

    [Fact]
    public void AnotherSeedGivesAnotherKey()
    {
        RateLimitPartition first = new(1);
        RateLimitPartition second = new(2);

        Assert.NotEqual(first.ForIp(Client), second.ForIp(Client));
        Assert.NotEqual(first.ForIpAndPath(Client, "/"), second.ForIpAndPath(Client, "/"));
        Assert.NotEqual(first.ForHeader("token"), second.ForHeader("token"));
    }

    [Fact]
    public void AKeyIsFortyEightBits()
    {
        RateLimitPartition partition = new(1);

        Assert.InRange<ulong>(partition.ForIp(Client), 1, RateLimitPartition.HashMask);
        Assert.InRange<ulong>(partition.ForIpAndPath(IPv6, "/api"), 1, RateLimitPartition.HashMask);
        Assert.InRange<ulong>(partition.ForHeader("token"), 1, RateLimitPartition.HashMask);
        Assert.Equal((1UL << 48) - 1, RateLimitPartition.HashMask);
    }
}
