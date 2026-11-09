using Bunit;

using MudBlazor;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The host form (PLAN §12): kind switching, inline validation at the fields, and what it sends.</summary>
public sealed class HostFormTests
{
    private static readonly UpstreamConfig App = new(UpstreamId.New(), "app", Destinations: [new DestinationConfig(DestinationId.New(), "http://app:8080")]);

    [Fact]
    public async Task ANewHost_StartsAsAProxy_WithOneRouteToTheOnlyUpstream()
    {
        HostEditModel model = HostEditModel.New(App.Id);
        await using DashboardTestContext context = new();

        IRenderedComponent<HostForm> form = Render(context, model);

        Assert.Single(form.FindAll("[data-section=routes]"));
        Assert.Empty(form.FindAll("[data-section=redirect]"));
        HostConfig sent = model.ToConfig();
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
        HostEditModel model = HostEditModel.New(App.Id);
        model.Names[0].Value = "app.example.com";
        await using DashboardTestContext context = new();
        IRenderedComponent<HostForm> form = Render(context, model);

        form.FindAll(".mud-toggle-item").Single(b => b.TextContent.Trim() == button).Click();

        Assert.Equal(kind, model.Kind);
        Assert.Single(form.FindAll($"[data-section={section}]"));
        Assert.Empty(form.FindAll("[data-section=routes]"));
        HostConfig sent = model.ToConfig();
        Assert.Empty(sent.Routes);
        Assert.Equal(kind == HostKind.Redirect, sent.Redirect is not null);
        Assert.Equal(kind == HostKind.Respond, sent.Respond is not null);
    }

    [Fact]
    public async Task ErrorsShowAtTheirFields_OnceReviewWasAttempted()
    {
        HostEditModel model = HostEditModel.New(App.Id);
        model.Names[0].Value = "not a name";
        IReadOnlyDictionary<string, string> errors = InlineValidation.ForHost(new ConfigDocument(Upstreams: [App]), model.ToConfig());
        await using DashboardTestContext context = new();

        IRenderedComponent<HostForm> quiet = Render(context, model, errors, showAll: false);
        IRenderedComponent<HostForm> shown = Render(context, model, errors, showAll: true);

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
        HostEditModel model = HostEditModel.New(null);
        model.Names[0].Value = "app.example.com";

        IReadOnlyDictionary<string, string> errors = InlineValidation.ForHost(current, model.ToConfig());

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
    public void Editing_KeepsTheStoredVersion_AndWhatTheFormDoesNotShow()
    {
        RouteId route = RouteId.New();
        RouteSettings routeSettings = new() { IpAllowList = new IpAllowListSettings(["10.0.0.0/8"]) };
        HostConfig stored = new(HostId.New(), ["app.example.com"], Version: 7, Routes: [new RouteConfig(route, App.Id, Path: "/api", Settings: routeSettings)], Tags: ["media"]);

        HostEditModel model = HostEditModel.From(stored);
        model.Enabled = false;
        HostConfig sent = model.ToConfig();

        Assert.Equal((stored.Id, 7L, false), (sent.Id, sent.Version, sent.Enabled));
        RouteConfig kept = Assert.Single(sent.Routes);
        Assert.Equal((route, "/api"), (kept.Id, kept.Path));
        Assert.Same(routeSettings, kept.Settings);
        Assert.Equal(["media"], sent.Tags);
    }

    [Fact]
    public async Task ReadOnly_OffersNoWayToAddOrRemove()
    {
        HostEditModel model = HostEditModel.From(new HostConfig(HostId.New(), ["a.example.com", "b.example.com"], Routes: [new RouteConfig(RouteId.New(), App.Id)]));
        await using DashboardTestContext context = new();

        IRenderedComponent<HostForm> form = Render(context, model, readOnly: true);

        Assert.DoesNotContain(form.FindAll("button"), b => b.TextContent.Contains("Add a", StringComparison.Ordinal));
        Assert.Empty(form.FindAll("button[aria-label^=Remove]"));
    }

    [Fact]
    public async Task TheAccessLogAndRequestIdFieldsAreInEveryLayerAndBindBackToTheHost()
    {
        await using DashboardTestContext context = new();
        HostEditModel model = HostEditModel.New(App.Id);
        model.Names[0].Value = "app.example.com";
        IRenderedComponent<HostForm> form = Render(context, model);

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

        HostConfig sent = model.ToConfig();
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

    private static IRenderedComponent<HostForm> Render(
        DashboardTestContext context, HostEditModel model, IReadOnlyDictionary<string, string>? errors = null, bool showAll = false, bool readOnly = false) =>
        context.Render<HostForm>(parameters => parameters
            .Add(f => f.Model, model)
            .Add(f => f.Upstreams, [App])
            .Add(f => f.Errors, errors ?? new Dictionary<string, string>())
            .Add(f => f.ShowAll, showAll)
            .Add(f => f.ReadOnly, readOnly));
}
