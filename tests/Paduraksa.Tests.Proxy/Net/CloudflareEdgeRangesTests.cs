using System.Net;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Simulation;
using Paduraksa.ProxyProtocol;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Net;

public sealed class CloudflareEdgeRangesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Good =
    [
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "104.16.0.0/13",
        "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32",
    ];

    private static EdgeRangeList Valid(params string[] extra)
    {
        Assert.True(EdgeRangeList.TryCreate([.. Good, .. extra], Now, out var list, out var reason), reason);
        return list;
    }

    [Fact]
    public void TheBuiltInListIsTheOneThisBuildShips()
    {
        var list = EdgeRangeList.BuiltIn;

        Assert.Same(CloudflareIpRanges.Default, list.Ranges);
        Assert.Equal(EdgeRangeOrigin.BuiltIn, list.Origin);
        Assert.Equal(CloudflareIpRanges.IPv4.Length + CloudflareIpRanges.IPv6.Length, list.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), list.FetchedAt);
    }

    [Fact]
    public void AFirstFetchIdenticalToTheBuiltInListComparesEqualAndIsLabelledFetched()
    {
        var builtIn = EdgeRangeList.BuiltIn;
        Assert.Equal(builtIn.Blocks.Order(StringComparer.Ordinal), builtIn.Blocks);

        // Listed in the order Cloudflare publishes them, which isn't the sorted one.
        Assert.True(EdgeRangeList.TryCreate([.. CloudflareIpRanges.IPv4, .. CloudflareIpRanges.IPv6], Now, out var fetched, out _));
        Assert.True(builtIn.SameBlocksAs(fetched));

        var source = new CloudflareEdgeRangeSource(new Clock(Now));
        Assert.False(source.Replace(fetched), "the same ranges are no change");
        Assert.Equal(EdgeRangeOrigin.Fetched, source.Current.Origin);
        Assert.Equal(Now, source.Current.FetchedAt);
    }

    [Fact]
    public void AGoodListKeepsItsBlocksSortedWithoutDuplicates()
    {
        var list = Valid(" 104.16.0.0/13 ", "2400:cb00::/32");

        Assert.Equal(Good.Length, list.Count);
        Assert.Equal(EdgeRangeOrigin.Fetched, list.Origin);
        Assert.Equal(Now, list.FetchedAt);
        Assert.Equal(list.Blocks.Order(StringComparer.Ordinal), list.Blocks);
        Assert.True(list.Ranges.Contains(IpKey.Parse("104.16.9.9")));
        Assert.True(list.Ranges.Contains(IpKey.Parse("2606:4700::1111")));
        Assert.False(list.Ranges.Contains(IpKey.Parse("8.8.8.8")));
    }

    public static TheoryData<string[], string> Refused => new()
    {
        { [.. Good, "not-a-network/24"], "not a valid CIDR block" },
        { [.. Good, "1.2.3.4"], "no prefix length" },
        { [.. Good, "10.0.0.1/8"], "host bits" },
        { [.. Good, "1.2.3.0/33"], "invalid prefix length" },
        { [.. Good, ""], "no prefix length" },
        { [.. Good, "0.0.0.0/0"], "wider than /8" },
        { [.. Good, "64.0.0.0/7"], "wider than /8" },
        { [.. Good, "::/0"], "wider than /12" },
        { [.. Good, "2000::/11"], "wider than /12" },
        { [.. Good, "::ffff:0:0/96"], "wider than /8" },
        { ["173.245.48.0/20", "103.21.244.0/22", "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32"], "2 IPv4 and 3 IPv6" },
        { [.. Good.Where(b => b.Contains(':', StringComparison.Ordinal))], "0 IPv4" },
        { [.. Good.Where(b => !b.Contains(':', StringComparison.Ordinal))], "0 IPv6" },
        { [], "0 IPv4 and 0 IPv6" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void ASuspiciousOrIncompleteListIsRefusedWhole(string[] blocks, string reason)
    {
        Assert.False(EdgeRangeList.TryCreate(blocks, Now, out var list, out var why));
        Assert.Null(list);
        Assert.Contains(reason, why, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsurdlyLongListIsRefused()
    {
        var many = Enumerable.Range(0, EdgeRangeList.MaximumEntries + 1)
            .Select(i => $"{16 + (i / 256)}.{i % 256}.0.0/16")
            .Concat(Good.Where(b => b.Contains(':', StringComparison.Ordinal)));

        Assert.False(EdgeRangeList.TryCreate(many, Now, out _, out var why));
        Assert.Contains("more than", why, StringComparison.Ordinal);
    }

    [Fact]
    public void AReasonCutsWhatCameFromTheNetwork()
    {
        Assert.False(EdgeRangeList.TryCreate([.. Good, new string('x', 500)], Now, out _, out var why));
        Assert.True(why.Length < 200, why);
    }

    [Theory]
    [InlineData("1.2.3.0/24\u001b[31m")]
    [InlineData("1.2.3.0/24\r\nFATAL forged line")]
    [InlineData("1.2.3.0/24\u202eevil")]
    [InlineData("1.2.3.0/24\u2028next")]
    public void AReasonHasNoControlOrLineBreakingCharacterOfTheResponse(string entry)
    {
        // Found by the cloudflare-ranges fuzz target: the reason is logged and shown, so it must stay one printable line.
        Assert.False(EdgeRangeList.TryCreate([.. Good, entry], Now, out _, out var why));
        Assert.False(why.Any(static c => char.IsControl(c) || c is '\u202e' or '\u2028'), why);
        Assert.Contains("?", why, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacingWithTheSameBlocksOnlyMovesTheAge()
    {
        var source = new CloudflareEdgeRangeSource(new Clock(Now));
        Assert.Same(EdgeRangeList.BuiltIn, source.Current);

        var first = Valid();
        Assert.True(source.Replace(first), "a list that differs from the built-in one is a change");
        Assert.Same(first, source.Current);

        EdgeRangeList.TryCreate(Good, Now.AddDays(1), out var again, out _);
        Assert.False(source.Replace(again!), "the same blocks are no change");
        Assert.Same(again, source.Current);

        EdgeRangeList.TryCreate([.. Good, "141.101.64.0/18"], Now.AddDays(2), out var grown, out _);
        Assert.True(source.Replace(grown!));
    }

    [Fact]
    public void AListIsStaleAfterThirtyDaysAndSeveralFailuresInARowWarn()
    {
        var clock = new Clock(Now);
        var source = new CloudflareEdgeRangeSource(clock);
        source.Replace(Valid());

        Assert.Null(source.Status().Warning);
        clock.Now = Now.AddDays(30);
        Assert.Null(source.Status().Warning);
        clock.Now = Now.AddDays(31);
        var stale = source.Status();
        Assert.True(stale.Stale);
        Assert.Contains("31 days old", stale.Warning, StringComparison.Ordinal);
        Assert.Contains("stored list", stale.Warning, StringComparison.Ordinal);

        // Fresh again, but failing: N in a row, never fewer.
        clock.Now = Now;
        source.RecordFailure("timed out");
        source.RecordFailure("timed out");
        Assert.Null(source.Status().Warning);
        source.RecordFailure("HTTP 500");
        var failing = source.Status();
        Assert.True(failing.Failing);
        Assert.Contains("refreshes keep failing (HTTP 500)", failing.Warning, StringComparison.Ordinal);

        // A good refresh clears it.
        source.Replace(Valid());
        Assert.Null(source.Status().Warning);
        Assert.Equal(0, source.ConsecutiveFailures);
        Assert.Null(source.LastFailure);
    }

    [Fact]
    public void TheBuiltInListGrowsStaleToo()
    {
        var source = new CloudflareEdgeRangeSource(new Clock(EdgeRangeList.BuiltIn.FetchedAt.AddDays(45)));

        Assert.Contains("built-in list, 45 days old", source.Status().Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCompilerReadsTheLiveListAtEveryCompileAndTheSnapshotSaysWhichOne()
    {
        TrustedProxySpec spec = new() { Mode = TrustedProxyMode.CloudflareProxy };
        var source = new CloudflareEdgeRangeSource(new Clock(Now));
        var options = new SnapshotCompilerOptions { CloudflareRanges = source };
        ProxyConfigModel model = new() { Defaults = new DefaultsSpec { TrustedProxies = spec } };

        var builtIn = Compile(model, options);
        Assert.Same(CloudflareIpRanges.Default, builtIn.TrustedProxies.TrustedPeers);
        Assert.Same(EdgeRangeList.BuiltIn, builtIn.EdgeRanges);

        var fetched = Valid("192.0.2.0/24");
        source.Replace(fetched);
        var after = Compile(model, options);
        Assert.Same(fetched.Ranges, after.TrustedProxies.TrustedPeers);
        Assert.Same(fetched, after.EdgeRanges);
        Assert.True(after.TrustedProxies.IsTrustedPeer(IpKey.Parse("192.0.2.7")));
        Assert.False(builtIn.TrustedProxies.IsTrustedPeer(IpKey.Parse("192.0.2.7")), "a snapshot keeps the list it was compiled with");

        // A fixed set wins over the live list.
        var fixedSet = CidrSet.Parse(["127.0.0.0/8"]);
        var pinned = Compile(model, options with { CloudflareEdgeRanges = fixedSet });
        Assert.Same(fixedSet, pinned.TrustedProxies.TrustedPeers);
        Assert.Equal(EdgeRangeOrigin.Configured, pinned.EdgeRanges!.Origin);
    }

    [Theory]
    [InlineData(TrustedProxyMode.Off)]
    [InlineData(TrustedProxyMode.Generic)]
    [InlineData(TrustedProxyMode.CloudflareTunnel)]
    public void OnlyTheProxyModeTrustsTheEdgeRanges(TrustedProxyMode mode)
    {
        ProxyConfigModel model = new() { Defaults = new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = mode, Proxies = mode == TrustedProxyMode.Off ? [] : ["10.0.0.0/8"] } } };

        Assert.Null(Compile(model, new SnapshotCompilerOptions { CloudflareRanges = new CloudflareEdgeRangeSource() }).EdgeRanges);
    }

    [Fact]
    public void TheSimulatorSaysWhichListIsInUseAndHowOldItIsOnlyWhereTheEdgeIsTrusted()
    {
        var source = new CloudflareEdgeRangeSource(new Clock(Now));
        var options = new SnapshotCompilerOptions { CloudflareRanges = source };
        var model = new ProxyConfigModel { Defaults = new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy } } };
        var probe = new GateProbe { Host = "any.test", ClientAddress = IPAddress.Parse("198.51.100.7") };

        static string Fact(GateSimulation run) =>
            Assert.Single(Assert.Single(run.Steps, s => s.Kind == GateStepKind.ClientAddress).Facts, f => f.Name == "edgeRanges").Value;

        Assert.Equal(
            "built-in list, 22 ranges, fetched 2026-11-06 (5 days ago)",
            Fact(GateSimulator.Run(Bound(model, options: options), probe, now: Now)));

        source.Replace(Valid("192.0.2.0/24"));
        Assert.Equal(
            "fetched list, 8 ranges, fetched 2026-11-11 (0 days ago)",
            Fact(GateSimulator.Run(Bound(model, options: options), probe, now: Now)));

        // Without a clock the age isn't stated; with the mode off the step doesn't mention the list at all.
        Assert.Equal("fetched list, 8 ranges, fetched 2026-11-11", Fact(GateSimulator.Run(Bound(model, options: options), probe)));
        var off = GateSimulator.Run(Bound(new ProxyConfigModel(), options: options), probe, now: Now);
        Assert.DoesNotContain(Assert.Single(off.Steps, s => s.Kind == GateStepKind.ClientAddress).Facts, f => f.Name == "edgeRanges");
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
