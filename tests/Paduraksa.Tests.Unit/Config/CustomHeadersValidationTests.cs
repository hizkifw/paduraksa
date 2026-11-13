using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using static Paduraksa.Application.Config.ConfigErrorCodes;
using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>Custom header rules at every layer that sets them: what is refused, with which code, and the route's merge over its host.</summary>
public sealed class CustomHeadersValidationTests
{
    private const string Proxy = "/hosts/0";

    [Fact]
    public void RulesAreValidAtEveryLayerAndOnARoute()
    {
        HostSettings valid = new()
        {
            Headers = new HeadersSettings
            {
                Request = [new HeaderRuleConfig("X-Tenant", HeaderOperation.Set, "homelab"), new HeaderRuleConfig("Authorization", HeaderOperation.Set, "Basic dXNlcjpwYXNz")],
                Response =
                [
                    new HeaderRuleConfig("Content-Security-Policy", HeaderOperation.Set, "default-src 'self'"),
                    new HeaderRuleConfig("Vary", HeaderOperation.Add, "Origin"),
                    new HeaderRuleConfig("X-Powered-By", HeaderOperation.Remove),
                    new HeaderRuleConfig("Server", HeaderOperation.Set, "edge"),
                ],
            },
        };
        var document = Valid();
        Assert.Empty(ConfigValidator.Validate(document with { Settings = document.Settings with { Defaults = valid } }));
        Assert.Empty(ConfigValidator.Validate(document with { Profiles = [document.Profiles[0] with { Settings = valid }] }));
        Assert.Empty(ConfigValidator.Validate(document with { Hosts = Host(document, valid) }));
        Assert.Empty(ConfigValidator.Validate(OnRoute(valid.Headers)));
    }

    public static TheoryData<string, bool> Reserved => new()
    {
        { "Host", false },
        { "content-length", false },
        { "Transfer-Encoding", true },
        { "Connection", true },
        { "Upgrade", false },
        { "Keep-Alive", true },
        { "TE", false },
        { "Trailer", true },
        { "Proxy-Connection", false },
        { "Content-Encoding", true },
        { "X-Forwarded-For", false },
        { "x-forwarded-proto", false },
        { "X-Forwarded-Anything", false },
        { "X-Real-IP", false },
        { "Forwarded", false },
        { "X-Request-Id", false },
        { "X-Request-Id", true },
        { "CF-Connecting-IP", false },
        { "True-Client-IP", false },
        { "X-Paduraksa-Internal", false },
        { "Strict-Transport-Security", true },
        { "Alt-Svc", true },
        { "Location", true },
        { "location", true },
    };

    [Theory]
    [MemberData(nameof(Reserved))]
    public void ANameTheProxyOwnsIsRefused(string name, bool response)
    {
        var rule = new HeaderRuleConfig(name, HeaderOperation.Set, "x");
        var headers = response ? new HeadersSettings { Response = [rule] } : new HeadersSettings { Request = [rule] };
        var direction = response ? "response" : "request";
        AssertErrors(OnHost(headers), ($"{Proxy}/settings/headers/{direction}/0/name", HeadersNameReserved));
        AssertErrors(OnRoute(headers), ($"{Proxy}/routes/0/settings/headers/{direction}/0/name", HeadersNameReserved));
    }

    [Fact]
    public void ServerAndAuthorizationStayAllowedWhereTheyMeanSomething()
    {
        // Server: a response rule follows the built-in one and wins. Host: refused on the request, harmless on the response.
        Assert.Empty(ConfigValidator.Validate(OnHost(new HeadersSettings { Response = [new HeaderRuleConfig("Server", HeaderOperation.Remove)] })));
        Assert.Empty(ConfigValidator.Validate(OnHost(new HeadersSettings { Request = [new HeaderRuleConfig("Authorization", HeaderOperation.Set, "Bearer t")] })));
    }

    [Theory]
    [InlineData(HeaderOperation.Set, true)]
    [InlineData(HeaderOperation.Remove, true)]
    [InlineData(HeaderOperation.Add, false)]
    public void ASetCookieRuleCanOnlyAdd(HeaderOperation operation, bool refused)
    {
        var rule = new HeaderRuleConfig("set-cookie", operation, operation == HeaderOperation.Remove ? null : "seen=1; Path=/");
        var document = OnHost(new HeadersSettings { Response = [rule] });
        if (refused)
        {
            AssertErrors(document, ($"{Proxy}/settings/headers/response/0/operation", HeadersOperationInvalid));
        }
        else
        {
            Assert.Empty(ConfigValidator.Validate(document));
        }

        // A request rule for Cookie is another matter: it is the request the upstream receives.
        Assert.Empty(ConfigValidator.Validate(OnHost(new HeadersSettings { Request = [new HeaderRuleConfig("Cookie", HeaderOperation.Remove)] })));
    }

