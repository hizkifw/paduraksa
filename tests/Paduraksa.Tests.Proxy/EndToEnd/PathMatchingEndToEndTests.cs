using Paduraksa.Proxy.Config;
using Paduraksa.Tests.Proxy.Testing;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

public sealed class PathMatchingEndToEndTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    private ProxyConfigModel Config => new()
    {
        Upstreams = [Upstream("ua", upstream.Address("a"))],
        Hosts =
        [
            ProxyHost(
                "guard",
                "guard.test",
                Route("guard-admin", "ua", "/admin") with { Policies = [new DenyPolicySpec(403, "denied\n")] },
                Route("guard-root", "ua")),
        ],
    };

    [Theory]
    [InlineData("/admin/x")]
    [InlineData("//admin/x")]
    [InlineData("/admin;a/x")]
    [InlineData("/admin\\x")]
    [InlineData("/admin%5Cx")]
    [InlineData("/admin%2Fx")]
    [InlineData("/admin%20")]
    [InlineData("/admin.")]
    [InlineData("/admin./x")]
    [InlineData("/ADMIN/x")]
    [InlineData("/%61dmin/x")]
    [InlineData("/adm%69n/x")]
    [InlineData("/x/../admin/y")]
    [InlineData("/x/%2e%2e/admin/y")]
    [InlineData("/x\\..\\admin")]
    public async Task EverySpellingOfAGuardedPathHitsItsPolicy(string target)
    {
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(Config);

        (int status, string body, _) = await RawHttp.GetAsync(server.BaseAddress, "guard.test", target, TestContext.Current.CancellationToken);

        Assert.Equal(403, status);
        Assert.Equal("denied\n", body);
    }

    [Theory]
    [InlineData("/administrator")]
    [InlineData("/adminx/y")]
    [InlineData("/x/admin")]
    [InlineData("/")]
    public async Task OtherPathsStillReachTheUpstream(string target)
    {
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(Config);

        (int status, _, _) = await RawHttp.GetAsync(server.BaseAddress, "guard.test", target, TestContext.Current.CancellationToken);

        Assert.Equal(200, status);
    }
}
