using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class ReviewNoticesTests
{
    private const string AnyTrust = "upstream.tls.anyTrust";
    private const string AnyTrustMessage = "upstream authentik accepts any certificate";

    private static readonly UpstreamConfig Authentik = new(UpstreamId.New(), "authentik", Destinations: [new DestinationConfig(DestinationId.New(), "https://10.0.0.5:9443")]);
    private static readonly UpstreamConfig App = new(UpstreamId.New(), "app", Destinations: [new DestinationConfig(DestinationId.New(), "http://10.0.0.6:8080")]);
    private static readonly HostConfig Ai = new(HostId.New(), ["ai.home.test"], Routes: [new RouteConfig(RouteId.New(), App.Id)]);

    private static readonly ConfigDocument Before = new(Upstreams: [Authentik, App], Hosts: [Ai]);

    [Fact]
    public void AStandingWarningOnAnUntouchedObjectStaysOutOfAnotherObjectsReview()
    {
        // The host's edit leaves the authentik upstream alone: its warning was already there, and isn't this change's.
        var after = Before with { Hosts = [Ai with { Settings = new HostSettings { AccessLog = false } }] };
        ConfigWarning standing = new("/upstreams/0/tls/trust", AnyTrust, AnyTrustMessage);

        var notices = ReviewNotices.Of(Before, after, [new HostChange(ConfigChangeKind.Modified, Ai.Id, "/hosts/0", ["settings"])], [standing], [standing]);

        Assert.Empty(notices);
    }

    [Fact]
    public void AStandingWarningShowsWhenTheChangeTouchesItsObject()
    {
        var after = Before with { Upstreams = [Authentik with { Name = "auth" }, App] };
        ConfigWarning standing = new("/upstreams/0/tls/trust", AnyTrust, AnyTrustMessage);

        var notices = ReviewNotices.Of(Before, after, [new UpstreamChange(ConfigChangeKind.Modified, Authentik.Id, "/upstreams/0", ["name"])], [standing], [standing]);

        Assert.Single(notices);
    }

    [Fact]
    public void AWarningTheChangeBringsShowsWhereverItIs()
    {
        var after = Before with { Hosts = [Ai with { Settings = new HostSettings { AccessLog = false } }] };
        ConfigWarning brought = new("/upstreams/0/tls/trust", AnyTrust, AnyTrustMessage);

        var notices = ReviewNotices.Of(Before, after, [new HostChange(ConfigChangeKind.Modified, Ai.Id, "/hosts/0", ["settings"])], [brought], []);

        Assert.Single(notices);
    }

    [Fact]
    public void AnObjectMovingToAnotherIndexKeepsItsStandingWarningOut()
    {
        // A new upstream ahead of authentik moves it from index 0 to 1; it is the same object with the same warning.
        UpstreamConfig added = new(UpstreamId.New(), "new", Destinations: [new DestinationConfig(DestinationId.New(), "http://10.0.0.7:80")]);
        var after = Before with { Upstreams = [added, Authentik, App] };

        var notices = ReviewNotices.Of(
            Before, after, [new UpstreamChange(ConfigChangeKind.Added, added.Id, "/upstreams/0", [])],
            [new ConfigWarning("/upstreams/1/tls/trust", AnyTrust, AnyTrustMessage)],
            [new ConfigWarning("/upstreams/0/tls/trust", AnyTrust, AnyTrustMessage)]);

        Assert.Empty(notices);
    }
}