    [Fact]
    public void NamesAreTokensOfBoundedLength()
    {
        AssertErrors(
            OnHost(new HeadersSettings
            {
                Request =
                [
                    new HeaderRuleConfig(string.Empty, HeaderOperation.Remove),
                    new HeaderRuleConfig("X Space", HeaderOperation.Remove),
                    new HeaderRuleConfig("X-Colon:", HeaderOperation.Remove),
                    new HeaderRuleConfig(new string('a', CustomHeaderRules.MaxNameLength + 1), HeaderOperation.Remove),
                    new HeaderRuleConfig("X-Unicode-é", HeaderOperation.Remove),
                ],
            }),
            ($"{Proxy}/settings/headers/request/0/name", HeadersNameInvalid),
            ($"{Proxy}/settings/headers/request/1/name", HeadersNameInvalid),
            ($"{Proxy}/settings/headers/request/2/name", HeadersNameInvalid),
            ($"{Proxy}/settings/headers/request/3/name", HeadersNameInvalid),
            ($"{Proxy}/settings/headers/request/4/name", HeadersNameInvalid));
    }

    [Fact]
    public void ValuesCantBreakTheHeaderBlock()
    {
        AssertErrors(
            OnHost(new HeadersSettings
            {
                Response =
                [
                    new HeaderRuleConfig("X-A", HeaderOperation.Set, "one\r\nSet-Cookie: stolen=1"),
                    new HeaderRuleConfig("X-B", HeaderOperation.Add, "lf\nonly"),
                    new HeaderRuleConfig("X-C", HeaderOperation.Set, " padded"),
                    new HeaderRuleConfig("X-D", HeaderOperation.Set, new string('v', CustomHeaderRules.MaxValueLength + 1)),
                    new HeaderRuleConfig("X-E", HeaderOperation.Set),
                    new HeaderRuleConfig("X-F", HeaderOperation.Set, "nul\0"),
                    new HeaderRuleConfig("X-G", HeaderOperation.Set, "café"),
                ],
            }),
            ($"{Proxy}/settings/headers/response/0/value", HeadersValueInvalid),
            ($"{Proxy}/settings/headers/response/1/value", HeadersValueInvalid),
            ($"{Proxy}/settings/headers/response/2/value", HeadersValueInvalid),
            ($"{Proxy}/settings/headers/response/3/value", HeadersValueInvalid),
            ($"{Proxy}/settings/headers/response/4/value", HeadersValueInvalid),
            ($"{Proxy}/settings/headers/response/5/value", HeadersValueInvalid),
            ($"{Proxy}/settings/headers/response/6/value", HeadersValueInvalid));
    }

    [Fact]
    public void ARemoveTakesNoValueAndANameHasOneRulePerLayer()
    {
        AssertErrors(
            OnHost(new HeadersSettings
            {
                Request =
                [
                    new HeaderRuleConfig("X-A", HeaderOperation.Remove, "x"),
                    new HeaderRuleConfig("X-B", HeaderOperation.Set, "1"),
                    new HeaderRuleConfig("x-b", HeaderOperation.Add, "2"),
                ],
            }),
            ($"{Proxy}/settings/headers/request/0/value", HeadersValueUnexpected),
            ($"{Proxy}/settings/headers/request/2/name", HeadersNameDuplicate));
    }

    [Fact]
    public void AListIsBoundedPerDirection()
    {
        HeaderRuleConfig[] rules = [.. Enumerable.Range(0, CustomHeaderRules.MaxRules + 1).Select(i => new HeaderRuleConfig($"X-Rule-{i}", HeaderOperation.Remove))];
        AssertErrors(OnHost(new HeadersSettings { Response = rules }), ($"{Proxy}/settings/headers/response", HeadersTooMany));
        Assert.Empty(ConfigValidator.Validate(OnHost(new HeadersSettings { Response = rules[..^1], Request = rules[..^1] })));
    }

