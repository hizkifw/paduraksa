using System.Net;

using Paduraksa.Admin.Auth;

namespace Paduraksa.Tests.Unit.Admin;

public sealed class AdminSourcePolicyTests
{
    [Fact]
    public void WithoutAnAllowlistEveryoneMayConnect()
    {
        AdminSourcePolicy policy = new([]);

        Assert.False(policy.IsRestricted);
        Assert.True(policy.IsAllowed(IPAddress.Parse("203.0.113.9")));
        Assert.True(policy.IsAllowed(null));
    }

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.77", true)]
    [InlineData("192.168.1.0/24", "192.168.2.1", false)]
    [InlineData("10.1.2.3", "10.1.2.3", true)]
    [InlineData("10.1.2.3", "10.1.2.4", false)]
    [InlineData("fd00::/8", "fd12:3456::1", true)]
    [InlineData("fd00::/8", "fe80::1", false)]
    [InlineData("::1", "::1", true)]
    public void AClientMustBeInsideABlock(string block, string client, bool allowed)
    {
        AdminSourcePolicy policy = new([block]);

        Assert.Equal(allowed, policy.IsAllowed(IPAddress.Parse(client)));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void LoopbackIsNotImplied(string client)
    {
        // A proxy on this machine would make every client look like loopback, so once there is an allowlist it must be listed.
        Assert.False(new AdminSourcePolicy(["203.0.113.0/24"]).IsAllowed(IPAddress.Parse(client)));
        Assert.True(new AdminSourcePolicy(["127.0.0.1/32", "::1/128"]).IsAllowed(IPAddress.Parse(client)));
    }

    [Fact]
    public void AnIPv4MappedIPv6ClientIsMatchedAsIPv4()
    {
        AdminSourcePolicy policy = new(["192.168.1.0/24"]);

        Assert.True(policy.IsAllowed(IPAddress.Parse("::ffff:192.168.1.5")));
        Assert.False(policy.IsAllowed(IPAddress.Parse("::ffff:192.168.9.5")));
    }

    [Fact]
    public void AnUnknownAddressIsRefusedOnceThereIsAnAllowlist() =>
        Assert.False(new AdminSourcePolicy(["10.0.0.0/8"]).IsAllowed(null));

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("10.0.0.0/33")]
    [InlineData("::1/129")]
    [InlineData("10.0.0.0/x")]
    [InlineData("::ffff:10.0.0.0/104")]
    public void ABadEntryIsRejected(string entry)
    {
        Assert.False(AdminSourcePolicy.TryParseBlock(entry, out _));
        Assert.Throws<FormatException>(() => new AdminSourcePolicy([entry]));
    }
}
