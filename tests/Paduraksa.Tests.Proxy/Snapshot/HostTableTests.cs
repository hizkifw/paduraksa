using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Proxy.Snapshot;

public sealed class HostTableTests
{
    private static readonly CompiledHost Exact = Host("exact");
    private static readonly CompiledHost Wild = Host("wild");
    private static readonly CompiledHost Apex = Host("apex");
    private static readonly CompiledHost Literal6 = Host("literal6");
    private static readonly CompiledHost Literal4 = Host("literal4");
    private static readonly CompiledHost Idn = Host("idn");

    private static readonly HostTable Table = Build(
        ("app.example.com", Exact),
        ("*.wild.test", Wild),
        ("wild.test", Apex),
        ("[2001:DB8:0::1]", Literal6),
        ("192.0.2.10", Literal4),
        ("bücher.test", Idn));

    [Theory]
    [InlineData("app.example.com")]
    [InlineData("APP.Example.COM")]
    [InlineData("app.example.com:8443")]
    [InlineData("app.example.com.")]
    [InlineData("app.example.com.:80")]
    public void ExactNamesMatchIgnoringCasePortAndTrailingDot(string header) =>
        Assert.Same(Exact, Table.Find(header));

    [Theory]
    [InlineData("a.wild.test")]
    [InlineData("A-b_c.WILD.test:443")]
    public void WildcardCoversExactlyOneLabel(string header) => Assert.Same(Wild, Table.Find(header));

    [Theory]
    [InlineData("a.b.wild.test")]
    [InlineData(".wild.test")]
    [InlineData("a@b.wild.test")]
    [InlineData("a/b.wild.test")]
    public void WildcardDoesNotCoverDeeperOrOddLabels(string header) => Assert.Null(Table.Find(header));

    [Fact]
    public void ApexIsItsOwnExactName() => Assert.Same(Apex, Table.Find("wild.test"));

    [Theory]
    [InlineData("[2001:db8::1]")]
    [InlineData("[2001:DB8:0:0:0:0:0:1]:8443")]
    [InlineData("[2001:db8::0:1]")]
    public void IPv6LiteralsMatchAnySpelling(string header) => Assert.Same(Literal6, Table.Find(header));

    [Theory]
    [InlineData("[2001:db8::2]")]
    [InlineData("[2001:db8::1")]
    [InlineData("[fe80::1%eth0]")]
    [InlineData("[]")]
    [InlineData("[192.0.2.10]")]
    public void OtherOrMalformedIPv6LiteralsMiss(string header) => Assert.Null(Table.Find(header));

    [Theory]
    [InlineData("192.0.2.10", true)]
    [InlineData("192.0.2.10:80", true)]
    [InlineData("192.000.002.010", false)]
    public void IPv4LiteralsMatchOnlyTheCanonicalForm(string header, bool matches) =>
        Assert.Equal(matches ? Literal4 : null, Table.Find(header));

    [Fact]
    public void IdnNamesAreStoredAsPunycode()
    {
        Assert.Same(Idn, Table.Find("xn--bcher-kva.test"));
        Assert.Null(Table.Find("bücher.test"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(":80")]
    [InlineData("example.com")]
    [InlineData("app.example.com..")]
    public void UnknownOrEmptyHostsMiss(string header) => Assert.Null(Table.Find(header));

    [Theory]
    [InlineData("a.test", "a.test")]
    [InlineData("a.test:80", "a.test")]
    [InlineData("a.test.", "a.test")]
    [InlineData("a.test.:80", "a.test")]
    [InlineData("[::1]", "[::1]")]
    [InlineData("[::1]:443", "[::1]")]
    [InlineData("[::1", "[::1")]
    [InlineData("", "")]
    public void HostNameStripsPortAndTrailingDot(string header, string expected) =>
        Assert.Equal(expected, HostTable.HostName(header).ToString());

    [Theory]
    [InlineData("a.test", "A.TEST")]
    [InlineData("a.test", "a.test.")]
    [InlineData("bücher.test", "xn--bcher-kva.test")]
    [InlineData("*.w.test", "*.W.test.")]
    [InlineData("[2001:db8::1]", "[2001:DB8:0:0::1]")]
    public void DuplicatesAfterNormalisationAreRejected(string first, string second)
    {
        HostTable.Builder builder = new();
        Assert.True(builder.TryAdd(first, Host("one"), out _));

        Assert.False(builder.TryAdd(second, Host("two"), out var error));
        Assert.Contains("duplicate host name", error, StringComparison.Ordinal);
        Assert.Contains($"'{TestIds.Host("one")}'", error, StringComparison.Ordinal);
        Assert.Contains($"'{TestIds.Host("two")}'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void WildcardAndApexAreDistinctNames()
    {
        HostTable.Builder builder = new();
        Assert.True(builder.TryAdd("*.w.test", Host("one"), out _));
        Assert.True(builder.TryAdd("w.test", Host("two"), out _));
    }

    [Fact]
    public void LookupDoesNotAllocate()
    {
        string[] inputs = ["app.example.com", "APP.EXAMPLE.COM:8443", "app.example.com.", "a.wild.test", "a.b.wild.test", "unknown.org", "192.0.2.10:80"];
        AllocAssert.Zero(
            (Table, inputs),
            static s =>
            {
                foreach (var input in s.inputs)
                {
                    _ = s.Table.Find(input);
                }
            },
            "HostTable.Find");
    }

    internal static CompiledHost Host(string label) => new()
    {
        Index = 0,
        Id = TestIds.Host(label),
        Kind = HostKind.Respond,
        Routes = [],
        Policies = [],
        FixedResponse = PrecomputedResponse.Text(200, label),
    };

    private static HostTable Build(params (string Name, CompiledHost Host)[] entries)
    {
        HostTable.Builder builder = new();
        foreach (var (name, host) in entries)
        {
            Assert.True(builder.TryAdd(name, host, out var error), error);
        }

        return builder.Build();
    }
}
