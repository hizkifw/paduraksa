using Paduraksa.Acme.Dns;

using static Paduraksa.Tests.Acme.Fakes.AcmeHarness;

namespace Paduraksa.Tests.Acme.Dns;

public sealed class CnameDelegationTests : IDisposable
{
    private const string Challenge = "_acme-challenge.example.com";

    private readonly FakeDnsServer _resolver = new();

    public void Dispose() => _resolver.Dispose();

    private CnameDelegation Delegation(params FakeDnsServer[] resolvers) =>
        new(new DnsPropagationOptions { Resolvers = [.. (resolvers.Length == 0 ? [_resolver] : resolvers).Select(r => r.EndPoint)], QueryTimeout = TimeSpan.FromMilliseconds(300) });

    private void Cname(string name, string target) => _resolver.Add(name, 5, DnsMessages.Name(target));

    [Fact]
    public async Task ANameThatIsNoCnameStaysWhereItIs()
    {
        Assert.Equal(Challenge, await Delegation().ResolveAsync(Challenge, Token));
        Assert.Equal(Challenge, await Delegation().ResolveAsync("_ACME-challenge.Example.com.", Token));
        Assert.All(_resolver.Queries, q => Assert.True(q.RecursionDesired && q.Type == 5));
    }

    [Fact]
    public async Task ACnameIsFollowedToTheNameTheRecordGoesTo()
    {
        Cname(Challenge, "token.acme.dns-zone.test");

        Assert.Equal("token.acme.dns-zone.test", await Delegation().ResolveAsync(Challenge, Token));
    }

    [Fact]
    public async Task AChainIsFollowedToItsEnd()
    {
        Cname(Challenge, "a.zone-one.test");
        Cname("a.zone-one.test", "b.zone-two.test");

        Assert.Equal("b.zone-two.test", await Delegation().ResolveAsync(Challenge, Token));
    }

    [Fact]
    public async Task ALoopIsAPermanentFailureNamingIt()
    {
        Cname(Challenge, "a.zone.test");
        Cname("a.zone.test", Challenge);

        var failure = await Assert.ThrowsAsync<DnsProviderException>(async () => await Delegation().ResolveAsync(Challenge, Token));

        Assert.False(failure.IsTransient);
        Assert.Contains("loops", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChainLongerThanTheCasFollowIsAPermanentFailure()
    {
        Cname(Challenge, "h0.zone.test");
        for (var i = 0; i < CnameDelegation.MaxHops + 1; i++)
        {
            Cname($"h{i}.zone.test", $"h{i + 1}.zone.test");
        }

        var failure = await Assert.ThrowsAsync<DnsProviderException>(async () => await Delegation().ResolveAsync(Challenge, Token));

        Assert.False(failure.IsTransient);
        Assert.Contains("longer than", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeadResolverIsSkippedAndNoResolverMeansNoDelegation()
    {
        Cname(Challenge, "token.acme.dns-zone.test");
        using FakeDnsServer silent = new() { Override = _ => [] };

        Assert.Equal("token.acme.dns-zone.test", await Delegation(silent, _resolver).ResolveAsync(Challenge, Token));
        Assert.Equal(Challenge, await Delegation(silent).ResolveAsync(Challenge, Token));
    }
}
