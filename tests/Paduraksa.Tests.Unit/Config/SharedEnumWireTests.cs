using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Paduraksa.Admin.Json;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Json;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>
/// The enums the config document and the proxy share live in <see cref="Paduraksa.Core.Model"/>, and their wire form is
/// the contract's: a camelCase name, never a number. Both source-generated contexts must register their converters (the
/// generator can't see the attribute in another assembly), so this pins the spelling on every surface and fails when a
/// shared enum is added without its line in the lists.
/// </summary>
public sealed class SharedEnumWireTests
{
    [Fact]
    public void EverySharedEnumIsCoveredHere()
    {
        Type[] shared =
        [
            .. typeof(HostKind).Assembly.GetTypes()
                .Where(t => t.IsEnum && t.GetCustomAttribute<JsonConverterAttribute>() is { ConverterType: { IsGenericType: true } converter } && converter.GetGenericTypeDefinition() == typeof(ConfigEnumConverter<>))
                .OrderBy(t => t.Name, StringComparer.Ordinal),
        ];

        // A new shared enum: add its converter to ConfigJsonContext and AdminJsonContext, then name it here with a value
        // a document writes.
        Assert.Equal(
            [
                typeof(AccessListSatisfy),
                typeof(ForwardAuthCacheScope),
                typeof(ForwardAuthPreset),
                typeof(HostKind),
                typeof(OriginLockdownMode),
                typeof(RateLimitKey),
                typeof(RateLimitPeriod),
                typeof(RequestIdPropagation),
                typeof(RobotsMode),
                typeof(RouteMatchKind),
                typeof(RuleAction),
                typeof(RulePathKind),
                typeof(ServerHeaderMode),
                typeof(TcpAlpn),
                typeof(TlsMinimumVersion),
                typeof(TrustedProxyMode),
                typeof(UnknownHostBehaviour),
                typeof(UpstreamTlsTrust),
            ],
            shared);
    }

    [Fact]
    public void TheConfigDocumentWritesEverySharedEnumByName()
    {
        AssertEveryName(
            JsonSerializer.Serialize(CanonicalConfigJson.Canonicalize(Document()), ConfigJsonContext.Default.ConfigDocument),
            CanonicalConfigJson.Serialize(Document()));
    }

    [Fact]
    public void EveryLoadBalancingPolicyIsAYarpPolicyName()
    {
        // The one contract enum that stays in Contracts: its wire form is the YARP policy name the mapping passes on.
        string[] yarp =
        [
            .. typeof(Yarp.ReverseProxy.LoadBalancing.LoadBalancingPolicies)
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(p => p.PropertyType == typeof(string))
                .Select(p => (string)p.GetValue(null)!),
        ];

        foreach (var name in Enum.GetNames<LoadBalancingPolicy>())
        {
            Assert.Contains(name, yarp);
        }
    }

    [Fact]
    public void TheAdminApiWritesEverySharedEnumByName()
    {
        AssertEveryName(
            JsonSerializer.Serialize(Document(), HttpOptions()),
            CanonicalConfigJson.Serialize(Document()));
    }

    private static void AssertEveryName(string written, byte[] canonical)
    {
        foreach (var member in new[]
        {
            "\"unknownHost\":\"close\"",
            "\"mode\":\"cloudflareTunnel\"",
            "\"kind\":\"respond\"",
            "\"match\":\"exact\"",
            "\"minimumVersion\":\"tls13\"",
            "\"tcpAlpn\":\"http11Only\"",
            "\"mode\":\"upstream\"",
            "\"robots\":{\"mode\":\"blockAiCrawlers\"",
            "\"cloudflare\":{\"originLockdown\":\"forbid\"",
            "\"requestIdPropagation\":\"responseOnly\"",
            "\"trust\":\"any\"",
            "\"per\":\"hour\"",
            "\"key\":\"header\"",
        })
        {
            Assert.True(written.Contains(member, StringComparison.Ordinal), written);
        }

        Assert.Contains("\"unknownHost\":\"close\"", System.Text.Encoding.UTF8.GetString(canonical), StringComparison.Ordinal);
    }

    /// <summary>A document that sets every shared enum to a member other than its default.</summary>
    private static ConfigDocument Document() => new(
        Settings: new GlobalSettings(
            Defaults: new HostSettings
            {
                Tls = new TlsSettings { MinimumVersion = TlsMinimumVersion.Tls13, TcpAlpn = TcpAlpn.Http11Only },
                RequestIdPropagation = RequestIdPropagation.ResponseOnly,
                ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Upstream },
                Robots = new RobotsSettings { Mode = RobotsMode.BlockAiCrawlers },
                Cloudflare = new CloudflareSettings { OriginLockdown = OriginLockdownMode.Forbid },
            },
            UnknownHost: UnknownHostBehaviour.Close,
            TrustedProxies: new TrustedProxySettings(TrustedProxyMode.CloudflareTunnel, ["10.0.0.0/8"])),
        Upstreams:
        [
            new UpstreamConfig(
                ConfigSamples.Id<UpstreamId>(1),
                "app",
                Destinations: [new DestinationConfig(ConfigSamples.Id<DestinationId>(1), "https://app:8443")],
                Tls: new UpstreamTlsConfig(UpstreamTlsTrust.Any)),
        ],
        RateLimitPolicies:
        [
            new RateLimitPolicyConfig(
                ConfigSamples.Id<RateLimitPolicyId>(1),
                "api",
                Requests: 10,
                Per: RateLimitPeriod.Hour,
                Key: RateLimitKey.Header,
                Header: "x-api-key"),
        ],
        Hosts:
        [
            new HostConfig(
                ConfigSamples.Id<HostId>(1),
                ["app.example.com"],
                Kind: HostKind.Respond,
                Respond: new RespondConfig(503, "soon"),
                Routes: []),
            new HostConfig(
                ConfigSamples.Id<HostId>(2),
                ["other.example.com"],
                Routes: [new RouteConfig(ConfigSamples.Id<RouteId>(1), ConfigSamples.Id<UpstreamId>(1), Match: RouteMatchKind.Exact, Path: "/exact")]),
        ]);

    private static JsonSerializerOptions HttpOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        AdminJsonContext.AddTo(options);
        return options;
    }
}
