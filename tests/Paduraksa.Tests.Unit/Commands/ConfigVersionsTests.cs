using Paduraksa.Application.Commands;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Commands;

public sealed class ConfigVersionsTests
{
    private static ConfigDocument Current() => ConfigVersions.Stamp(Valid(), Valid(), ConfigDiff.Compare(new ConfigDocument(), Valid()), next: 4);

    [Fact]
    public void EverythingAddedGetsTheNextVersionButUnchangedSettingsKeepTheirs()
    {
        var stamped = Current();

        Assert.All(stamped.Hosts, h => Assert.Equal(4, h.Version));
        Assert.All(stamped.Upstreams, u => Assert.Equal(4, u.Version));
        Assert.All(stamped.Profiles, p => Assert.Equal(4, p.Version));
        Assert.Equal(4, stamped.Settings.Version); // the sample's settings differ from the defaults
    }

    [Fact]
    public void OnlyTouchedObjectsMoveAndAChildChangeCountsForItsParent()
    {
        var current = Current();
        var edited = current with
        {
            Upstreams = [current.Upstreams[0] with { Destinations = [current.Upstreams[0].Destinations[0] with { Enabled = true, Address = "http://other:80" }] }],
            Hosts = [.. current.Hosts.Select(h => h.Id == AppHost ? h with { Routes = [h.Routes[0] with { Path = "/x" }, h.Routes[1]] } : h)],
        };

        var stamped = ConfigVersions.Stamp(edited, current, ConfigDiff.Compare(current, edited), next: 9);

        Assert.Equal(9, stamped.Upstreams[0].Version);
        Assert.Equal(9, stamped.Hosts.Single(h => h.Id == AppHost).Version);
        Assert.All(stamped.Hosts.Where(h => h.Id != AppHost), h => Assert.Equal(4, h.Version));
        Assert.Equal(4, stamped.Profiles[0].Version);
        Assert.Equal(4, stamped.Settings.Version);
    }

    [Fact]
    public void SentVersionsAreIgnoredAndTheOrderIsKept()
    {
        var current = Current();
        var sent = current with { Hosts = [.. current.Hosts.Reverse().Select(h => h with { Version = 1000 })], Settings = current.Settings with { Version = -3 } };

        var stamped = ConfigVersions.Stamp(sent, current, ConfigDiff.Compare(current, sent), next: 5);

        Assert.Equal(sent.Hosts.Select(h => h.Id), stamped.Hosts.Select(h => h.Id));
        Assert.All(stamped.Hosts, h => Assert.Equal(4, h.Version));
        Assert.Equal(4, stamped.Settings.Version);
    }

    [Fact]
    public void ARemovedAndReAddedObjectGetsANewVersion()
    {
        var current = Current();
        var readded = current.Hosts.Single(h => h.Id == RespondHost) with { Version = 4 };
        var without = current with { Hosts = [.. current.Hosts.Where(h => h.Id != RespondHost)] };
        var again = without with { Hosts = [.. without.Hosts, readded] };

        var stamped = ConfigVersions.Stamp(again, without, ConfigDiff.Compare(without, again), next: 6);

        Assert.Equal(6, stamped.Hosts.Single(h => h.Id == RespondHost).Version);
    }

    [Fact]
    public void TouchedObjectsCountChildrenAgainstTheirParents()
    {
        var route = Id<RouteId>(9);
        var destination = Id<DestinationId>(9);
        var touched = TouchedObjects.From(
        [
            new RouteChange(ConfigChangeKind.Added, AppHost, route, "/hosts/0/routes/2", []),
            new DestinationChange(ConfigChangeKind.Removed, AppUpstream, destination, "/upstreams/0/destinations/1", []),
            new ProfileChange(ConfigChangeKind.Modified, LanProfile, "/profiles/0", ["/profiles/0/name"]),
            new DocumentChange(ConfigChangeKind.Modified, string.Empty, ["/apiVersion"]),
        ]);

        Assert.False(touched.Settings);
        Assert.Equal([AppHost], touched.Hosts);
        Assert.Equal([AppUpstream], touched.Upstreams);
        Assert.Equal([LanProfile], touched.Profiles);
    }
}
