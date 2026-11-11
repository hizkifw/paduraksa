using System.Reflection;
using System.Text.Json;

using Bunit;

using MudBlazor;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// Every field of <see cref="HostSettings"/> through the settings components: setting it yields the record the
/// form would send, the inherit choice names the value the layer below has, clearing it collapses the nested record away, and an
/// error at its path shows at it. One row per field; the completeness test walks the contract, so a new setting can't be missing.
/// </summary>
public sealed class HostSettingsFieldsTests
{
    private const string Message = "Refused for the test.";
    private const string InheritWord = "Inherit";

    private static readonly RateLimitPolicyId SamplePolicy = RateLimitPolicyId.New();

    private static readonly RateLimitPolicyConfig SamplePolicyConfig = new(
        SamplePolicy, "Strict API", Requests: 10, Per: RateLimitPeriod.Second, Burst: 50);

    /// <summary>What the layer below sets for every field, so each inherit choice has a value in words to name.</summary>
    private static readonly HostSettings Inherited = new()
    {
        ForceHttps = true,
        Tls = new TlsSettings { MinimumVersion = TlsMinimumVersion.Tls13, TcpAlpn = TcpAlpn.Http11Only, Http3 = false },
        IpAllowList = new IpAllowListSettings(["10.0.0.0/8"]),
        Hsts = new HstsSettings { Enabled = true, MaxAge = HstsMaxAgePresets.SixMonths, IncludeSubDomains = true, Preload = true },
        AccessLog = false,
        RequestIdPropagation = RequestIdPropagation.UpstreamOnly,
        ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom, Value = "edge/1.0" },
        ProofOfWork = new ProofOfWorkSettings { Enabled = true, Difficulty = 18, TokenLifetimeHours = 168 },
        RateLimit = new RateLimitSettings { PolicyId = SamplePolicy },
        Robots = new RobotsSettings { Mode = RobotsMode.BlockAiCrawlers },
        Cloudflare = new CloudflareSettings { OriginLockdown = OriginLockdownMode.Forbid, AuthenticatedOriginPulls = true },
    };

    /// <summary>One row per field of the contract, in its order.</summary>
    private static readonly Field[] Fields =
    [
        // Group key: the validator names the certificate's mode for a host that forces HTTPS, never /forceHttps.
        new(
            "forceHttps",
            "force-https",
            "/forceHttps",
            new HostSettings(),
            (fields, _) => SetSelect<bool>(fields, "HTTPS redirect", true),
            new HostSettings { ForceHttps = true },
            (fields, _) => SetSelect<bool>(fields, "HTTPS redirect", null),
            new HostSettings(),
            "Inherit (redirect)")
        {
            GroupKey = true,
        },
        new(
            "tls.minimumVersion",
            "minimum-tls",
            "/tls",
            new HostSettings(),
            (fields, _) => SetSelect<TlsMinimumVersion>(fields, "Minimum TLS version", TlsMinimumVersion.Tls13),
            new HostSettings { Tls = new TlsSettings { MinimumVersion = TlsMinimumVersion.Tls13 } },
            (fields, _) => SetSelect<TlsMinimumVersion>(fields, "Minimum TLS version", null),
            new HostSettings(),
            "Inherit (TLS 1.3)")
        {
            ValidatorPath = "/tls/minimumVersion",
        },
        new(
            "tls.tcpAlpn",
            "tcp-alpn",
            "/tls",
            new HostSettings(),
            (fields, _) => SetSelect<TcpAlpn>(fields, "Protocols over TCP", TcpAlpn.Http11Only),
            new HostSettings { Tls = new TlsSettings { TcpAlpn = TcpAlpn.Http11Only } },
            (fields, _) => SetSelect<TcpAlpn>(fields, "Protocols over TCP", null),
            new HostSettings(),
            "Inherit (HTTP/1.1 only)")
        {
            ValidatorPath = "/tls/tcpAlpn",
        },
        // Group key: the validator has no rule of its own for HTTP/3.
        new(
            "tls.http3",
            "http3",
            "/tls",
            new HostSettings(),
            (fields, _) => SetSelect<bool>(fields, "HTTP/3", false),
            new HostSettings { Tls = new TlsSettings { Http3 = false } },
            (fields, _) => SetSelect<bool>(fields, "HTTP/3", null),
            new HostSettings(),
            "Inherit (off)")
        {
            GroupKey = true,
        },
        new(
            "ipAllowList",
            "allow-list-set",
            "/ipAllowList",
            new HostSettings(),
            async (fields, holder) =>
            {
                await Toggle(fields, "allow-list-set", true);
                fields.Render(parameters => parameters.Add(f => f.Settings, holder.Value));
                await Input(fields, "allow-list", "10.0.0.5");
            },
            new HostSettings { IpAllowList = new IpAllowListSettings(["10.0.0.5"]) },
            (fields, _) => Toggle(fields, "allow-list-set", false),
            new HostSettings(),
            "inherits 1 entry from the built-in defaults")
        {
            ValidatorPath = "/ipAllowList",
            ShowsAtField = true,
        },
        // Group key: HSTS's errors are at maxAge and preload; the switch itself has none.
        new(
            "hsts.enabled",
            "hsts-enabled",
            "/hsts",
            new HostSettings(),
            (fields, _) => SetSelect<bool>(fields, "HSTS", true),
            new HostSettings { Hsts = new HstsSettings { Enabled = true } },
            (fields, _) => SetSelect<bool>(fields, "HSTS", null),
            new HostSettings(),
            "Inherit (on)")
        {
            GroupKey = true,
        },
        new(
            "hsts.maxAge",
            "hsts-max-age",
            "/hsts/maxAge",
            new HostSettings { Hsts = new HstsSettings { Enabled = true } },
            (fields, _) => SetMaxAgeAsync(fields),
            new HostSettings { Hsts = new HstsSettings { Enabled = true, MaxAge = 1234 } },
            (fields, _) => SetChoice(fields, HstsMaxAgeChoice.Inherit),
            new HostSettings { Hsts = new HstsSettings { Enabled = true } },
            "Inherit (6 months)")
        {
            ValidatorPath = "/hsts/maxAge",
            ShowsAtField = true,
        },
        // Group key: the preload pair's error is at /hsts/preload, not here.
        new(
            "hsts.includeSubDomains",
            "hsts-include-subdomains",
            "/hsts/includeSubDomains",
            new HostSettings { Hsts = new HstsSettings { Enabled = true } },
            (fields, _) => SetSelect<bool>(fields, "Every subdomain", true),
            new HostSettings { Hsts = new HstsSettings { Enabled = true, IncludeSubDomains = true } },
            (fields, _) => SetSelect<bool>(fields, "Every subdomain", null),
            new HostSettings { Hsts = new HstsSettings { Enabled = true } },
            "Inherit (on)")
        {
            GroupKey = true,
        },
        new(
            "hsts.preload",
            "hsts-preload",
            "/hsts/preload",
            new HostSettings { Hsts = new HstsSettings { Enabled = true } },
            (fields, _) => SetSelect<bool>(fields, "Preload list", true),
            new HostSettings { Hsts = new HstsSettings { Enabled = true, Preload = true } },
            (fields, _) => SetSelect<bool>(fields, "Preload list", null),
            new HostSettings { Hsts = new HstsSettings { Enabled = true } },
            "Inherit (on)")
        {
            ValidatorPath = "/hsts/preload",
            ShowsAtField = true,
        },
        // Group key: the validator has no rule for the access log.
        new(
            "accessLog",
            "access-log",
            "/accessLog",
            new HostSettings(),
            (fields, _) => SetSelect<bool>(fields, "Access log", false),
            new HostSettings { AccessLog = false },
            (fields, _) => SetSelect<bool>(fields, "Access log", null),
            new HostSettings(),
            "Inherit (off)")
        {
            GroupKey = true,
        },
        new(
            "requestIdPropagation",
            "request-id-propagation",
            "/requestIdPropagation",
            new HostSettings(),
            (fields, _) => SetSelect<RequestIdPropagation>(fields, "Request ID", RequestIdPropagation.UpstreamOnly),
            new HostSettings { RequestIdPropagation = RequestIdPropagation.UpstreamOnly },
            (fields, _) => SetSelect<RequestIdPropagation>(fields, "Request ID", null),
            new HostSettings(),
            "Inherit (upstream only)")
        {
            ValidatorPath = "/requestIdPropagation",
            ShowsAtField = true,
        },
        new(
            "serverHeader.mode",
            "server-header-mode",
            "/serverHeader/mode",
            new HostSettings(),
            (fields, _) => SetSelect<ServerHeaderMode>(fields, "Server header", ServerHeaderMode.Remove),
            new HostSettings { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Remove } },
            (fields, _) => SetSelect<ServerHeaderMode>(fields, "Server header", null),
            new HostSettings(),
            "Inherit (custom: edge/1.0)")
        {
            ValidatorPath = "/serverHeader/mode",
            ShowsAtField = true,
        },

        // The custom value has no inherit choice of its own: it belongs to the mode, which says what a layer would inherit.
        new(
            "serverHeader.value",
            "server-header-value",
            "/serverHeader/value",
            new HostSettings { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom } },
            (fields, _) => Input(fields, "server-header-value", "edge/2.0"),
            new HostSettings { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom, Value = "edge/2.0" } },
            (fields, _) => Input(fields, "server-header-value", string.Empty),
            new HostSettings { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom } },
            InheritText: null)
        {
            ValidatorPath = "/serverHeader/value",
            ShowsAtField = true,
        },

        // Group key: the validator rules the difficulty and lifetime, not the switch.
        new(
            "proofOfWork.enabled",
            "proof-of-work-enabled",
            "/proofOfWork",
            new HostSettings(),
            (fields, _) => SetSelect<bool>(fields, "Browser check", true),
            new HostSettings { ProofOfWork = new ProofOfWorkSettings { Enabled = true } },
            (fields, _) => SetSelect<bool>(fields, "Browser check", null),
            new HostSettings(),
            "Inherit (on)")
        {
            GroupKey = true,
        },
        new(
            "proofOfWork.difficulty",
            "proof-of-work-difficulty",
            "/proofOfWork/difficulty",
            new HostSettings(),
            (fields, _) => SetSelect<int>(fields, "Difficulty", 20),
            new HostSettings { ProofOfWork = new ProofOfWorkSettings { Difficulty = 20 } },
            (fields, _) => SetSelect<int>(fields, "Difficulty", null),
            new HostSettings(),
            "Inherit (18 bits")
        {
            ValidatorPath = "/proofOfWork/difficulty",
            ShowsAtField = true,
        },
        new(
            "proofOfWork.tokenLifetimeHours",
            "proof-of-work-lifetime",
            "/proofOfWork/tokenLifetimeHours",
            new HostSettings(),
            (fields, _) => SetLifetime(fields, TokenLifetimeChoice.OneDay),
            new HostSettings { ProofOfWork = new ProofOfWorkSettings { TokenLifetimeHours = 24 } },
            (fields, _) => SetLifetime(fields, TokenLifetimeChoice.Inherit),
            new HostSettings(),
            "Inherit (7 days)")
        {
            ValidatorPath = "/proofOfWork/tokenLifetimeHours",
            ShowsAtField = true,
        },

        // Group key: the validator has no rule for the switch on its own; the picker reads both paths, so the old
        // switch's error still lands here.
        new(
            "rateLimit.enabled",
            "rate-limit",
            "/rateLimit/enabled",
            new HostSettings(),
            (fields, _) => SetRateLimit(fields, "off"),
            new HostSettings { RateLimit = new RateLimitSettings { Enabled = false } },
            (fields, _) => SetRateLimit(fields, "inherit"),
            new HostSettings(),
            "Inherit (Strict API, 10/s)")
        {
            ValidatorPath = "/rateLimit/enabled",
            ShowsAtField = true,
        },
        new(
            "rateLimit.policyId",
            "rate-limit",
            "/rateLimit/policyId",
            new HostSettings(),
            (fields, _) => SetRateLimit(fields, "p0"),
            new HostSettings { RateLimit = new RateLimitSettings { Enabled = true, PolicyId = SamplePolicy } },
            (fields, _) => SetRateLimit(fields, "inherit"),
            new HostSettings(),
            "Inherit (Strict API, 10/s)")
        {
            ValidatorPath = "/rateLimit/policyId",
            ShowsAtField = true,
        },

        // Group key: the validator only refuses an undefined mode, which the select can't produce.
        new(
            "robots.mode",
            "robots-mode",
            "/robots/mode",
            new HostSettings(),
            (fields, _) => SetSelect<RobotsMode>(fields, "robots.txt", RobotsMode.DisallowAll),
            new HostSettings { Robots = new RobotsSettings { Mode = RobotsMode.DisallowAll } },
            (fields, _) => SetSelect<RobotsMode>(fields, "robots.txt", null),
            new HostSettings(),
            $"Inherit (block {RobotsText.AiCrawlers.Count} AI crawlers)")
        {
            GroupKey = true,
        },

        // The text has no inherit choice of its own: it belongs to the custom mode, which says what a layer would inherit.
        new(
            "robots.customText",
            "robots-custom-text",
            "/robots/customText",
            new HostSettings { Robots = new RobotsSettings { Mode = RobotsMode.Custom } },
            (fields, _) => Input(fields, "robots-custom-text", "User-agent: *\nDisallow: /private\n"),
            new HostSettings { Robots = new RobotsSettings { Mode = RobotsMode.Custom, CustomText = "User-agent: *\nDisallow: /private\n" } },
            (fields, _) => Input(fields, "robots-custom-text", string.Empty),
            new HostSettings { Robots = new RobotsSettings { Mode = RobotsMode.Custom } },
            InheritText: null)
        {
            ValidatorPath = "/robots/customText",
            ShowsAtField = true,
        },

        // Group keys: the validator has no rule of its own for either (a lockdown mode the select can't make is the enum's
        // error, and the pair only warns).
        new(
            "cloudflare.originLockdown",
            "cloudflare-lockdown",
            "/cloudflare/originLockdown",
            new HostSettings(),
            (fields, _) => SetSelect<OriginLockdownMode>(fields, "Only allow Cloudflare", OriginLockdownMode.Drop),
            new HostSettings { Cloudflare = new CloudflareSettings { OriginLockdown = OriginLockdownMode.Drop } },
            (fields, _) => SetSelect<OriginLockdownMode>(fields, "Only allow Cloudflare", null),
            new HostSettings(),
            "Inherit (refuse with 403)")
        {
            GroupKey = true,
        },
        new(
            "cloudflare.authenticatedOriginPulls",
            "cloudflare-aop",
            "/cloudflare/authenticatedOriginPulls",
            new HostSettings(),
            (fields, _) => SetSelect<bool>(fields, "Authenticated Origin Pulls", false),
            new HostSettings { Cloudflare = new CloudflareSettings { AuthenticatedOriginPulls = false } },
            (fields, _) => SetSelect<bool>(fields, "Authenticated Origin Pulls", null),
            new HostSettings(),
            "Inherit (on)")
        {
            GroupKey = true,
        },
    ];

    public static TheoryData<string> FieldNames => [.. Fields.Select(row => row.Name)];

    [Theory]
    [MemberData(nameof(FieldNames))]
    public async Task EveryField_SetsItsRecord_ShowsWhatItInherits_AndClearsAndShowsErrors(string name)
    {
        var field = Fields.Single(row => row.Name == name);
        await using DashboardTestContext context = new();
        Holder holder = new() { Value = field.Layer };

        // The row is there, and its inherit choice names the value the layer below sets.
        var fields = Render(context, holder, field.Layer);
        Assert.Single(fields.FindAll($"[data-field={field.DataField}]"));
        if (field.InheritText is { } inherited)
        {
            Assert.Contains(inherited, Row(fields, field.DataField), StringComparison.Ordinal);
        }

        // Setting it through the control gives the record the form would send.
        await field.Set(fields, holder);
        AssertSettings(field.AfterSet, holder.Value);

        // Clearing it leaves no field behind, and an empty nested record collapses back to null.
        fields.Render(parameters => parameters.Add(f => f.Settings, holder.Value));
        await field.Clear(fields, holder);
        AssertSettings(field.AfterClear, holder.Value);

        // The validator's own path, when it has one: the control shows the message where its key matches, and leaves a
        // broader key to the form-level summary (which is where a page shows it today).
        if (field.ValidatorPath is { } validatorPath)
        {
            Holder errored = new() { Value = field.ErrorLayer };
            var withError = Render(
                context,
                errored,
                field.ErrorLayer,
                errors: new Dictionary<string, string>(StringComparer.Ordinal) { ["/settings" + (field.ShowsAtField ? field.Path : validatorPath)] = Message });
            var row = Row(withError, field.DataField);
            if (field.ShowsAtField)
            {
                Assert.Contains(Message, row, StringComparison.Ordinal);
                Assert.Contains("mud-input-error", row, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain(Message, row, StringComparison.Ordinal);
                Assert.DoesNotContain("mud-input-error", row, StringComparison.Ordinal);
            }
        }
        else
        {
            // A group key: the validator emits nothing at this setting, so there is nothing for the row to show.
            Assert.True(field.GroupKey);
        }

        if (name == "hsts.maxAge")
        {
            await AssertMaxAgePresetAsync(context, field);
        }
    }

    [Fact]
    public async Task EveryDifficultyChoiceNamesItsOwnBits()
    {
        // The items' text is a render fragment drawn after the loop has finished, so reading the loop variable there gave
        // every choice the last value ("29 bits") while their values stayed right.
        await using DashboardTestContext context = new();
        var popovers = context.Render<MudPopoverProvider>();
        var fields = Render(context, new Holder(), new HostSettings());

        await fields.InvokeAsync(() => fields.FindComponents<MudSelect<int?>>().Single(s => s.Instance.Label == "Difficulty").Instance.OpenMenu());

        var texts = popovers.FindAll(".mud-popover-open .mud-list-item").Select(item => item.TextContent.Trim()).ToList();
        Assert.Contains("8 bits", texts[1], StringComparison.Ordinal);
        Assert.Contains("28 bits", texts[^1], StringComparison.Ordinal);
        Assert.Equal(texts.Count, texts.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task TheRateLimitPicker_NamesWhatItInherits_AndMapsEachChoice()
    {
        await using DashboardTestContext context = new();
        Holder holder = new() { Value = new HostSettings() };
        var fields = Render(context, holder, holder.Value);

        // The inherit choice names the policy the layer below resolves, and the picker shows it as the selection.
        Assert.Contains("Inherit (Strict API, 10/s)", fields.Find("[data-field=rate-limit]").OuterHtml, StringComparison.Ordinal);

        await SetRateLimit(fields, "off");
        Assert.Equal(new RateLimitSettings { Enabled = false }, holder.Value.RateLimit);
        Assert.Contains("No limit", fields.Find("[data-field=rate-limit]").OuterHtml, StringComparison.Ordinal);
        await SetRateLimit(fields, "p0");
        Assert.Equal(new RateLimitSettings { Enabled = true, PolicyId = SamplePolicy }, holder.Value.RateLimit);
        Assert.Contains("Strict API", fields.Find("[data-field=rate-limit]").OuterHtml, StringComparison.Ordinal);
        await SetRateLimit(fields, "inherit");
        Assert.Null(holder.Value.RateLimit);
    }

    [Fact]
    public async Task TheCloudflareRowsOfferTheThreeLockdownChoicesAndSayWhenTheModeMakesThemInert()
    {
        await using DashboardTestContext context = new();
        var popovers = context.Render<MudPopoverProvider>();
        var fields = Render(context, new Holder(), new HostSettings());

        await fields.InvokeAsync(() => fields.FindComponents<MudSelect<OriginLockdownMode?>>().Single(s => s.Instance.Label == "Only allow Cloudflare").Instance.OpenMenu());
        var choices = popovers.FindAll(".mud-popover-open .mud-list-item").Select(item => item.TextContent.Trim()).ToList();
        Assert.Equal(["Inherit (refuse with 403)", "Off", "Refuse with 403", "Drop connection"], choices);

        // No mode given (a caller that can't say) and a Cloudflare mode show nothing; any other mode says the rows do nothing.
        Assert.Empty(fields.FindAll("[data-field=cloudflare-needs-mode]"));
        foreach (var mode in new[] { TrustedProxyMode.CloudflareProxy, TrustedProxyMode.CloudflareTunnel })
        {
            var cloudflare = context.Render<HostSettingsFields>(parameters => parameters
                .Add(f => f.Settings, new HostSettings())
                .Add(f => f.Inherited, Inherited)
                .Add(f => f.ProxyMode, mode));
            Assert.Empty(cloudflare.FindAll("[data-field=cloudflare-needs-mode]"));
        }

        foreach (var mode in new[] { TrustedProxyMode.Off, TrustedProxyMode.Generic })
        {
            var inert = context.Render<HostSettingsFields>(parameters => parameters
                .Add(f => f.Settings, new HostSettings())
                .Add(f => f.Inherited, Inherited)
                .Add(f => f.ProxyMode, mode));
            Assert.Contains("do nothing until client addresses are set to Cloudflare proxy or Cloudflare Tunnel", inert.Find("[data-field=cloudflare-needs-mode]").TextContent, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheCloudflareSettingsAreSummarisedAndCounted()
    {
        Assert.Equal(["Cloudflare only (refuse with 403), Authenticated Origin Pulls"], SettingsSummary.Summary(new HostSettings { Cloudflare = new CloudflareSettings { OriginLockdown = OriginLockdownMode.Forbid, AuthenticatedOriginPulls = true } }));
        Assert.Equal(["Cloudflare only (drop the connection)"], SettingsSummary.Summary(new HostSettings { Cloudflare = new CloudflareSettings { OriginLockdown = OriginLockdownMode.Drop } }));
        Assert.Equal(["Open to any peer, No Authenticated Origin Pulls"], SettingsSummary.Summary(new HostSettings { Cloudflare = new CloudflareSettings { OriginLockdown = OriginLockdownMode.Off, AuthenticatedOriginPulls = false } }));
        Assert.Empty(SettingsSummary.Summary(new HostSettings()));
        Assert.Equal(2, HostSettingsEdit.CountSet(new HostSettings { Cloudflare = new CloudflareSettings { OriginLockdown = OriginLockdownMode.Forbid, AuthenticatedOriginPulls = true } }));
        Assert.Null(HostSettingsEdit.WithCloudflare(new HostSettings { Cloudflare = new CloudflareSettings { AuthenticatedOriginPulls = true } }, c => c with { AuthenticatedOriginPulls = null }).Cloudflare);
    }

    [Fact]
    public async Task TheRobotsDependentsFollowTheModeAndTheListIsShown()
    {
        await using DashboardTestContext context = new();

        Holder custom = new() { Value = new HostSettings { Robots = new RobotsSettings { Mode = RobotsMode.Custom } } };
        var customFields = Render(context, custom, custom.Value);
        Assert.Single(customFields.FindAll("[data-field=robots-custom-text]"));
        Assert.Empty(customFields.FindAll("[data-field=robots-crawlers]"));

        Holder ai = new() { Value = new HostSettings { Robots = new RobotsSettings { Mode = RobotsMode.BlockAiCrawlers } } };
        var aiFields = Render(context, ai, ai.Value);
        var list = aiFields.Find("[data-field=robots-crawlers]");
        Assert.Contains($"{RobotsText.AiCrawlers.Count} user agents", list.TextContent, StringComparison.Ordinal);
        Assert.Contains("GPTBot", list.TextContent, StringComparison.Ordinal);
        Assert.Contains("Ai2Bot-Dolma", list.TextContent, StringComparison.Ordinal);
        Assert.Contains("Disallow: /\n\nUser-agent: *", aiFields.Find("[data-field=robots-preview]").TextContent, StringComparison.Ordinal);
        Assert.Empty(aiFields.FindAll("[data-field=robots-custom-text]"));

        // Switching away from the custom mode drops this layer's text instead of leaving a document the validator refuses.
        Holder switching = new() { Value = new HostSettings { Robots = new RobotsSettings { Mode = RobotsMode.Custom, CustomText = "x" } } };
        var switchingFields = Render(context, switching, switching.Value);
        await SetSelect<RobotsMode>(switchingFields, "robots.txt", RobotsMode.DisallowAll);
        AssertSettings(new HostSettings { Robots = new RobotsSettings { Mode = RobotsMode.DisallowAll } }, switching.Value);

        // Inherit keeps it: a layer that sets only the text serves it under the custom mode a layer below resolves.
        Holder inheriting = new() { Value = new HostSettings { Robots = new RobotsSettings { CustomText = "B" } } };
        var inheritingFields = Render(
            context,
            inheriting,
            inheriting.Value,
            inherited: new HostSettings { Robots = new RobotsSettings { Mode = RobotsMode.Custom, CustomText = "A" } });
        await SetSelect<RobotsMode>(inheritingFields, "robots.txt", null);
        AssertSettings(new HostSettings { Robots = new RobotsSettings { CustomText = "B" } }, inheriting.Value);
    }

    [Fact]
    public void TheRowsCoverTheWholeContract()
    {
        // Walked from the contract itself, nested records included: a field added to HostSettings has to get its row here, or
        // this test names it. It is the dashboard's counterpart to SettingsInheritanceTests' completeness check.
        List<string> contract = [];
        foreach (var property in typeof(HostSettings).GetProperties())
        {
            if (typeof(ISettingsLayer).IsAssignableFrom(property.PropertyType))
            {
                foreach (var nested in property.PropertyType.GetProperties().Where(p => p.Name != nameof(ISettingsLayer.SetsNothing)))
                {
                    contract.Add($"{Camel(property.Name)}.{Camel(nested.Name)}");
                }

                continue;
            }

            contract.Add(Camel(property.Name));
        }

        Assert.Equal(contract, Fields.Select(row => row.Name));
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>The record the form would send, compared as JSON: the lists in it compare by what they hold, not by reference.</summary>
    private static void AssertSettings(HostSettings expected, HostSettings actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    /// <summary>The settings row around a field: the row MudBlazor puts the error text and class in.</summary>
    private static string Row(IRenderedComponent<HostSettingsFields> fields, string field)
    {
        var element = fields.Find($"[data-field={field}]");
        while (!element.ClassList.Contains("pdk-setting") && element.ParentElement is { } parent)
        {
            element = parent;
        }

        return element.OuterHtml;
    }

    private static IRenderedComponent<HostSettingsFields> Render(
        DashboardTestContext context,
        Holder holder,
        HostSettings settings,
        HostSettings? inherited = null,
        IReadOnlyDictionary<string, string>? errors = null) =>
        context.Render<HostSettingsFields>(parameters => parameters
            .Add(f => f.Settings, settings)
            .Add(f => f.SettingsChanged, (Action<HostSettings>)(value => holder.Value = value))
            .Add(f => f.Inherited, inherited ?? Inherited)
            .Add(f => f.InheritWord, InheritWord)
            .Add(f => f.PathPrefix, "/settings")
            .Add(f => f.Errors, errors ?? new Dictionary<string, string>())
            .Add(f => f.ShowAll, true)
            .Add(f => f.Policies, [SamplePolicyConfig]));

    private static Task SetSelect<T>(IRenderedComponent<HostSettingsFields> fields, string label, T? value)
        where T : struct =>
        fields.InvokeAsync(() => fields.FindComponents<MudSelect<T?>>()
            .Single(select => select.Instance.Label == label)
            .Instance.ValueChanged.InvokeAsync(value));

    /// <summary>Chooses the picker's inherit, no-limit, or nth-policy item by its key.</summary>
    private static Task SetRateLimit(IRenderedComponent<HostSettingsFields> fields, string key) =>
        fields.InvokeAsync(() => fields.FindComponents<MudSelect<string>>()
            .Single(select => select.Instance.Label == "Rate limit")
            .Instance.ValueChanged.InvokeAsync(key));

    private static async Task SetMaxAgeAsync(IRenderedComponent<HostSettingsFields> fields)
    {
        await SetChoice(fields, HstsMaxAgeChoice.Custom);
        await fields.InvokeAsync(() => fields.FindComponents<MudNumericField<int?>>().Single().Instance.ValueChanged.InvokeAsync(1234));
    }

    /// <summary>
    /// The preset-and-seconds mapping the HSTS row owns: a preset picked through the select is the number the config holds, and a
    /// stored number that is a preset reads back as its words, not as custom seconds.
    /// </summary>
    private static async Task AssertMaxAgePresetAsync(DashboardTestContext context, Field field)
    {
        Holder holder = new() { Value = field.Layer };
        var fields = Render(context, holder, field.Layer);

        await SetChoice(fields, HstsMaxAgeChoice.TwoYears);
        AssertSettings(new HostSettings { Hsts = new HstsSettings { Enabled = true, MaxAge = 63_072_000 } }, holder.Value);

        Holder stored = new() { Value = new HostSettings { Hsts = new HstsSettings { Enabled = true, MaxAge = 63_072_000 } } };
        var shown = Render(context, stored, stored.Value);
        var select = shown.Find("[data-field=hsts-max-age]").TextContent;
        Assert.Contains("2 years", select, StringComparison.Ordinal);
        Assert.DoesNotContain("Custom seconds", select, StringComparison.Ordinal);
    }

    private static Task SetChoice(IRenderedComponent<HostSettingsFields> fields, HstsMaxAgeChoice choice) =>
        fields.InvokeAsync(() => fields.FindComponents<MudSelect<HstsMaxAgeChoice>>()
            .Single(select => select.Instance.Label == "Remembered for")
            .Instance.ValueChanged.InvokeAsync(choice));

    private static Task SetLifetime(IRenderedComponent<HostSettingsFields> fields, TokenLifetimeChoice choice) =>
        fields.InvokeAsync(() => fields.FindComponents<MudSelect<TokenLifetimeChoice>>()
            .Single(select => select.Instance.Label == "Token lifetime")
            .Instance.ValueChanged.InvokeAsync(choice));

    private static Task Toggle(IRenderedComponent<HostSettingsFields> fields, string field, bool value) =>
        InputOf(fields, field).ChangeAsync(new() { Value = value });

    private static Task Input(IRenderedComponent<HostSettingsFields> fields, string field, string value) =>
        InputOf(fields, field).ChangeAsync(new() { Value = value });

    /// <summary>The control's input: MudBlazor puts the unmatched <c>data-field</c> on the input itself or on its wrapper.</summary>
    private static AngleSharp.Dom.IElement InputOf(IRenderedComponent<HostSettingsFields> fields, string field)
    {
        var element = fields.Find($"[data-field={field}]");
        return element.LocalName is "input" or "textarea" ? element : element.QuerySelector("input, textarea")!;
    }

    /// <summary>What the rendered fields last handed back: the form's state, as the page holds it.</summary>
    private sealed class Holder
    {
        public HostSettings Value { get; set; } = new();
    }

    /// <summary>
    /// One field of the contract: the key its control listens on, the layer that shows it, how the control sets it, the record
    /// that leaves, how it clears, and the words its inherit choice uses.
    /// </summary>
    private sealed record Field(
        string Name,
        string DataField,
        string Path,
        HostSettings Layer,
        Func<IRenderedComponent<HostSettingsFields>, Holder, Task> Set,
        HostSettings AfterSet,
        Func<IRenderedComponent<HostSettingsFields>, Holder, Task> Clear,
        HostSettings AfterClear,
        string? InheritText)
    {
        /// <summary>A layer that shows the field when an error has to be displayed at it (the IP allow list's text field is
        /// only there once the list is set).</summary>
        public HostSettings ErrorLayer => Name == "ipAllowList" ? AfterSet : Layer;

        /// <summary>The validator's path for this field, when it has one; null is a <see cref="GroupKey"/> row.</summary>
        public string? ValidatorPath { get; init; }

        /// <summary>Whether the validator's path is the one this control listens on, so the message reaches the row.</summary>
        public bool ShowsAtField { get; init; }

        /// <summary>True when the validator has no error of its own at this field: the control's key is broader, or there is
        /// no rule at all. The form-level summary is the only place a page shows such an error today.</summary>
        public bool GroupKey { get; init; }
    }
}
