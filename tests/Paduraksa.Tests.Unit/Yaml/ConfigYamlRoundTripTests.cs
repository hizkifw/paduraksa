using System.Text;

using Paduraksa.Application.Config;
using Paduraksa.Application.Yaml;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Shared;
using Paduraksa.Tests.Unit.Config;

using Xunit;

namespace Paduraksa.Tests.Unit.Yaml;

public sealed class ConfigYamlRoundTripTests
{
    [Fact]
    public void TheSampleConfigExportsAsFriendlyYamlAndReadsBackToTheSameDocument()
    {
        var document = YamlTestSupport.WithoutVersions(ConfigSamples.Valid());
        var yaml = ConfigYamlExporter.Export(document);

        var read = YamlTestSupport.Mapped(yaml);

        Assert.Equal(Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(document)), Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(read)));
    }

    [Fact]
    public void AnUpstreamsHostHeaderIsAKeyOfTheYamlAndReadsBack()
    {
        var sample = YamlTestSupport.WithoutVersions(ConfigSamples.Valid());
        var document = sample with
        {
            Upstreams =
            [
                sample.Upstreams[0] with { HostHeader = new UpstreamHostHeaderConfig(UpstreamHostHeaderMode.Custom, "app.internal:8443") },
                .. sample.Upstreams.Skip(1),
            ],
        };

        var yaml = ConfigYamlExporter.Export(document);
        var read = YamlTestSupport.Mapped(yaml);

        Assert.Contains("hostHeader:", yaml, StringComparison.Ordinal);
        Assert.Contains("app.internal:8443", yaml, StringComparison.Ordinal);
        Assert.Equal(UpstreamHostHeaderMode.Custom, read.Upstreams[0].HostHeader!.Mode);
        Assert.Equal("app.internal:8443", read.Upstreams[0].HostHeader!.Value);
        Assert.Equal(Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(document)), Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(read)));
    }

    [Fact]
    public void AnUpstreamsTimeoutsAreAKeyOfTheYamlAndReadBack()
    {
        var sample = YamlTestSupport.WithoutVersions(ConfigSamples.Valid());
        var document = sample with
        {
            Upstreams =
            [
                sample.Upstreams[0] with { Timeouts = new UpstreamTimeoutsConfig(TimeSpan.FromSeconds(3), TimeSpan.Zero) },
                .. sample.Upstreams.Skip(1),
            ],
        };

        var yaml = ConfigYamlExporter.Export(document);
        var read = YamlTestSupport.Mapped(yaml);

        Assert.Contains("timeouts:", yaml, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(3), read.Upstreams[0].Timeouts!.Connect);
        Assert.Equal(TimeSpan.Zero, read.Upstreams[0].Timeouts!.Idle);
        Assert.Equal(Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(document)), Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(read)));
    }

    [Fact]
    public void EveryGeneratedValidDocumentExportsAndReadsBackToTheSameDocument()
    {
        var checkedDocuments = 0;
        ConfigGen.Documents.Check(
            300,
            generated =>
            {
                if (ConfigValidator.Validate(generated).Count > 0)
                {
                    return;
                }

                checkedDocuments++;
                var document = YamlTestSupport.WithoutVersions(generated);
                var yaml = ConfigYamlExporter.Export(document);
                var result = YamlTestSupport.Read(yaml);
                Assert.True(result.Succeeded, YamlTestSupport.Errors(result) + "\n" + yaml);
                Assert.Equal(Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(document)), Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(result.Mapped!.Document)));
            });
        Assert.True(checkedDocuments > 20, $"only {checkedDocuments} documents were valid");
    }

    [Fact]
    public void TheExportIsAFixpointAndTheSameEveryTime()
    {
        ConfigGen.Documents.Check(
            200,
            generated =>
            {
                if (ConfigValidator.Validate(generated).Count > 0)
                {
                    return;
                }

                var yaml = ConfigYamlExporter.Export(YamlTestSupport.WithoutVersions(generated));
                Assert.Equal(yaml, ConfigYamlExporter.Export(YamlTestSupport.WithoutVersions(generated)));
                Assert.Equal(yaml, ConfigYamlExporter.Export(YamlTestSupport.Mapped(yaml)));
            });
    }

    [Fact]
    public void TheExportCarriesNoVersionAndNoPasswordAndAUserIsItsNameAndCredential()
    {
        var credential = ConfigSamples.Credential(1);
        var document = ConfigSamples.Valid() with
        {
            AccessLists =
            [
                new AccessListConfig(
                    ConfigSamples.LanList, "lan", Version: 9,
                    BasicAuth: new AccessListBasicAuth("Members", [new AccessListUserConfig("alice", credential, Password: "never-exported")])),
            ],
        };

        var yaml = ConfigYamlExporter.Export(CanonicalConfigJson.Canonicalize(document));

        Assert.DoesNotMatch(@"(?m)^\s*(- )?version:", yaml);
        Assert.DoesNotContain("password", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("never-exported", yaml, StringComparison.Ordinal);
        Assert.Contains($"- username: alice\n          credentialId: {credential}", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceIsANameWhenOneObjectHasItAndAnIdWhenTwoDoOrNoneDoes()
    {
        var unique = ConfigSamples.Valid();
        Assert.Contains("profile: LAN only", ConfigYamlExporter.Export(CanonicalConfigJson.Canonicalize(unique)), StringComparison.Ordinal);

        var twins = unique with { Profiles = [.. unique.Profiles, new ProfileConfig(ConfigSamples.Id<ProfileId>(2), "lan only")] };
        Assert.Contains($"profile: {ConfigSamples.LanProfile}", ConfigYamlExporter.Export(CanonicalConfigJson.Canonicalize(twins)), StringComparison.Ordinal);

        var gone = unique with { Profiles = [] };
        Assert.Contains($"profile: {ConfigSamples.LanProfile}", ConfigYamlExporter.Export(CanonicalConfigJson.Canonicalize(gone)), StringComparison.Ordinal);
    }

    [Fact]
    public void ABuiltInRuleSetIsNamedLikeAnyOther()
    {
        var document = ConfigSamples.Valid() with
        {
            Settings = new GlobalSettings(Defaults: new HostSettings { RuleSet = new RuleSetSettings { RuleSetId = RuleSetPresets.KnownScanners.Id } }),
        };

        var yaml = ConfigYamlExporter.Export(CanonicalConfigJson.Canonicalize(document));

        Assert.Contains("ruleSet: Block known scanners", yaml, StringComparison.Ordinal);
        Assert.Equal(RuleSetPresets.KnownScanners.Id, YamlTestSupport.Mapped(yaml).Settings.Defaults.RuleSet!.RuleSetId);
    }

    [Fact]
    public void EmptyListsAndMappingsAreLeftOutExceptWhereEmptyIsNotAbsent()
    {
        var document = new ConfigDocument
        {
            Upstreams = [new UpstreamConfig(ConfigSamples.AppUpstream, "app", PassiveHealth: new PassiveHealthConfig())],
        };

        var yaml = ConfigYamlExporter.Export(CanonicalConfigJson.Canonicalize(document));

        Assert.DoesNotContain("hosts", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("destinations", yaml, StringComparison.Ordinal);
        Assert.Contains("passiveHealth: {}", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEmptyConfigIsJustItsHeaderAndDefaults()
    {
        var yaml = ConfigYamlExporter.Export(CanonicalConfigJson.Canonicalize(new ConfigDocument()));

        Assert.StartsWith("apiVersion: paduraksa.dev/v1\nkind: Config\n", yaml, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(new ConfigDocument())), Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(YamlTestSupport.Mapped(yaml))));
    }

    [Fact]
    public void TextThatIsHardForYamlSurvivesInEveryStringField()
    {
        string[] awkward = ["NO", "true", "null", "123", "00:00:15", "a: b", "# not a comment", " padded ", "line one\nline two\n", "- dash", "'quoted'", "tab\there", "\u00e9\u65e5", ""];
        foreach (var text in awkward)
        {
            var document = ConfigSamples.Valid() with
            {
                Profiles = [new ProfileConfig(ConfigSamples.LanProfile, "profile", Notes: text.Length == 0 ? "x" : text)],
                Hosts = [new HostConfig(ConfigSamples.AppHost, ["a.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(200, text), Notes: text)],
            };
            var canonical = YamlTestSupport.WithoutVersions(document);

            var read = YamlTestSupport.Mapped(ConfigYamlExporter.Export(canonical));

            Assert.Equal(Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(canonical)), Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(read)));
        }
    }

    [Fact]
    public void ARequiredListThatIsEmptyIsWrittenSoItReadsBackAsTheSameInvalidDocument()
    {
        var document = new ConfigDocument { Hosts = [new HostConfig(ConfigSamples.AppHost, [], Kind: HostKind.Respond, Respond: new RespondConfig(200))] };
        var canonical = YamlTestSupport.WithoutVersions(document);

        var yaml = ConfigYamlExporter.Export(canonical);

        Assert.Contains("names: []", yaml, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(canonical)), Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(YamlTestSupport.Mapped(yaml))));
    }
}
