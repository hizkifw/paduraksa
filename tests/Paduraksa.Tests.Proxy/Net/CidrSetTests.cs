using Paduraksa.Proxy.Net;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Net;

public sealed class CidrSetTests
{
    [Theory]
    [InlineData("10.0.0.0/8", "10.0.0.0", 8, true)]
    [InlineData("0.0.0.0/0", "0.0.0.0", 0, true)]
    [InlineData("192.0.2.1", "192.0.2.1", 32, true)]
    [InlineData("192.0.2.1/32", "192.0.2.1", 32, true)]
    [InlineData("2001:db8::/32", "2001:db8::", 32, false)]
    [InlineData("::/0", "::", 0, false)]
    [InlineData("::1", "::1", 128, false)]
    [InlineData("::ffff:10.0.0.0/104", "10.0.0.0", 8, true)]
    [InlineData("::ffff:0:0/96", "0.0.0.0", 0, true)]
    [InlineData("2001:DB8:0::/48", "2001:db8::", 48, false)]
    public void ParsesBlocks(string text, string first, int prefix, bool isIPv4)
    {
        var network = IpNetwork.Parse(text);
        Assert.Equal(IpKey.Parse(first), network.First);
        Assert.Equal(prefix, network.PrefixLength);
        Assert.Equal(isIPv4, network.IsIPv4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/8")]
    [InlineData("10.0.0.0/")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/08")]
    [InlineData("10.0.0.0/+8")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/8/8")]
    [InlineData("10.0.0.0/ 8")]
    [InlineData("10.0.0.0 /8")]
    [InlineData("10.0.0.1/8")]
    [InlineData("10.0.0/8")]
    [InlineData("2001:db8::/129")]
    [InlineData("2001:db8::/0032")]
    [InlineData("2001:db8::1/32")]
    [InlineData("[2001:db8::]/32")]
    [InlineData("fe80::%eth0/64")]
    [InlineData("::ffff:10.0.0.0/8")]
    public void RejectsInvalidBlocks(string text)
    {
        Assert.False(IpNetwork.TryParse(text, out _, out var error));
        Assert.NotNull(error);
        Assert.Throws<FormatException>(() => new CidrSet.Builder().Add(text));
        Assert.False(new CidrSet.Builder().TryAdd(text));
    }

    [Fact]
    public void ToStringUsesTheFamilyPrefix()
    {
        Assert.Equal("10.0.0.0/8", IpNetwork.Parse("10.0.0.0/8").ToString());
        Assert.Equal("10.0.0.0/8", IpNetwork.Parse("::ffff:10.0.0.0/104").ToString());
        Assert.Equal("2001:db8::/32", IpNetwork.Parse("2001:db8::/32").ToString());
        Assert.Equal(IpNetwork.Parse("10.0.0.0/8"), IpNetwork.Create(IpKey.Parse("10.1.2.3"), 8));
    }

    [Fact]
    public void ContainsMixedFamilies()
    {
        var set = CidrSet.Parse(["10.0.0.0/8", "192.168.1.0/24", "2001:db8::/32", "203.0.113.7"]);

        Assert.True(set.Contains(IpKey.Parse("10.255.255.255")));
        Assert.True(set.Contains(IpKey.Parse("::ffff:10.0.0.1")));
        Assert.True(set.Contains(IpKey.Parse("192.168.1.200")));
        Assert.True(set.Contains(IpKey.Parse("2001:db8:ffff::1")));
        Assert.True(set.Contains(IpKey.Parse("203.0.113.7")));

        Assert.False(set.Contains(IpKey.Parse("11.0.0.0")));
        Assert.False(set.Contains(IpKey.Parse("9.255.255.255")));
        Assert.False(set.Contains(IpKey.Parse("192.168.2.1")));
        Assert.False(set.Contains(IpKey.Parse("2001:db9::")));
        Assert.False(set.Contains(IpKey.Parse("203.0.113.8")));
        Assert.False(set.Contains(IpKey.Parse("::10.0.0.1")));
        Assert.False(set.Contains(IpKey.Unspecified));
    }

    [Fact]
    public void IPv4AnyCoversOnlyIPv4AndIPv6AnyCoversEverything()
    {
        var v4Any = CidrSet.Parse(["0.0.0.0/0"]);
        Assert.True(v4Any.Contains(IpKey.Parse("255.255.255.255")));
        Assert.True(v4Any.Contains(IpKey.Parse("0.0.0.0")));
        Assert.False(v4Any.Contains(IpKey.Parse("::1")));
        Assert.False(v4Any.Contains(IpKey.Parse("2001:db8::1")));

        var all = CidrSet.Parse(["::/0"]);
        Assert.True(all.Contains(IpKey.Parse("1.2.3.4")));
        Assert.True(all.Contains(IpKey.Parse("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")));
        Assert.True(all.Contains(IpKey.Unspecified));
        Assert.Equal(1, all.RangeCount);
    }

    [Fact]
    public void MergesOverlappingNestedAndAdjacentBlocks()
    {
        var set = CidrSet.Parse([
            "10.0.0.0/24", "10.0.1.0/24", // adjacent → 10.0.0.0/23
            "10.0.0.128/25",              // nested
            "10.0.0.0/24",                // duplicate
            "172.16.0.0/12", "172.20.0.0/16", // nested
            "2001:db8::/33", "2001:db8:8000::/33", // adjacent → /32
            "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ff00/120", "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", // top of space
        ]);

        var ranges = set.GetRanges();
        Assert.Equal(4, ranges.Count);
        Assert.Equal((IpKey.Parse("10.0.0.0"), IpKey.Parse("10.0.1.255")), ranges[0]);
        Assert.Equal((IpKey.Parse("172.16.0.0"), IpKey.Parse("172.31.255.255")), ranges[1]);
        Assert.Equal((IpKey.Parse("2001:db8::"), IpKey.Parse("2001:db8:ffff:ffff:ffff:ffff:ffff:ffff")), ranges[2]);
        Assert.Equal(IpKey.Parse("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"), ranges[3].Last);
    }

    [Fact]
    public void DefaultNetworkIsTheUnspecifiedAddressNotEverything()
    {
        var network = default(IpNetwork);
        Assert.Equal(128, network.Prefix128);
        Assert.Equal(IpKey.Unspecified, network.Last);
        Assert.True(network.Contains(IpKey.Unspecified));
        Assert.False(network.Contains(IpKey.Parse("1.2.3.4")));
        Assert.False(network.Contains(IpKey.Parse("2001:db8::1")));
        Assert.Equal("::/128", network.ToString());

        var set = new CidrSet.Builder().Add(default(IpNetwork)).Build();
        Assert.False(set.Contains(IpKey.Parse("1.2.3.4")));
        Assert.True(set.Contains(IpKey.Unspecified));

        // An explicit ::/0 still covers everything.
        Assert.Equal(0, IpNetwork.Parse("::/0").Prefix128);
        Assert.True(IpNetwork.Parse("::/0").Contains(IpKey.Parse("1.2.3.4")));
    }

    [Fact]
    public void EmptySetContainsNothing()
    {
        Assert.True(CidrSet.Empty.IsEmpty);
        Assert.False(CidrSet.Empty.Contains(IpKey.Parse("1.2.3.4")));
        Assert.Same(CidrSet.Empty, new CidrSet.Builder().Build());
    }

    [Fact]
    public void Fuzz_MatchesNaiveContains()
    {
        var random = new Random(1918);
        for (var round = 0; round < 50; round++)
        {
            var networks = new List<IpNetwork>();
            var builder = new CidrSet.Builder();
            var count = random.Next(1, 60);
            for (var i = 0; i < count; i++)
            {
                var network = random.Next(2) == 0
                    ? IpNetwork.Create(IpKey.FromIPv4((uint)random.NextInt64(0, 1L << 32)), random.Next(4, 33))
                    : IpNetwork.Create(new IpKey(new UInt128(0x2001_0db8_0000_0000UL | (uint)random.Next(1 << 16), (ulong)random.NextInt64())), random.Next(28, 129));
                networks.Add(network);
                builder.Add(network);
            }

            var set = builder.Build();
            for (var probe = 0; probe < 2_000; probe++)
            {
                // Probe near block boundaries as well as at random.
                var n = networks[random.Next(networks.Count)];
                var key = random.Next(4) switch
                {
                    0 => n.First,
                    1 => n.Last,
                    2 => new IpKey(n.Last.Value + 1),
                    _ => new IpKey(n.First.Value - 1),
                };

                var expected = false;
                foreach (var candidate in networks)
                {
                    expected |= candidate.Contains(key);
                }

                Assert.Equal(expected, set.Contains(key));
            }
        }
    }

    [Fact]
    public void ContainsIsAllocationFree()
    {
        var builder = new CidrSet.Builder();
        for (var i = 0; i < 2_000; i++)
        {
            builder.Add(IpNetwork.Create(IpKey.FromIPv4((uint)i << 12), 20));
        }

        var state = (Set: builder.Build(), Keys: new[] { IpKey.Parse("10.0.0.1"), IpKey.Parse("2001:db8::1"), IpKey.Parse("0.0.48.1") });
        AllocAssert.Zero(state, static s =>
        {
            foreach (var key in s.Keys)
            {
                _ = s.Set.Contains(key);
            }
        }, "CidrSet.Contains");
    }
}