    [Fact]
    public void TheMergedListIsBoundedAsEachLayerIs()
    {
        // 20 names at the global defaults and 20 others at the host: each layer is within its bound, the host's merged list isn't.
        HeaderRuleConfig[] Rules(string stem) => [.. Enumerable.Range(0, 20).Select(i => new HeaderRuleConfig($"X-{stem}-{i}", HeaderOperation.Remove))];
        var document = Valid();
        document = document with
        {
            Settings = document.Settings with { Defaults = new HostSettings { Headers = new HeadersSettings { Response = Rules("Global") } } },
            Hosts = Host(document, new HostSettings { Headers = new HeadersSettings { Response = Rules("Host") } }),
        };
        Assert.Empty(ConfigValidator.Validate(document));

        var mapped = TestMapping.Map(document, Id<ConfigRevisionId>(1), CertificateInventory.Empty);
        var result = SnapshotCompiler.Compile(mapped.Model);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Message.Contains("merge to 40 custom response header rules", StringComparison.Ordinal));
    }

    [Fact]
    public void ARouteReplacesTheHostsRuleForItsNameAndKeepsTheRest()
    {
        var document = Valid();
        var host = document.Hosts[0] with
        {
            Settings = new HostSettings
            {
                Headers = new HeadersSettings { Response = [new HeaderRuleConfig("X-Frame-Options", HeaderOperation.Set, "DENY"), new HeaderRuleConfig("X-Kept", HeaderOperation.Set, "host")] },
            },
            Routes =
            [
                document.Hosts[0].Routes[0],
                document.Hosts[0].Routes[1] with
                {
                    Settings = new RouteSettings
                    {
                        Headers = new HeadersSettings { Response = [new HeaderRuleConfig("x-frame-options", HeaderOperation.Remove), new HeaderRuleConfig("X-Route", HeaderOperation.Set, "api")] },
                    },
                },
            ],
        };
        document = document with { Hosts = [host, .. document.Hosts.Skip(1)] };
        Assert.Empty(ConfigValidator.Validate(document));

        var mapped = TestMapping.Map(document, Id<ConfigRevisionId>(1), CertificateInventory.Empty);
        Assert.Empty(mapped.Errors);
        var spec = mapped.Model.Hosts.Single(h => h.Id == AppHost);
        var api = spec.Routes.Single(r => r.Id == ApiRoute);
        Assert.Equal(
            [("x-frame-options", HeaderOperation.Remove, null), ("X-Kept", HeaderOperation.Set, "host"), ("X-Route", HeaderOperation.Set, "api")],
            api.Headers.Response.Select(r => (r.Name, r.Operation, r.Value)));

        // A route that sets nothing shares its host's rules, so the compiler compiles them once.
        Assert.Same(spec.Headers, spec.Routes.Single(r => r.Id == RootRoute).Headers);
        Assert.True(SnapshotCompiler.Compile(mapped.Model).Succeeded);
    }

    [Fact]
    public void TheCompilerRefusesWhatTheValidatorWouldHave()
    {
        // A hand-built model past the validator still can't put a reserved name or a broken value on the wire.
        var document = Valid();
        var mapped = TestMapping.Map(document, Id<ConfigRevisionId>(1), CertificateInventory.Empty);
        var bad = new HostHeadersSpec
        {
            Request = [new HeaderRuleSpec("X-Forwarded-For", HeaderOperation.Set, "1.2.3.4")],
            Response = [new HeaderRuleSpec("X-Ok", HeaderOperation.Set, "a\r\nb: c")],
        };
        var model = mapped.Model with
        {
            Hosts = [.. mapped.Model.Hosts.Select(h => h.Id == AppHost ? h with { Headers = bad, Routes = [.. h.Routes.Select(r => r with { Headers = bad })] } : h)],
        };
        var result = SnapshotCompiler.Compile(model);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Message.Contains("X-Forwarded-For", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Message.Contains("X-Ok", StringComparison.Ordinal));
    }

    [Fact]
    public void ARequestRuleCantForgeAnIdentityTheHostsProviderVouchesFor()
    {
        var document = Valid() with
        {
            AuthProviders = [new AuthProviderConfig(Sso, "sso", ForwardAuth: new ForwardAuthConfig("http://auth.example.com/verify", ResponseHeaders: ["Remote-User", "Remote-*"]))],
        };
        var host = document.Hosts[0] with
        {
            Settings = new HostSettings { AuthProvider = new AuthProviderSettings { AuthProviderId = Sso } },
            Routes =
            [
                document.Hosts[0].Routes[0] with
                {
                    Settings = new RouteSettings { Headers = new HeadersSettings { Request = [new HeaderRuleConfig("Remote-Groups", HeaderOperation.Set, "admins")] } },
                },
                document.Hosts[0].Routes[1],
            ],
        };
        document = document with { Hosts = [host, .. document.Hosts.Skip(1)] };
        Assert.Empty(ConfigValidator.Validate(document));

        var mapped = TestMapping.Map(document, Id<ConfigRevisionId>(1), CertificateInventory.Empty);
        Assert.Empty(mapped.Errors);
        var result = SnapshotCompiler.Compile(mapped.Model);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Message.Contains("Remote-Groups", StringComparison.Ordinal) && e.Message.Contains("identity", StringComparison.Ordinal));
    }

    private static ConfigDocument OnHost(HeadersSettings headers)
    {
        var document = Valid();
        return document with { Hosts = Host(document, new HostSettings { Headers = headers }) };
    }

    private static ConfigDocument OnRoute(HeadersSettings headers)
    {
        var document = Valid();
        var host = document.Hosts[0];
        return document with
        {
            Hosts = [host with { Routes = [host.Routes[0] with { Settings = new RouteSettings { Headers = headers } }, .. host.Routes.Skip(1)] }, .. document.Hosts.Skip(1)],
        };
    }

    private static HostConfig[] Host(ConfigDocument document, HostSettings settings) =>
        [.. document.Hosts.Select(h => h.Id == AppHost ? h with { Settings = settings } : h)];

    private static void AssertErrors(ConfigDocument document, params (string Path, string Code)[] expected) =>
        Assert.Equal(expected, ConfigValidator.Validate(document).Select(e => (e.Path, e.Code)));
}
