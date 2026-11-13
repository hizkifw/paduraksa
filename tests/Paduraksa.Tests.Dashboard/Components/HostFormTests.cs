using Bunit;

using MudBlazor;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class HostFormTests
{
    private static readonly UpstreamConfig App = new(UpstreamId.New(), "app", Destinations: [new DestinationConfig(DestinationId.New(), "http://app:8080")]);

    [Fact]
    public async Task ANewHost_StartsAsAProxy_WithOneRouteToTheOnlyUpstream()
    {
        var model = HostEditModel.New(App.Id);
        await using DashboardTestContext context = new();

        var form = Render(context, model);

        Assert.Single(form.FindAll("[data-section=routes]"));
        Assert.Empty(form.FindAll("[data-section=redirect]"));
        var sent = model.ToConfig();
        Assert.Equal(HostKind.Proxy, sent.Kind);
        Assert.Equal(App.Id, Assert.Single(sent.Routes).UpstreamId);
        Assert.Equal(0, sent.Version);
    }

    [Theory]
    [InlineData("Redirect", HostKind.Redirect, "redirect")]
    [InlineData("Fixed answer", HostKind.Respond, "respond")]
    [InlineData("Admin", HostKind.Admin, "admin")]
    public async Task SwitchingTheKind_ShowsItsFields_AndSendsOnlyItsPart(string button, HostKind kind, string section)
    {
        var model = HostEditModel.New(App.Id);
        model.Names[0].Value = "app.example.com";
        await using DashboardTestContext context = new();
        var form = Render(context, model);

        form.FindAll("[data-field=kind] [role=radio]").Single(b => b.TextContent.Trim() == button).Click();

        Assert.Equal(kind, model.Kind);
        Assert.Single(form.FindAll($"[data-section={section}]"));
        Assert.Empty(form.FindAll("[data-section=routes]"));
        var sent = model.ToConfig();
        Assert.Empty(sent.Routes);
        Assert.Equal(kind == HostKind.Redirect, sent.Redirect is not null);
        Assert.Equal(kind == HostKind.Respond, sent.Respond is not null);
    }

    [Fact]
    public async Task ErrorsShowAtTheirFields_OnceReviewWasAttempted()
    {
        var model = HostEditModel.New(App.Id);
        model.Names[0].Value = "not a name";
        var errors = InlineValidation.ForHost(new ConfigDocument(Upstreams: [App]), model.ToConfig());
        await using DashboardTestContext context = new();

        var quiet = Render(context, model, errors, showAll: false);
        var shown = Render(context, model, errors, showAll: true);

        Assert.Contains("/names/0", errors.Keys);
        Assert.Empty(quiet.FindAll("[data-form=errors]"));
        Assert.DoesNotContain(errors["/names/0"], quiet.Markup, StringComparison.Ordinal);
        Assert.Single(shown.FindAll("[data-form=errors]"));
        Assert.Contains(errors["/names/0"], shown.Find("[data-form=errors]").TextContent, StringComparison.Ordinal);
        Assert.Contains("mud-input-error", shown.Find(".pdk-name-row").InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineValidation_FollowsTheRealRules_AgainstTheCurrentConfig()
    {
        HostConfig taken = new(HostId.New(), ["app.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(200));
        ConfigDocument current = new(Upstreams: [App], Hosts: [taken]);
        var model = HostEditModel.New(null);
        model.Names[0].Value = "app.example.com";

        var errors = InlineValidation.ForHost(current, model.ToConfig());

        Assert.Contains(errors.Keys, path => path.StartsWith("/names", StringComparison.Ordinal));

        // A hostname another host has says which host, by its title, not by its place in the config.
        Assert.Equal("'app.example.com' is already a hostname of app.example.com.", errors["/names/0"]);
        Assert.Contains(errors.Keys, path => path.StartsWith("/routes/0", StringComparison.Ordinal));

        // A route whose upstream wasn't picked asks for one, in the form's words, without an ID.
        Assert.Equal("Choose an upstream.", errors["/routes/0/upstreamId"]);

        model.Names[0].Value = "other.example.com";
        model.Routes[0].UpstreamId = App.Id;
        Assert.Empty(InlineValidation.ForHost(current, model.ToConfig()));

        // One pointing at an upstream that was deleted since says so, by what it was, not by its ID.
        model.Routes[0].UpstreamId = UpstreamId.New();
        model.ProfileId = ProfileId.New();
        errors = InlineValidation.ForHost(current, model.ToConfig());
        Assert.Equal("That upstream doesn't exist any more. Choose another one.", errors["/routes/0/upstreamId"]);
        Assert.Equal("That profile doesn't exist any more. Choose another one, or no profile.", errors["/profileId"]);
    }

    [Fact]
    public void Editing_KeepsTheStoredVersion_AndTheRoutesOwnSettings()
    {
        var route = RouteId.New();
        RouteSettings routeSettings = new() { AccessList = new AccessListSettings { AccessListId = AccessListId.New() } };
        HostConfig stored = new(HostId.New(), ["app.example.com"], Version: 7, Routes: [new RouteConfig(route, App.Id, Path: "/api", Settings: routeSettings)], Tags: ["media"]);

        var model = HostEditModel.From(stored);
        model.Enabled = false;
        var sent = model.ToConfig();

        Assert.Equal((stored.Id, 7L, false), (sent.Id, sent.Version, sent.Enabled));
        var kept = Assert.Single(sent.Routes);
        Assert.Equal((route, "/api"), (kept.Id, kept.Path));
        Assert.Same(routeSettings, kept.Settings);
        Assert.Equal(["media"], sent.Tags);
    }

    [Fact]
    public async Task TheRouteBrowserCheckSwitch_OverridesTheHost_AndKeepsTheRoutesOtherSettings()
    {
        var route = RouteId.New();
        RouteSettings settings = new() { AccessList = new AccessListSettings { AccessListId = AccessListId.New() } };
        var stored = new HostConfig(HostId.New(), ["app.example.com"], Routes: [new RouteConfig(route, App.Id, Settings: settings)]);
        var model = HostEditModel.From(stored);
        await using DashboardTestContext context = new();
        var popovers = context.Render<MudPopoverProvider>();
        var form = Render(context, model);

        // A route that sets its own access list shows its settings without the menu.
        Assert.Single(form.FindAll("[data-section='route-settings-0']"));
        Assert.NotNull(popovers);
        await form.InvokeAsync(() => form.FindComponents<MudSelect<bool?>>()
            .Single(select => select.Instance.Label == "Browser check on this route")
            .Instance.ValueChanged.InvokeAsync(false));

        var kept = Assert.Single(model.ToConfig().Routes);
        Assert.Equal((route, false), (kept.Id, kept.Settings.ProofOfWork?.Enabled));
        Assert.Same(settings.AccessList, kept.Settings.AccessList);
    }

    [Fact]
    public async Task ARouteThatOverridesShowsItsSettingsWithoutTheMenu()
    {
        var stored = new HostConfig(HostId.New(), ["app.example.com"],
            Routes: [new RouteConfig(RouteId.New(), App.Id, Settings: new RouteSettings { ProofOfWork = new ProofOfWorkSettings { Enabled = false } })]);
        await using DashboardTestContext context = new();

        var form = Render(context, HostEditModel.From(stored));

        Assert.Single(form.FindAll("[data-section='route-settings-0']"));
    }

    [Fact]
    public async Task ARouteThatNamesItsOwnAuthProviderShowsItsSettingsAndKeepsThemWhenAnotherIsChanged()
    {
        var provider = AuthProviderId.New();
        var route = RouteId.New();
        RouteSettings settings = new() { AuthProvider = new AuthProviderSettings { AuthProviderId = provider } };
        var model = HostEditModel.From(new HostConfig(HostId.New(), ["app.example.com"], Routes: [new RouteConfig(route, App.Id, Settings: settings)]));
        await using DashboardTestContext context = new();
        context.Render<MudPopoverProvider>();
        var form = Render(context, model);

        Assert.Single(form.FindAll("[data-section='route-settings-0']"));
        Assert.Single(form.FindAll("[data-field='route-auth-provider-0']"));
        await form.InvokeAsync(() => form.FindComponents<MudSelect<bool?>>()
            .Single(select => select.Instance.Label == "Browser check on this route")
            .Instance.ValueChanged.InvokeAsync(true));

        var kept = Assert.Single(model.ToConfig().Routes);
        Assert.Same(settings.AuthProvider, kept.Settings.AuthProvider);
        Assert.True(kept.Settings.ProofOfWork?.Enabled);

        // Turning the route's sign-in off (the route that serves the provider's own pages) is a record of its own, and inheriting again leaves none.
        await form.InvokeAsync(() => form.FindComponents<MudSelect<string>>()
            .Single(select => select.Instance.Label == "Sign-in on this route")
            .Instance.ValueChanged.InvokeAsync("off"));
        Assert.False(Assert.Single(model.ToConfig().Routes).Settings.AuthProvider?.Enabled);
        await form.InvokeAsync(() => form.FindComponents<MudSelect<string>>()
            .Single(select => select.Instance.Label == "Sign-in on this route")
            .Instance.ValueChanged.InvokeAsync("inherit"));
        Assert.Null(Assert.Single(model.ToConfig().Routes).Settings.AuthProvider);
    }

    [Fact]
    public async Task ARouteThatNamesItsOwnRuleSetShowsItsSettingsAndKeepsThemWhenAnotherIsChanged()
    {
        var set = RuleSetId.New();
        var route = RouteId.New();
        RouteSettings settings = new() { RuleSet = new RuleSetSettings { RuleSetId = set } };
        var model = HostEditModel.From(new HostConfig(HostId.New(), ["app.example.com"], Routes: [new RouteConfig(route, App.Id, Settings: settings)]));
        await using DashboardTestContext context = new();
        context.Render<MudPopoverProvider>();
        var form = Render(context, model);

        Assert.Single(form.FindAll("[data-section='route-settings-0']"));
        Assert.Single(form.FindAll("[data-field='route-rule-set-0']"));
        await form.InvokeAsync(() => form.FindComponents<MudSelect<bool?>>()
            .Single(select => select.Instance.Label == "Browser check on this route")
            .Instance.ValueChanged.InvokeAsync(true));

        var kept = Assert.Single(model.ToConfig().Routes);
        Assert.Same(settings.RuleSet, kept.Settings.RuleSet);
        Assert.True(kept.Settings.ProofOfWork?.Enabled);

        // Turning the route's rules off is a record of its own, and inheriting again leaves none.
        await form.InvokeAsync(() => form.FindComponents<MudSelect<string>>()
            .Single(select => select.Instance.Label == "Rules on this route")
            .Instance.ValueChanged.InvokeAsync("off"));
        Assert.False(Assert.Single(model.ToConfig().Routes).Settings.RuleSet?.Enabled);
        await form.InvokeAsync(() => form.FindComponents<MudSelect<string>>()
            .Single(select => select.Instance.Label == "Rules on this route")
            .Instance.ValueChanged.InvokeAsync("inherit"));
        Assert.Null(Assert.Single(model.ToConfig().Routes).Settings.RuleSet);
    }

    [Fact]
    public async Task TheRouteMenuEditsTheUpstreamAndRemovesTheRoute()
    {
        var model = HostEditModel.From(new HostConfig(HostId.New(), ["app.example.com"],
            Routes: [new RouteConfig(RouteId.New(), App.Id, Path: "/"), new RouteConfig(RouteId.New(), App.Id, Path: "/api")]));
        await using DashboardTestContext context = new();
        var popovers = context.Render<MudPopoverProvider>();
        UpstreamId? edited = null;
        var form = context.Render<HostForm>(parameters => parameters
            .Add(f => f.Model, model)
            .Add(f => f.Upstreams, [App])
            .Add(f => f.Errors, new Dictionary<string, string>())
            .Add(f => f.OnEditUpstream, id => edited = id));

        await form.Find("[data-action='route-menu-1'] button").ClickAsync(new());
        await popovers.Find("[data-action='edit-upstream']").ClickAsync(new());
        Assert.Equal(App.Id, edited);

        await form.Find("[data-action='route-menu-1'] button").ClickAsync(new());
        await popovers.Find("[data-action='remove-route']").ClickAsync(new());
        Assert.Equal(["/"], model.Routes.Select(r => r.Path));
    }

    [Fact]
    public async Task ReadOnly_OffersNoWayToAddOrRemove()
    {
        var model = HostEditModel.From(new HostConfig(HostId.New(), ["a.example.com", "b.example.com"], Routes: [new RouteConfig(RouteId.New(), App.Id)]));
        await using DashboardTestContext context = new();

        var form = Render(context, model, readOnly: true);

        Assert.DoesNotContain(form.FindAll("button"), b => b.TextContent.Contains("Add a", StringComparison.Ordinal));
        Assert.Empty(form.FindAll("button[aria-label^=Remove]"));
    }

    [Fact]
    public async Task TheAccessLogAndRequestIdFieldsAreInEveryLayerAndBindBackToTheHost()
    {
        await using DashboardTestContext context = new();
        var model = HostEditModel.New(App.Id);
        model.Names[0].Value = "app.example.com";
        var form = Render(context, model);

        // Both fields are there, inheriting by default, and say what they would inherit.
        Assert.Single(form.FindAll("[data-field=access-log]"));
        Assert.Single(form.FindAll("[data-field=request-id-propagation]"));
        Assert.Contains("Inherit (on)", form.Find("[data-field=access-log]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Inherit (upstream and the client)", form.Find("[data-field=request-id-propagation]").TextContent, StringComparison.Ordinal);
        // Each says what it is for in a line: what the log holds, and what the ID ties together.
        Assert.Contains("One line per request, whatever answered it.", form.Markup, StringComparison.Ordinal);
        Assert.Contains("ties a line in the access log to the same request in the app's own log", form.Markup, StringComparison.Ordinal);

        await form.InvokeAsync(async () =>
        {
            await form.FindComponents<MudSelect<bool?>>().Single(select => select.Instance.Label == "Access log").Instance.ValueChanged.InvokeAsync(false);
            await form.FindComponents<MudSelect<RequestIdPropagation?>>().Single(select => select.Instance.Label == "Request ID").Instance.ValueChanged.InvokeAsync(RequestIdPropagation.UpstreamOnly);
        });

        var sent = model.ToConfig();
        Assert.False(sent.Settings.AccessLog);
        Assert.Equal(RequestIdPropagation.UpstreamOnly, sent.Settings.RequestIdPropagation);

        // Clearing them goes back to inheriting: no key in the document.
        await form.InvokeAsync(async () =>
        {
            await form.FindComponents<MudSelect<bool?>>().Single(select => select.Instance.Label == "Access log").Instance.ValueChanged.InvokeAsync(null);
            await form.FindComponents<MudSelect<RequestIdPropagation?>>().Single(select => select.Instance.Label == "Request ID").Instance.ValueChanged.InvokeAsync(null);
        });
        Assert.Null(model.ToConfig().Settings.AccessLog);
        Assert.Null(model.ToConfig().Settings.RequestIdPropagation);
    }

    [Fact]
    public async Task TheRouteRateLimit_InheritsTheHostsPolicy_AndOverridesItForOneRoute()
    {
        RateLimitPolicyConfig strict = new(RateLimitPolicyId.New(), "Strict API", Requests: 10, Per: RateLimitPeriod.Second, Burst: 50);
        var model = HostEditModel.From(new HostConfig(HostId.New(), ["app.example.com"], Routes: [new RouteConfig(RouteId.New(), App.Id)]));
        await using DashboardTestContext context = new();
        var popovers = context.Render<MudPopoverProvider>();
        var form = context.Render<HostForm>(parameters => parameters
            .Add(f => f.Model, model)
            .Add(f => f.Upstreams, [App])
            .Add(f => f.RateLimits, [strict])
            .Add(f => f.Defaults, new HostSettings { RateLimit = new RateLimitSettings { PolicyId = strict.Id } })
            .Add(f => f.Errors, new Dictionary<string, string>()));

        await form.Find("[data-action='route-menu-0'] button").ClickAsync(new());
        await popovers.Find("[data-action='route-settings']").ClickAsync(new());

        // The route's inherit choice names the host's policy; the route can turn the limit off or name the policy itself.
        var field = form.Find("[data-field='route-rate-limit-0']");
        Assert.Contains("Inherit (Strict API, 10/s)", field.OuterHtml, StringComparison.Ordinal);
        await ChooseRateLimit(form, "off");
        Assert.Equal(new RateLimitSettings { Enabled = false }, model.ToConfig().Routes[0].Settings.RateLimit);
        await ChooseRateLimit(form, "p0");
        Assert.Equal(new RateLimitSettings { Enabled = true, PolicyId = strict.Id }, model.ToConfig().Routes[0].Settings.RateLimit);
        await ChooseRateLimit(form, "inherit");
        Assert.Null(model.ToConfig().Routes[0].Settings.RateLimit);
    }

    [Fact]
    public async Task TheRouteAccessList_InheritsTheHostsList_AndOverridesItForOneRoute()
    {
        AccessListConfig office = new(AccessListId.New(), "Office", Ip: new AccessListIp(["10.0.0.0/8"]));
        var model = HostEditModel.From(new HostConfig(HostId.New(), ["app.example.com"], Routes: [new RouteConfig(RouteId.New(), App.Id)]));
        await using DashboardTestContext context = new();
        var popovers = context.Render<MudPopoverProvider>();
        var form = context.Render<HostForm>(parameters => parameters
            .Add(f => f.Model, model)
            .Add(f => f.Upstreams, [App])
            .Add(f => f.AccessLists, [office])
            .Add(f => f.Defaults, new HostSettings { AccessList = new AccessListSettings { AccessListId = office.Id } })
            .Add(f => f.Errors, new Dictionary<string, string>()));

        await form.Find("[data-action='route-menu-0'] button").ClickAsync(new());
        await popovers.Find("[data-action='route-settings']").ClickAsync(new());

        // The route's inherit choice names the host's list; the route can turn it off or name the list itself.
        var field = form.Find("[data-field='route-access-list-0']");
        Assert.Contains("Inherit (Office)", field.OuterHtml, StringComparison.Ordinal);
        await ChooseAccessList(form, "off");
        Assert.Equal(new AccessListSettings { Enabled = false }, model.ToConfig().Routes[0].Settings.AccessList);
        await ChooseAccessList(form, "p0");
        Assert.Equal(new AccessListSettings { Enabled = true, AccessListId = office.Id }, model.ToConfig().Routes[0].Settings.AccessList);
        await ChooseAccessList(form, "inherit");
        Assert.Null(model.ToConfig().Routes[0].Settings.AccessList);
    }

    [Fact]
    public async Task TheRouteBodySize_InheritsTheHostsLimit_AndOverridesItForOneRoute()
    {
        var model = HostEditModel.From(new HostConfig(HostId.New(), ["app.example.com"], Routes: [new RouteConfig(RouteId.New(), App.Id)]));
        await using DashboardTestContext context = new();
        var popovers = context.Render<MudPopoverProvider>();
        var form = context.Render<HostForm>(parameters => parameters
            .Add(f => f.Model, model)
            .Add(f => f.Upstreams, [App])
            .Add(f => f.Defaults, new HostSettings { RequestLimits = new RequestLimitsSettings { MaxBodySize = 10 * 1024 * 1024 } })
            .Add(f => f.Errors, new Dictionary<string, string>()));

        await form.Find("[data-action='route-menu-0'] button").ClickAsync(new());
        await popovers.Find("[data-action='route-settings']").ClickAsync(new());

        // The route's inherit choice names the host's limit; an upload route can name its own, or none.
        Assert.Contains("Inherit (10 MiB)", form.Find("[data-field='route-max-body-size-0']").OuterHtml, StringComparison.Ordinal);
        await ChooseBodySize(form, "p1073741824");
        Assert.Equal(new RequestLimitsSettings { MaxBodySize = 1024L * 1024 * 1024 }, model.ToConfig().Routes[0].Settings.RequestLimits);
        await ChooseBodySize(form, "none");
        Assert.Equal(new RequestLimitsSettings { MaxBodySize = 0 }, model.ToConfig().Routes[0].Settings.RequestLimits);
        await ChooseBodySize(form, "inherit");
        Assert.Null(model.ToConfig().Routes[0].Settings.RequestLimits);
    }

    [Fact]
    public async Task TheRouteHeaders_ListTheHostsRules_AndAddTheRoutesOwn()
    {
        var model = HostEditModel.From(new HostConfig(
            HostId.New(),
            ["app.example.com"],
            Settings: new HostSettings { Headers = new HeadersSettings { Response = [new HeaderRuleConfig("X-Frame-Options", HeaderOperation.Set, "DENY")] } },
            Routes: [new RouteConfig(RouteId.New(), App.Id)]));
        await using DashboardTestContext context = new();
        var popovers = context.Render<MudPopoverProvider>();
        var form = Render(context, model);

        await form.Find("[data-action='route-menu-0'] button").ClickAsync(new());
        await popovers.Find("[data-action='route-settings']").ClickAsync(new());

        Assert.Contains("X-Frame-Options: DENY", form.Find("[data-field='route-headers-response-0']").TextContent, StringComparison.Ordinal);
        await form.InvokeAsync(() => form.Find("[data-action='route-headers-response-0-add']").Click());
        await form.InvokeAsync(() => form.FindComponents<MudTextField<string>>()
            .Single(text => text.Instance.UserAttributes.TryGetValue("data-field", out var value) && Equals(value, "route-headers-response-0-name-0"))
            .Instance.ValueChanged.InvokeAsync("X-Frame-Options"));

        Assert.Equal([new HeaderRuleConfig("X-Frame-Options")], model.ToConfig().Routes[0].Settings.Headers!.Response!);
        Assert.Contains("replaced below", form.Find("[data-field='route-headers-response-0']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRouteRewrite_StripsAndAddsAPrefix_AndSaysWhatIsSent()
    {
        var model = HostEditModel.From(new HostConfig(HostId.New(), ["app.example.com"], Routes: [new RouteConfig(RouteId.New(), App.Id, Path: "/app")]));
        await using DashboardTestContext context = new();
        var popovers = context.Render<MudPopoverProvider>();
        var form = Render(context, model);
        await form.Find("[data-action='route-menu-0'] button").ClickAsync(new());
        await popovers.Find("[data-action='route-settings']").ClickAsync(new());

        await form.InvokeAsync(() => form.FindComponents<MudSelect<bool>>().Single(s => s.Instance.Label == "Path sent upstream").Instance.ValueChanged.InvokeAsync(true));
        await form.InvokeAsync(() => form.FindComponents<MudTextField<string>>().Single(t => t.Instance.Label == "Prefix to add").Instance.ValueChanged.InvokeAsync("/api"));

        Assert.Equal(new PathRewriteConfig(StripPrefix: true, AddPrefix: "/api"), model.ToConfig().Routes[0].Rewrite);

        // A strip offers the Location switch; off is stored, on is the default and stores nothing.
        await form.InvokeAsync(() => form.FindComponents<MudSelect<bool>>().Single(s => s.Instance.Label == "Redirects from the upstream").Instance.ValueChanged.InvokeAsync(false));
        Assert.Equal(new PathRewriteConfig(StripPrefix: true, AddPrefix: "/api", RewriteLocation: false), model.ToConfig().Routes[0].Rewrite);
        await form.InvokeAsync(() => form.FindComponents<MudSelect<bool>>().Single(s => s.Instance.Label == "Redirects from the upstream").Instance.ValueChanged.InvokeAsync(true));
        Assert.Equal(new PathRewriteConfig(StripPrefix: true, AddPrefix: "/api"), model.ToConfig().Routes[0].Rewrite);
        Assert.Contains("/app/page is sent as /api/page", form.Find("[data-section='route-settings-0']").TextContent, StringComparison.Ordinal);

        await form.InvokeAsync(() => form.FindComponents<MudSelect<bool>>().Single(s => s.Instance.Label == "Path sent upstream").Instance.ValueChanged.InvokeAsync(false));
        await form.InvokeAsync(() => form.FindComponents<MudTextField<string>>().Single(t => t.Instance.Label == "Prefix to add").Instance.ValueChanged.InvokeAsync(string.Empty));
        Assert.Null(model.ToConfig().Routes[0].Rewrite);
    }

    private static Task ChooseBodySize(IRenderedComponent<HostForm> form, string key) =>
        form.InvokeAsync(() => form.FindComponents<MudSelect<string>>()
            .Single(select => select.Instance.Label == "Body size on this route")
            .Instance.ValueChanged.InvokeAsync(key));

    private static Task ChooseAccessList(IRenderedComponent<HostForm> form, string key) =>
        form.InvokeAsync(() => form.FindComponents<MudSelect<string>>()
            .Single(select => select.Instance.Label == "Access list on this route")
            .Instance.ValueChanged.InvokeAsync(key));

    private static Task ChooseRateLimit(IRenderedComponent<HostForm> form, string key) =>
        form.InvokeAsync(() => form.FindComponents<MudSelect<string>>()
            .Single(select => select.Instance.Label == "Rate limit on this route")
            .Instance.ValueChanged.InvokeAsync(key));

    private static IRenderedComponent<HostForm> Render(
        DashboardTestContext context, HostEditModel model, IReadOnlyDictionary<string, string>? errors = null, bool showAll = false, bool readOnly = false) =>
        context.Render<HostForm>(parameters => parameters
            .Add(f => f.Model, model)
            .Add(f => f.Upstreams, [App])
            .Add(f => f.Errors, errors ?? new Dictionary<string, string>())
            .Add(f => f.ShowAll, showAll)
            .Add(f => f.ReadOnly, readOnly));
}
