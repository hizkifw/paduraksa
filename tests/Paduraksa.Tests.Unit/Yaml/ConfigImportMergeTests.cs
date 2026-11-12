using Paduraksa.Application.Yaml;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Unit.Config;

using Xunit;

using static Paduraksa.Tests.Unit.Yaml.YamlTestSupport;

namespace Paduraksa.Tests.Unit.Yaml;

public sealed class ConfigImportMergeTests
{
    private const string Header = "apiVersion: paduraksa.dev/v1\nkind: Config\n";

    private static MappedYaml Map(string yaml, ConfigDocument stored) => Read(Header + yaml, stored, ConfigImportMode.Merge).Mapped!;

    [Theory]
    [InlineData("/hosts/0/profileId", "/hosts/0/profile")]
    [InlineData("/hosts/1/routes/0/upstreamId", "/hosts/1/routes/0/upstream")]
    [InlineData("/hosts/1/routes/0/settings/rateLimit/policyId", "/hosts/1/routes/0/settings/rateLimit/policy")]
    [InlineData("/settings/defaults/maintenance/bypassAccessListId", "/settings/defaults/maintenance/bypassAccessList")]
    [InlineData("/hosts/0/names/2", "/hosts/0/names/2")]
    [InlineData("/hosts/0/certificate/certificateId", "/hosts/0/certificate/certificateId")]
    [InlineData("/hosts/0/nothingHere/x", "/hosts/0/nothingHere/x")]
    [InlineData("", "")]
    public void AJsonPointerBecomesThePointerOfTheYamlKeyThatWritesIt(string path, string expected) =>
        Assert.Equal(expected, YamlPointers.FromDocument(path));

    [Fact]
    public void MergeReplacesAMatchedObjectAsWrittenKeepsItsVersionAddsTheRestAndDeletesNothing()
    {
        var stored = ConfigSamples.Valid() with { Hosts = [.. ConfigSamples.Valid().Hosts.Select(static h => h with { Version = 5 })] };
        var imported = Map("hosts:\n  - names: [old.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n  - names: [new.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n", stored);

        var plan = ConfigImportMerge.Merge(stored, imported);

        Assert.Equal(4, plan.Document.Hosts.Count);
        Assert.Equal(ConfigSamples.AppHost, plan.Document.Hosts[0].Id);
        var replaced = plan.Document.Hosts[1];
        Assert.Equal(ConfigSamples.RedirectHost, replaced.Id);
        Assert.Equal(HostKind.Respond, replaced.Kind);
        Assert.Null(replaced.Redirect);
        Assert.Equal(5, replaced.Version);
        Assert.Equal("new.example.com", plan.Document.Hosts[3].Names[0]);
        Assert.Equal([null, 0, null, 1], plan.Origins["hosts"]);
        Assert.Equal(stored.Profiles, plan.Document.Profiles);
    }

    [Fact]
    public void AnErrorPointerMapsToTheYamlEntryOrToNothingForAStoredObject()
    {
        var stored = ConfigSamples.Valid();
        var plan = ConfigImportMerge.Merge(stored, Map("hosts:\n  - names: [old.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n", stored));

        Assert.Equal("/hosts/0/names/0", plan.ToYaml("/hosts/1/names/0"));
        Assert.Null(plan.ToYaml("/hosts/0/names/0"));
        Assert.Equal("/settings", plan.ToYaml("/settings"));
        Assert.Equal("/hosts", plan.ToYaml("/hosts"));
    }

    [Fact]
    public void ReplaceIsTheYamlAsItIsWithItsOrder()
    {
        var imported = Map("hosts:\n  - names: [b.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n", ConfigSamples.Valid());

        var plan = ConfigImportMerge.Replace(imported);

        Assert.Single(plan.Document.Hosts);
        Assert.Equal("/hosts/0/names/0", plan.ToYaml("/hosts/0/names/0"));
    }
}
