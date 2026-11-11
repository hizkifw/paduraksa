using System.Net;
using System.Text;

using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Unit.Compilation;

using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>
/// Documents written before access lists existed carry an inline <c>ipAllowList</c> in the settings of the global defaults,
/// a profile, a host or a route. Reading one converts it: the same addresses pass and fail, the same refusals drop the
/// connection, and converting twice gives the same bytes. The JSON here is written by hand, as the old build wrote it.
/// </summary>
public sealed class LegacyAccessListUpgradeTests : IDisposable
{
    private static readonly ConfigRevisionId Revision = Id<ConfigRevisionId>(7);
    private static readonly IPAddress InTen = IPAddress.Parse("10.1.2.3");
    private static readonly IPAddress InLan = IPAddress.Parse("192.168.1.9");
    private static readonly IPAddress Outsider = IPAddress.Parse("198.51.100.1");

    private readonly AnyCertificate _certificates = new();

    public void Dispose() => _certificates.Dispose();

    [Fact]
    public async Task AHostWhoseRoutesNamedDifferentListsBehavesAsItDid()
    {
        // The old SplitHost: route /a admits 10/8, route /b admits 192.168.1/24, the host forces HTTPS.
        using var gate = Gate(Old(
            defaults: "{\"forceHttps\":true}",
            hostSettings: "{}",
            routes: [("/a", "{\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"]}}"), ("/b", "{\"ipAllowList\":{\"addresses\":[\"192.168.1.0/24\"]}}")]));

        Assert.Equal(GateHarness.Forwarded, await gate.SendAsync("app.example.com", "https", "/a/x", InTen));
        Assert.Equal(403, await gate.SendAsync("app.example.com", "https", "/b", InTen));
        Assert.Equal(GateHarness.Forwarded, await gate.SendAsync("app.example.com", "https", "/b", InLan));
        Assert.Equal(403, await gate.SendAsync("app.example.com", "https", "/a", InLan));
        Assert.Equal(301, await gate.SendAsync("app.example.com", "http", "/b", InTen));
        Assert.Equal(404, await gate.SendAsync("app.example.com", "https", "/nothing-here", InTen));

        // A client no route admits is refused everywhere, over plain HTTP too, so nothing tells the paths apart.
        foreach (var path in (string[])["/", "/a", "/b", "/nothing-here"])
        {
            Assert.Equal(403, await gate.SendAsync("app.example.com", "http", path, Outsider));
            Assert.Equal(403, await gate.SendAsync("app.example.com", "https", path, Outsider));
        }
    }

    [Fact]
    public async Task DropDeniedStillAbortsTheConnection()
    {
        using var gate = Gate(Old(
            defaults: "{\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"],\"dropDenied\":true}}",
            hostSettings: "{}",
            routes: [("/a", "{}"), ("/b", "{\"ipAllowList\":{\"addresses\":[\"192.168.1.0/24\"]}}")]));

        // Route /a inherits the defaults' list, which drops; route /b names its own, which answers 403. Every list must drop for
        // the host chain to drop, so the outsider gets the 403.
        Assert.Equal(GateHarness.Forwarded, await gate.SendAsync("app.example.com", "https", "/a", InTen));
        Assert.Equal(GateHarness.Aborted, await gate.SendAsync("app.example.com", "https", "/a", InLan));
        Assert.Equal(403, await gate.SendAsync("app.example.com", "https", "/b", InTen));
        Assert.Equal(403, await gate.SendAsync("app.example.com", "https", "/a", Outsider));
    }

    [Fact]
    public async Task EveryListThatDropsDropsTheOutsider()
    {
        using var gate = Gate(Old(
            defaults: "{}",
            hostSettings: "{\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"],\"dropDenied\":true}}",
            routes: [("/a", "{}"), ("/b", "{}")]));

        Assert.Equal(GateHarness.Forwarded, await gate.SendAsync("app.example.com", "https", "/a", InTen));
        Assert.Equal(GateHarness.Aborted, await gate.SendAsync("app.example.com", "https", "/a", Outsider));
        Assert.Equal(GateHarness.Aborted, await gate.SendAsync("app.example.com", "http", "/nothing-here", Outsider));
    }

    [Fact]
    public async Task AnEmptyListStillLiftsTheListItInherited()
    {
        using var gate = Gate(Old(
            defaults: "{\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"]}}",
            hostSettings: "{}",
            routes: [("/a", "{}"), ("/b", "{\"ipAllowList\":{\"addresses\":[]}}")]));

        Assert.Equal(403, await gate.SendAsync("app.example.com", "https", "/a", Outsider));
        Assert.Equal(GateHarness.Forwarded, await gate.SendAsync("app.example.com", "https", "/b", Outsider));
    }

    [Fact]
    public async Task AHostListOverTheProfilesAndAnEmptyOneOverThat()
    {
        var upgraded = Deserialize(Old(
            defaults: "{}",
            hostSettings: "{\"ipAllowList\":{\"addresses\":[\"192.168.1.0/24\"]}}",
            routes: [("/", "{}")],
            profile: "{\"ipAllowList\":{\"addresses\":[]}}"));
        using var gate = Gate(upgraded);

        // The profile lifts what is inherited; the host's own list replaces whatever the layers below say.
        Assert.Equal(GateHarness.Forwarded, await gate.SendAsync("app.example.com", "https", "/", InLan));
        Assert.Equal(403, await gate.SendAsync("app.example.com", "https", "/", InTen));
    }

    [Fact]
    public async Task AnExplicitNullListInheritsInsteadOfLiftingWhatIsBelow()
    {
        var upgraded = Deserialize(Old(
            defaults: "{\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"]}}",
            hostSettings: "{\"ipAllowList\":null}",
            routes: [("/a", "{\"ipAllowList\":null}")]));

        Assert.Null(Assert.Single(upgraded.Hosts).Settings.AccessList);
        Assert.Null(upgraded.Hosts[0].Routes[0].Settings.AccessList);
        Assert.Single(upgraded.AccessLists);
        using var gate = Gate(upgraded);
        Assert.Equal(GateHarness.Forwarded, await gate.SendAsync("app.example.com", "https", "/a", InTen));
        Assert.Equal(403, await gate.SendAsync("app.example.com", "https", "/a", Outsider));
    }

    [Fact]
    public void ADocumentWhoseOnlyListIsNullIsStillConvertedSoTheStrictReaderAcceptsIt()
    {
        var old = Encoding.UTF8.GetBytes(Old("{}", "{\"ipAllowList\":null}", [("/", "{}")]));

        Assert.True(CanonicalConfigJson.TryDeserialize(old, out var document, out _, out var upgrade));
        Assert.True(upgrade.Upgraded);
        Assert.Empty(document.AccessLists);
        Assert.DoesNotContain("ipAllowList", Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(document)), StringComparison.Ordinal);
    }

    [Fact]
    public void EachDistinctListBecomesOneAccessListNamedAfterWhereItCameFrom()
    {
        var document = Deserialize(Old(
            defaults: "{\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"]}}",
            hostSettings: "{\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"]}}",
            routes: [("/admin", "{\"ipAllowList\":{\"addresses\":[\"192.168.1.0/24\"]}}"), ("/b", "{\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"],\"dropDenied\":true}}")],
            profile: "{\"ipAllowList\":{\"addresses\":[\"192.168.1.0/24\"]}}",
            profileName: "LAN only"));

        // Document order: the global defaults, the profile, the host, then its routes. The host's list is the defaults' content,
        // and the profile's is the admin route's, so they share a list; the dropping one differs by its setting.
        Assert.Equal(["global-defaults", "profile-lan-only", "route-app-example-com-b"], document.AccessLists.Select(l => l.Name).Order(StringComparer.Ordinal));
        var byName = document.AccessLists.ToDictionary(l => l.Name);
        Assert.Equal(["10.0.0.0/8"], byName["global-defaults"].Ip!.Allow);
        Assert.Equal(["192.168.1.0/24"], byName["profile-lan-only"].Ip!.Allow);
        Assert.True(byName["route-app-example-com-b"].Ip!.DropDenied);
        Assert.All(document.AccessLists, l => Assert.Null(l.BasicAuth));

        var host = Assert.Single(document.Hosts);
        Assert.Equal(byName["global-defaults"].Id, document.Settings.Defaults.AccessList!.AccessListId);
        Assert.Equal(byName["global-defaults"].Id, host.Settings.AccessList!.AccessListId);
        Assert.Equal(byName["profile-lan-only"].Id, host.Routes.Single(r => r.Path == "/admin").Settings.AccessList!.AccessListId);
        Assert.All(document.Hosts.SelectMany(h => h.Routes).Where(r => r.Settings.AccessList is not null), r => Assert.True(r.Settings.AccessList!.Enabled));
    }

    [Fact]
    public void NamesAreDeDuplicatedAndNeverClash()
    {
        // Two hosts with the same first name and different lists: the second gets a suffix.
        var json = OldWithHosts(
            ("app.example.com", "{\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"]}}"),
            ("app.example.com", "{\"ipAllowList\":{\"addresses\":[\"192.168.0.0/16\"]}}"));

        var document = Deserialize(json);

        Assert.Equal(["host-app-example-com", "host-app-example-com-2"], document.AccessLists.Select(l => l.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(ConfigValidator.Validate(document), e => e.Code is "name.duplicate" or "id.duplicate");
    }

    [Fact]
    public void ConvertingTwiceGivesTheSameBytesAndTheSameIds()
    {
        var json = Old(
            defaults: "{\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"]}}",
            hostSettings: "{\"ipAllowList\":{\"addresses\":[\"192.168.1.0/24\",\"10.0.0.0/8\"],\"dropDenied\":true}}",
            routes: [("/a", "{\"ipAllowList\":{\"addresses\":[]}}")]);

        var first = CanonicalConfigJson.Serialize(Deserialize(json));
        var second = CanonicalConfigJson.Serialize(Deserialize(json));

        Assert.Equal(first, second);
        // The converted document is the current shape: reading its bytes converts nothing and changes nothing.
        var again = CanonicalConfigJson.Deserialize(first);
        Assert.Equal(first, CanonicalConfigJson.Serialize(again));
        Assert.Equal(Encoding.UTF8.GetString(first), Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(CanonicalConfigJson.Deserialize(first))));
        Assert.DoesNotContain("ipAllowList", Encoding.UTF8.GetString(first), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameContentGetsTheSameIdInAnotherDocument()
    {
        var one = Deserialize(Old("{\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"]}}", "{}", [("/", "{}")]));
        var other = Deserialize(Old("{}", "{\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"]}}", [("/", "{}")]));

        Assert.Equal(one.AccessLists.Single().Id, other.AccessLists.Single().Id);
        Assert.NotEqual(Deserialize(Old("{\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"],\"dropDenied\":true}}", "{}", [("/", "{}")])).AccessLists.Single().Id, one.AccessLists.Single().Id);
    }

    [Fact]
    public void AddressesAreCanonicalisedSoSpellingsOfOneListAreOneList()
    {
        var document = Deserialize(Old(
            defaults: "{\"ipAllowList\":{\"addresses\":[\" 10.0.0.0/8\",\"::ffff:10.0.0.0/104\"]}}",
            hostSettings: "{\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"]}}",
            routes: [("/", "{}")]));

        var list = Assert.Single(document.AccessLists);
        Assert.Equal(["10.0.0.0/8"], list.Ip!.Allow);
    }

    [Fact]
    public void ADocumentWithoutAnInlineListIsLeftAlone()
    {
        var json = CanonicalConfigJson.Serialize(Valid());

        Assert.Null(LegacyAccessListUpgrade.Upgrade(json));
        Assert.Equal(json, CanonicalConfigJson.Serialize(CanonicalConfigJson.Deserialize(json)));
    }

    [Fact]
    public void ADocumentOfTheOldShapeIsReportedAsUpgradedAndAnyOtherIsNot()
    {
        var old = Encoding.UTF8.GetBytes(Old("{\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"]}}", "{}", [("/", "{}")]));

        Assert.True(CanonicalConfigJson.TryDeserialize(old, out _, out _, out var upgrade));
        Assert.True(upgrade.Upgraded);
        Assert.True(CanonicalConfigJson.TryDeserialize(CanonicalConfigJson.Serialize(Valid()), out _, out _, out upgrade));
        Assert.False(upgrade.Upgraded);
    }

    [Fact]
    public void TheConvertedDocumentValidatesAndCompiles()
    {
        var document = Deserialize(Old(
            defaults: "{\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"],\"dropDenied\":true}}",
            hostSettings: "{\"ipAllowList\":{\"addresses\":[\"192.168.1.0/24\"]}}",
            routes: [("/a", "{\"ipAllowList\":{\"addresses\":[\"2001:db8::/32\",\"203.0.113.7\"]}}"), ("/b", "{\"ipAllowList\":{\"addresses\":[]}}")]));

        Assert.Empty(ConfigValidator.Validate(document));
        var compilation = _certificates.Compile(document, Revision);
        Assert.True(compilation.Succeeded, string.Join("; ", compilation.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void NotJsonOrNotAnObjectIsLeftForTheReaderToReport()
    {
        Assert.Null(LegacyAccessListUpgrade.Upgrade("[\"ipAllowList\"]"u8));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => LegacyAccessListUpgrade.Upgrade("{\"ipAllowList\": "u8));
    }

    private GateHarness Gate(string oldJson) => Gate(Deserialize(oldJson));

    private GateHarness Gate(ConfigDocument document)
    {
        var compilation = _certificates.Compile(document, Revision);
        Assert.True(compilation.Succeeded, string.Join("; ", compilation.Errors.Select(e => $"{e.Path}: {e.Message}")));
        return new GateHarness(compilation.Snapshot!);
    }

    private static ConfigDocument Deserialize(string json) => CanonicalConfigJson.Deserialize(Encoding.UTF8.GetBytes(json));

    /// <summary>A document as the build before access lists wrote it: one proxy host with the given routes, optionally under a profile.</summary>
    private static string Old(string defaults, string hostSettings, (string Path, string Settings)[] routes, string? profile = null, string profileName = "profile")
    {
        var routeJson = string.Join(
            ",",
            routes.Select((r, i) => $"{{\"id\":\"{Id<RouteId>(i + 1)}\",\"upstreamId\":\"{AppUpstream}\",\"order\":0,\"match\":\"prefix\",\"path\":\"{r.Path}\",\"settings\":{r.Settings}}}"));
        var profiles = profile is null ? "[]" : $"[{{\"id\":\"{LanProfile}\",\"name\":\"{profileName}\",\"version\":1,\"settings\":{profile}}}]";
        var profileId = profile is null ? string.Empty : $"\"profileId\":\"{LanProfile}\",";
        return $$$"""
            {"apiVersion":"paduraksa.dev/v1","settings":{"version":0,"unknownHost":"notFound","defaults":{{{defaults}}},"trustedProxies":{"mode":"off","useTrueClientIp":false,"forwardedForFallback":false,"trustForwardedProto":false,"proxies":[]}},
            "profiles":{{{profiles}}},
            "upstreams":[{"id":"{{{AppUpstream}}}","name":"app","version":1,"sessionAffinity":false,"destinations":[{"id":"{{{AppDestination}}}","address":"http://app","enabled":true}]}],
            "hosts":[{"id":"{{{AppHost}}}","names":["app.example.com"],"version":1,"kind":"proxy","enabled":true,{{{profileId}}}"certificate":{"mode":"auto"},"settings":{{{hostSettings}}},"routes":[{{{routeJson}}}],"tags":[]}],
            "rateLimitPolicies":[]}
            """;
    }

    private static string OldWithHosts(params (string Name, string Settings)[] hosts)
    {
        var hostJson = string.Join(
            ",",
            hosts.Select((h, i) => $"{{\"id\":\"{Id<HostId>(i + 1)}\",\"names\":[\"{h.Name}\"],\"version\":1,\"kind\":\"proxy\",\"enabled\":true,\"certificate\":{{\"mode\":\"auto\"}},\"settings\":{h.Settings},\"routes\":[{{\"id\":\"{Id<RouteId>(i + 1)}\",\"upstreamId\":\"{AppUpstream}\",\"order\":0,\"match\":\"prefix\",\"path\":\"/\",\"settings\":{{}}}}],\"tags\":[]}}"));
        return $$$"""
            {"apiVersion":"paduraksa.dev/v1","settings":{"version":0,"unknownHost":"notFound","defaults":{},"trustedProxies":{"mode":"off","useTrueClientIp":false,"forwardedForFallback":false,"trustForwardedProto":false,"proxies":[]}},
            "profiles":[],
            "upstreams":[{"id":"{{{AppUpstream}}}","name":"app","version":1,"sessionAffinity":false,"destinations":[{"id":"{{{AppDestination}}}","address":"http://app","enabled":true}]}],
            "hosts":[{{{hostJson}}}],
            "rateLimitPolicies":[]}
            """;
    }
}
