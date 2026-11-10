using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

using static Paduraksa.Application.Config.ConfigErrorCodes;
using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>
/// The rate-limit policy's field rules and the host/route references to it. The rate is judged against the engine's own
/// bounds, so a document that validates can't make <see cref="Paduraksa.Proxy.RateLimiting.RateLimitParameters"/> throw.
/// </summary>
public sealed class RateLimitValidationTests
{
    private const string Proxy = "/hosts/0";
    private const string PolicyPath = "/rateLimitPolicies/0";

    [Fact]
    public void TheSamplePolicyAndAHeaderKeyAreValid()
    {
        Assert.Empty(ConfigValidator.Validate(Valid()));
        Assert.Empty(ConfigValidator.Validate(Valid() with
        {
            RateLimitPolicies = [new RateLimitPolicyConfig(ApiPolicy, "api", Requests: 100, Per: RateLimitPeriod.Second, Burst: 200, Key: RateLimitKey.Header, Header: "X-Api-Key")],
        }));
    }

    [Theory]
    [InlineData(0, RateLimitRequestsInvalid)]
    [InlineData(-1, RateLimitRequestsInvalid)]
    [InlineData(1_000_001, RateLimitRateInvalid)]
    public void RequestsMustBeAPositiveRateTheEngineAccepts(int requests, string code)
    {
        AssertErrors(
            Valid() with { RateLimitPolicies = [new RateLimitPolicyConfig(ApiPolicy, "api", Requests: requests, Per: RateLimitPeriod.Second)] },
            ($"{PolicyPath}/requests", code));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_048_576)]
    public void BurstMustFitTheTokenField(int burst) =>
        AssertErrors(
            Valid() with { RateLimitPolicies = [new RateLimitPolicyConfig(ApiPolicy, "api", Burst: burst)] },
            ($"{PolicyPath}/burst", RateLimitBurstInvalid));

    [Fact]
    public void AHeaderKeyNeedsItsHeaderAndOtherKeysRefuseOne()
    {
        AssertErrors(
            Valid() with { RateLimitPolicies = [new RateLimitPolicyConfig(ApiPolicy, "api", Key: RateLimitKey.Header)] },
            ($"{PolicyPath}/header", RateLimitHeaderRequired));
        AssertErrors(
            Valid() with { RateLimitPolicies = [new RateLimitPolicyConfig(ApiPolicy, "api", Key: RateLimitKey.ClientIp, Header: "x-api-key")] },
            ($"{PolicyPath}/header", RateLimitHeaderUnexpected));
        AssertErrors(
            Valid() with { RateLimitPolicies = [new RateLimitPolicyConfig(ApiPolicy, "api", Key: RateLimitKey.Header, Header: "not a header")] },
            ($"{PolicyPath}/header", RateLimitHeaderInvalid));
    }

    [Fact]
    public void AnUnknownHostPolicyIsReportedWhereTheHostResolvesIt()
    {
        RateLimitPolicyId missing = Id<RateLimitPolicyId>(9);
        AssertErrors(
            Valid() with
            {
                Hosts = MapHost(Valid(), AppHost, h => h with { Settings = new HostSettings { RateLimit = new RateLimitSettings { PolicyId = missing } } }),
            },
            ($"{Proxy}/settings/rateLimit/policyId", HostRateLimitUnknown));
    }

    [Fact]
    public void AnUnknownRoutePolicyIsReportedAtTheRoute()
    {
        RateLimitPolicyId missing = Id<RateLimitPolicyId>(9);
        AssertErrors(
            Valid() with
            {
                Hosts = MapHost(
                    Valid(),
                    AppHost,
                    h => h with
                    {
                        Routes = [h.Routes[0] with { Settings = new RouteSettings { RateLimit = new RateLimitSettings { PolicyId = missing } } }, h.Routes[1]],
                    }),
            },
            ($"{Proxy}/routes/0/settings/rateLimit/policyId", RouteRateLimitUnknown));
    }

    [Fact]
    public void AProfilePolicyIsInheritedAndNamedWhereItCameFrom()
    {
        RateLimitPolicyId missing = Id<RateLimitPolicyId>(9);
        AssertErrors(
            Valid() with
            {
                Profiles = [new ProfileConfig(LanProfile, "LAN only", Settings: new HostSettings { RateLimit = new RateLimitSettings { PolicyId = missing } })],
            },
            ($"{Proxy}/settings/rateLimit/policyId", HostRateLimitUnknown));
    }

    [Fact]
    public void AValidPolicyReferenceInEveryLayerValidates()
    {
        ConfigDocument document = Valid() with
        {
            Settings = new GlobalSettings(Defaults: new HostSettings { RateLimit = new RateLimitSettings { PolicyId = ApiPolicy } }),
            Profiles = [new ProfileConfig(LanProfile, "LAN only", Settings: new HostSettings { RateLimit = new RateLimitSettings { Enabled = true } })],
            Hosts = MapHost(
                Valid(),
                AppHost,
                h => h with
                {
                    Settings = new HostSettings { RateLimit = new RateLimitSettings { PolicyId = ApiPolicy } },
                    Routes = [h.Routes[0] with { Settings = new RouteSettings { RateLimit = new RateLimitSettings { Enabled = false } } }, h.Routes[1]],
                }),
        };

        Assert.Empty(ConfigValidator.Validate(document));
    }

    private static void AssertErrors(ConfigDocument document, params (string Path, string Code)[] expected) =>
        Assert.Equal(expected, ConfigValidator.Validate(document).Select(e => (e.Path, e.Code)));

    private static HostConfig[] MapHost(ConfigDocument document, HostId id, Func<HostConfig, HostConfig> change) =>
        [.. document.Hosts.Select(h => h.Id == id ? change(h) : h)];
}
