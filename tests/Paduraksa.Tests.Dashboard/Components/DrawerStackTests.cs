using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Config;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The drawer stack: levels push and pop and hand a result back to the level below, Escape and the backdrop act on the top
/// level only, a level with unsaved changes asks before it goes, and an upstream made from a host's drawer is staged into the host's
/// draft and applied with it as one revision (and goes with it when the host is cancelled).
/// </summary>
public sealed class DrawerStackTests
{
    [Fact]
    public async Task ALevel_HandsItsResultBack_AndClosingOneClosesTheLevelsAboveIt()
    {
        DrawerStack stack = new();
        var bottom = stack.Push<NotFoundDrawer>(NotFoundDrawer.For("Bottom", "b"));
        var middle = stack.Push<NotFoundDrawer>(NotFoundDrawer.For("Middle", "m"));
        var top = stack.Push<NotFoundDrawer>(NotFoundDrawer.For("Top", "t"));
        Assert.Same(top, stack.Top);
        Assert.Same(middle, top.Parent);
        Assert.Equal([0, 1, 2], new[] { bottom.Depth, middle.Depth, top.Depth });

        top.Close("made");

        Assert.Equal("made", (await Done(top)).As<string>());
        Assert.Same(middle, stack.Top);

        // A level closing takes the ones above it along, cancelled; the ones below stay.
        var again = stack.Push<NotFoundDrawer>(NotFoundDrawer.For("Again", "a"));
        middle.Close(42);
        Assert.True((await Done(again)).Canceled);
        Assert.Equal(42, (await Done(middle)).As<int>());
        Assert.Same(bottom, stack.Top);

        stack.Clear();
        Assert.True((await Done(bottom)).Canceled);
        Assert.Empty(stack.Levels);
    }

    [Fact]
    public async Task Escape_ClosesTheTopLevelOnly_AndTheLevelsBelowAreInert()
    {
        await using DashboardTestContext context = new();
        context.SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        var host = context.Render<DrawerHost>();
        var stack = context.Services.GetRequiredService<DrawerStack>();
        DrawerLevel bottom = null!;
        DrawerLevel top = null!;
        await host.InvokeAsync(() =>
        {
            bottom = stack.Push<NotFoundDrawer>(NotFoundDrawer.For("New host", "b"), DrawerWidth.Wide);
            top = stack.Push<NotFoundDrawer>(NotFoundDrawer.For("New upstream", "t"));
        });

        Assert.Equal(2, host.FindAll(".pdk-sheet").Count);
        Assert.True(host.Find(".pdk-sheet[data-level='0']").HasAttribute("inert"));
        Assert.False(host.Find(".pdk-sheet[data-top]").HasAttribute("inert"));
        Assert.Equal("1", host.Find(".pdk-sheet[data-top]").GetAttribute("data-level"));
        Assert.Single(host.FindAll(".pdk-stack-backdrop"));
        Assert.Equal("Back to New host", host.Find(".pdk-sheet[data-top] [data-drawer-action=back]").GetAttribute("aria-label"));

        await host.InvokeAsync(host.Instance.EscapeAsync);

        Assert.True((await Done(top)).Canceled);
        Assert.True(bottom.IsOpen);
        Assert.Single(host.FindAll(".pdk-sheet"));
        Assert.False(host.Find(".pdk-sheet").HasAttribute("inert"));

        // The backdrop belongs to the top level too.
        await host.Find(".pdk-stack-backdrop").ClickAsync(new());
        Assert.Empty(host.FindAll(".pdk-sheet"));
        Assert.True((await Done(bottom)).Canceled);
    }

    [Fact]
    public async Task ALevelWithUnsavedChanges_AsksBeforeItCloses()
    {
        await using DashboardTestContext context = new();
        context.SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        var host = context.Render<DrawerHost>();
        var stack = context.Services.GetRequiredService<DrawerStack>();
        DrawerLevel level = null!;
        await host.InvokeAsync(() => level = stack.Push<NotFoundDrawer>(NotFoundDrawer.For("New host", "b")));
        level.IsDirty = true;

        // Escape asks, and the question takes focus with the drawer under it inert; Escape again is "keep editing", and focus goes back.
        await host.InvokeAsync(host.Instance.EscapeAsync);
        Assert.Single(host.FindAll("[data-drawer=discard]"));
        Assert.True(host.Find(".pdk-sheet[data-top] .pdk-sheet-content").HasAttribute("inert"));
        context.JSInterop.VerifyInvoke("paduraksa.drawers.ask");
        Assert.True(level.IsOpen);
        await host.InvokeAsync(host.Instance.EscapeAsync);
        Assert.Empty(host.FindAll("[data-drawer=discard]"));
        Assert.False(host.Find(".pdk-sheet[data-top] .pdk-sheet-content").HasAttribute("inert"));
        context.JSInterop.VerifyInvoke("paduraksa.drawers.answered");
        Assert.True(level.IsOpen);

        // The close button asks too; "Keep editing" keeps it, "Discard" closes it without a result.
        await host.Find("[data-drawer-action=close]").ClickAsync(new());
        await host.Find("[data-drawer=keep]").ClickAsync(new());
        Assert.True(level.IsOpen);
        await host.Find("[data-drawer-action=close]").ClickAsync(new());
        await host.Find("[data-drawer=discard-confirm]").ClickAsync(new());

        Assert.True((await Done(level)).Canceled);
        Assert.Empty(host.FindAll(".pdk-sheet"));
    }

    [Fact]
    public async Task ABusyLevel_StaysOpenUntilItIsDone()
    {
        DrawerStack stack = new();
        var level = stack.Push<NotFoundDrawer>(NotFoundDrawer.For("Review", "r"));
        level.Busy = true;

        stack.Escape();
        level.RequestClose();

        Assert.True(level.IsOpen);
        level.Busy = false;
        stack.Escape();
        Assert.True((await Done(level)).Canceled);
    }

    [Fact]
    public async Task AnUpstreamMadeFromTheHostDrawer_IsAppliedWithTheHost_AsOneRevision()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument();
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Hosts>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=host] input[data-field=name-0]").Count == 1);
        await TypeAsync(drawers, "[data-drawer=host] input[data-field=name-0]", "jelly.home.test");
        Assert.Equal("jelly.home.test", drawers.FindComponent<HostForm>().Instance.Model.Names[0].Value);

        // "New upstream..." on the first route stacks the upstream form on the host's.
        var upstream = await MakeUpstreamAsync(drawers, "jellyfin", "http://10.0.0.5:8096");

        Assert.Single(drawers.FindAll(".pdk-sheet"));
        Assert.Contains("jellyfin", drawers.Find("[data-staged=jellyfin]").TextContent, StringComparison.Ordinal);
        Assert.Empty(config.Executed);

        // Review changes stacks on the host and holds the host's submit until it closes.
        var reviewing = drawers.Find("form[data-drawer=host]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);
        Assert.Equal(2, drawers.FindAll(".pdk-sheet").Count);
        await drawers.Find("[data-review=apply]").ClickAsync(new());
        await reviewing.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);

        // One dry run and one commit, both of the whole document: the new upstream and the host that routes to it, together.
        Assert.Equal(2, config.Executed.Count);
        var applied = Assert.IsType<ReplaceConfigCommand>(config.Executed[^1]);
        Assert.False(applied.DryRun);
        Assert.True(config.Executed[0].DryRun);
        Assert.Equal(upstream.Id, Assert.Single(applied.Document.Upstreams).Id);
        var host = Assert.Single(applied.Document.Hosts);
        Assert.Equal(["jelly.home.test"], host.Names);
        Assert.Equal(upstream.Id, Assert.Single(host.Routes).UpstreamId);
        Assert.Equal("New host jelly.home.test with upstream jellyfin", applied.Message);
        await drawers.WaitForStateAsync(() => drawers.FindAll(".pdk-sheet").Count == 0);
    }

    [Fact]
    public async Task AStagedUpstream_OpensAgainToBeChanged_AndAReviewsRefusalOfIt_ShowsAtItsFields()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument();
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Hosts>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=host] input[data-field=name-0]").Count == 1);
        await TypeAsync(drawers, "[data-drawer=host] input[data-field=name-0]", "app.home.test");
        var made = await MakeUpstreamAsync(drawers, "app", "http://10.0.0.7:8080");

        // Opened again from the host's drawer: still the same new upstream, with what it was given; the change comes back staged.
        var editing = drawers.Find("[data-staged=app] [data-action=edit-staged]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=upstream] input[data-field=name]").Count == 1);
        Assert.Equal("app", drawers.Find("[data-drawer=upstream] input[data-field=name]").GetAttribute("value"));
        Assert.StartsWith("Not applied yet", drawers.Find("[data-drawer=upstream] .pdk-drawer-subtitle").TextContent, StringComparison.Ordinal);
        await TypeAsync(drawers, "[data-drawer=upstream] input[data-field=name]", "app-backend");
        await drawers.Find("form[data-drawer=upstream]").SubmitAsync();
        await editing.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        Assert.Single(drawers.FindAll("[data-staged=app-backend]"));
        Assert.Equal(made.Id, drawers.FindComponent<HostForm>().Instance.Model.Routes[0].UpstreamId);

        // The dry run refuses the staged upstream's address: the review names it, and the upstream is marked and shows it at its field.
        config.Results = command => command is ReplaceConfigCommand { DryRun: true }
            ? new ConfigCommandResult(ConfigCommandStatus.Invalid, Errors: [new ConfigError("/upstreams/0/destinations/0/address", "upstream.address.invalid", "the address points at this proxy")])
            : new ConfigCommandResult(ConfigCommandStatus.DryRun);
        var reviewing = drawers.Find("form[data-drawer=host]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=invalid]").Count == 1);
        Assert.Contains("app-backend", drawers.Find("[data-review=invalid] .pdk-error-where").TextContent, StringComparison.Ordinal);
        await drawers.InvokeAsync(drawers.Instance.EscapeAsync);
        await reviewing.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        Assert.Single(drawers.FindAll("[data-staged=app-backend] [data-chip=staged-error]"));

        _ = drawers.Find("[data-staged=app-backend] [data-action=edit-staged]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=upstream]").Count == 1);
        Assert.Contains("The address points at this proxy.", drawers.Find("[data-drawer=upstream]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADrawersTrail_NamesEveryLevelUnderIt()
    {
        await using DashboardTestContext context = new();
        context.SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        var host = context.Render<DrawerHost>();
        var stack = context.Services.GetRequiredService<DrawerStack>();
        await host.InvokeAsync(() =>
        {
            stack.Push<NotFoundDrawer>(NotFoundDrawer.For("app.home.test", "a"), DrawerWidth.Wide);
            stack.Push<NotFoundDrawer>(NotFoundDrawer.For("New certificate", "b"));
            stack.Push<NotFoundDrawer>(NotFoundDrawer.For("Add an ACME account", "c"), DrawerWidth.Narrow);
        });

        Assert.Equal(["app.home.test", "New certificate", "Add an ACME account"], host.FindAll(".pdk-sheet[data-top] [data-drawer=trail] li").Select(li => li.TextContent));
        Assert.Equal("Back to New certificate", host.Find(".pdk-sheet[data-top] [data-drawer-action=back]").GetAttribute("aria-label"));
        Assert.Empty(host.FindAll(".pdk-sheet[data-level='0'] [data-drawer=trail]"));
    }

    [Fact]
    public async Task AStagedUpstreamNoRouteUses_SaysItWontBeCreated()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument();
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Hosts>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=host] input[data-field=name-0]").Count == 1);
        await MakeUpstreamAsync(drawers, "app", "http://10.0.0.7:8080");
        Assert.Equal("true", drawers.Find("[data-staged=app]").GetAttribute("data-used"));

        // The route goes elsewhere: the staged upstream stays listed, but says it won't be created with the host.
        var form = drawers.FindComponent<HostForm>().Instance;
        await drawers.InvokeAsync(() =>
        {
            form.Model.Routes[0].UpstreamId = null;
            return form.OnChanged.InvokeAsync();
        });

        Assert.Equal("false", drawers.Find("[data-staged=app]").GetAttribute("data-used"));
        Assert.Contains("Won't be created: no route uses it", drawers.Find("[data-staged=app] [data-chip=staged-unused]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavingAPageWithADirtyRoutedDrawer_GoesWhereThePersonWent_NotBackToTheList()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        UpstreamConfig app = new(UpstreamId.New(), "app", Version: 1, Destinations: [new DestinationConfig(DestinationId.New(), "http://app:8080")]);
        HostConfig host = new(HostId.New(), ["app.home.test"], Version: 1, Routes: [new RouteConfig(RouteId.New(), app.Id)]);
        config.Document = new ConfigDocument(Upstreams: [app], Hosts: [host]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(DashboardRoutes.Host(host.Id));
        var page = context.Render<Hosts>(parameters => parameters.Add(p => p.Key, host.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=host] input[data-field=name-0]").Count == 1);
        await TypeAsync(drawers, "[data-drawer=host] input[data-field=name-0]", "changed.home.test");

        await drawers.InvokeAsync(() => navigation.NavigateTo(DashboardRoutes.Upstreams));
        Assert.Single(drawers.FindAll("[data-drawer=discard]"));
        Assert.EndsWith(DashboardRoutes.Host(host.Id), navigation.Uri, StringComparison.Ordinal);
        await drawers.Find("[data-drawer=discard-confirm]").ClickAsync(new());

        // The upstreams, as asked; the drawer goes with the hosts page.
        await drawers.WaitForStateAsync(() => navigation.Uri.EndsWith(DashboardRoutes.Upstreams, StringComparison.Ordinal));
        page.Instance.Dispose();
        Assert.Empty(context.Services.GetRequiredService<DrawerStack>().Levels);
        Assert.EndsWith(DashboardRoutes.Upstreams, navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADrawerThatTakesThePlaceOfTheTopOne_TakesFocus()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Viewer);
        HostConfig one = new(HostId.New(), ["one.home.test"], Version: 1, Kind: HostKind.Respond, Respond: new RespondConfig(200, "one"));
        HostConfig two = new(HostId.New(), ["two.home.test"], Version: 1, Kind: HostKind.Respond, Respond: new RespondConfig(200, "two"));
        config.Document = new ConfigDocument(Hosts: [one, two]);
        var drawers = context.Render<DrawerHost>();
        var page = context.Render<Hosts>(parameters => parameters.Add(p => p.Key, one.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=host] .pdk-form").Count == 1);
        Assert.Empty(context.JSInterop.Invocations["paduraksa.drawers.focusTop"]);

        page.Render(parameters => parameters.Add(p => p.Key, two.Id.ToString()));

        await drawers.WaitForStateAsync(() => drawers.Find(".pdk-drawer-title").TextContent == "two.home.test");
        Assert.Single(drawers.FindAll(".pdk-sheet"));
        context.JSInterop.VerifyInvoke("paduraksa.drawers.focusTop");
    }

    [Fact]
    public async Task CancellingTheHost_LeavesNoUpstreamBehind()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument();
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Hosts>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=host] input[data-field=name-0]").Count == 1);
        await MakeUpstreamAsync(drawers, "app", "http://10.0.0.7:8080");

        // The staged upstream is an unsaved change: closing asks, and discarding drops it with the host.
        await drawers.Find("[data-drawer=host] [data-action=cancel]").ClickAsync(new());
        await drawers.Find("[data-drawer=discard-confirm]").ClickAsync(new());

        Assert.Empty(drawers.FindAll(".pdk-sheet"));
        Assert.Empty(config.Executed);
    }

    [Fact]
    public async Task ARateLimitMadeFromTheHostDrawer_IsStagedAndSelected_AndAppliedWithTheHost()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        UpstreamConfig app = new(UpstreamId.New(), "app", Destinations: [new DestinationConfig(DestinationId.New(), "http://app:8080")]);
        config.Document = new ConfigDocument(Upstreams: [app]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Hosts>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=host] input[data-field=name-0]").Count == 1);
        await TypeAsync(drawers, "[data-drawer=host] input[data-field=name-0]", "app.home.test");

        // "New rate limit..." on the host's Access row stacks the policy editor on the host's.
        var picker = drawers.FindComponents<RateLimitField>().Single(field => field.Instance.Field == "rate-limit");
        var choosing = drawers.InvokeAsync(() => picker.Instance.OnNew.InvokeAsync());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=rate-limit] input[data-field=name]").Count == 1);
        Assert.Equal(2, drawers.FindAll(".pdk-sheet").Count);
        Assert.Equal("Back to New host", drawers.Find("[data-drawer=rate-limit] [data-drawer-action=back]").GetAttribute("aria-label"));
        await TypeAsync(drawers, "[data-drawer=rate-limit] input[data-field=name]", "Strict API");
        await drawers.Find("form[data-drawer=rate-limit]").SubmitAsync();
        await choosing.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);

        // Staged with the host, and the host's picker now names it; nothing is sent yet.
        var hosted = drawers.FindComponent<HostForm>().Instance.Model;
        var chosen = hosted.Settings.RateLimit;
        Assert.True(chosen?.Enabled);
        Assert.NotNull(chosen?.PolicyId);
        Assert.Single(drawers.FindAll("[data-staged='Strict API']"));
        Assert.Contains("Strict API", drawers.Find("[data-field=rate-limit]").OuterHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(chosen!.PolicyId.ToString()!, drawers.Markup, StringComparison.Ordinal);
        Assert.Empty(config.Executed);

        // The host and the policy go in one revision.
        _ = drawers.Find("form[data-drawer=host]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);
        await drawers.Find("[data-review=apply]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => config.Executed.Count == 2);

        var applied = Assert.IsType<ReplaceConfigCommand>(config.Executed[^1]);
        Assert.False(applied.DryRun);
        Assert.Equal("Strict API", Assert.Single(applied.Document.RateLimitPolicies).Name);
        Assert.Equal(Assert.Single(applied.Document.Hosts).Settings.RateLimit?.PolicyId, chosen!.PolicyId);
    }

    [Fact]
    public async Task AnAccessListMadeFromTheHostDrawer_IsStagedAndSelected_AndAppliedWithTheHost()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        UpstreamConfig app = new(UpstreamId.New(), "app", Destinations: [new DestinationConfig(DestinationId.New(), "http://app:8080")]);
        config.Document = new ConfigDocument(Upstreams: [app]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Hosts>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=host] input[data-field=name-0]").Count == 1);
        await TypeAsync(drawers, "[data-drawer=host] input[data-field=name-0]", "app.home.test");

        // "New access list..." on the host's Access row stacks the list editor on the host's.
        var picker = drawers.FindComponents<AccessListField>().Single(field => field.Instance.Field == "access-list");
        var choosing = drawers.InvokeAsync(() => picker.Instance.OnNew.InvokeAsync());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=access-list] input[data-field=name]").Count == 1);
        Assert.Equal(2, drawers.FindAll(".pdk-sheet").Count);
        Assert.Equal("Back to New host", drawers.Find("[data-drawer=access-list] [data-drawer-action=back]").GetAttribute("aria-label"));
        await TypeAsync(drawers, "[data-drawer=access-list] input[data-field=name]", "Office");
        await TypeAsync(drawers, "[data-drawer=access-list] textarea[data-field=allowed]", "10.0.0.0/8");
        await drawers.Find("form[data-drawer=access-list]").SubmitAsync();
        await choosing.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);

        // Staged with the host, and the host's picker now names it; nothing is sent yet.
        var hosted = drawers.FindComponent<HostForm>().Instance.Model;
        var chosen = hosted.Settings.AccessList;
        Assert.True(chosen?.Enabled);
        Assert.NotNull(chosen?.AccessListId);
        Assert.Single(drawers.FindAll("[data-staged='Office']"));
        Assert.Contains("Office", drawers.Find("[data-field=access-list]").OuterHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(chosen!.AccessListId.ToString()!, drawers.Markup, StringComparison.Ordinal);
        Assert.Empty(config.Executed);

        // The host and the list go in one revision.
        _ = drawers.Find("form[data-drawer=host]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);
        await drawers.Find("[data-review=apply]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => config.Executed.Count == 2);

        var applied = Assert.IsType<ReplaceConfigCommand>(config.Executed[^1]);
        Assert.False(applied.DryRun);
        var list = Assert.Single(applied.Document.AccessLists);
        Assert.Equal("Office", list.Name);
        Assert.Equal(["10.0.0.0/8"], list.Ip!.Allow);
        Assert.Equal(Assert.Single(applied.Document.Hosts).Settings.AccessList?.AccessListId, chosen.AccessListId);
    }

    [Fact]
    public async Task AnAuthProviderMadeFromTheHostDrawer_IsStagedAndSelected_AndAppliedWithTheHost()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        UpstreamConfig app = new(UpstreamId.New(), "app", Destinations: [new DestinationConfig(DestinationId.New(), "http://app:8080")]);
        config.Document = new ConfigDocument(Upstreams: [app]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Hosts>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=host] input[data-field=name-0]").Count == 1);
        await TypeAsync(drawers, "[data-drawer=host] input[data-field=name-0]", "app.home.test");

        // "New auth provider..." on the host's Access row stacks the provider editor on the host's.
        var picker = drawers.FindComponents<AuthProviderField>().Single(field => field.Instance.Field == "auth-provider");
        var choosing = drawers.InvokeAsync(() => picker.Instance.OnNew.InvokeAsync());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=auth-provider] input[data-field=name]").Count == 1);
        Assert.Equal(2, drawers.FindAll(".pdk-sheet").Count);
        Assert.Equal("Back to New host", drawers.Find("[data-drawer=auth-provider] [data-drawer-action=back]").GetAttribute("aria-label"));
        await TypeAsync(drawers, "[data-drawer=auth-provider] input[data-field=name]", "Authelia");
        await drawers.InvokeAsync(() => drawers.FindComponents<MudSelect<ForwardAuthPreset>>().Single().Instance.ValueChanged.InvokeAsync(ForwardAuthPreset.Authelia));
        await TypeAsync(drawers, "[data-drawer=auth-provider] input[data-field=url]", "http://authelia:9091");
        Assert.Contains("Use this provider", drawers.Find("[data-drawer=auth-provider] [data-action=stage]").TextContent, StringComparison.Ordinal);
        await drawers.Find("form[data-drawer=auth-provider]").SubmitAsync();
        await choosing.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);

        // Staged with the host, and the host's picker now names it; nothing is sent yet.
        var hosted = drawers.FindComponent<HostForm>().Instance.Model;
        var chosen = hosted.Settings.AuthProvider;
        Assert.True(chosen?.Enabled);
        Assert.NotNull(chosen?.AuthProviderId);
        Assert.Single(drawers.FindAll("[data-staged='Authelia']"));
        Assert.Contains("Authelia", drawers.Find("[data-field=auth-provider]").OuterHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(chosen!.AuthProviderId.ToString()!, drawers.Markup, StringComparison.Ordinal);
        Assert.Empty(config.Executed);

        // The host and the provider go in one revision.
        _ = drawers.Find("form[data-drawer=host]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);
        await drawers.Find("[data-review=apply]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => config.Executed.Count == 2);

        var applied = Assert.IsType<ReplaceConfigCommand>(config.Executed[^1]);
        Assert.False(applied.DryRun);
        var provider = Assert.Single(applied.Document.AuthProviders);
        Assert.Equal("Authelia", provider.Name);
        Assert.Equal(new ForwardAuthConfig("http://authelia:9091", ForwardAuthPreset.Authelia), provider.ForwardAuth);
        Assert.Equal(Assert.Single(applied.Document.Hosts).Settings.AuthProvider?.AuthProviderId, chosen.AuthProviderId);
    }

    [Fact]
    public async Task AStagedAuthProviderNoHostOrRouteNamesIsLeftOutAndSaysSo()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        UpstreamConfig app = new(UpstreamId.New(), "app", Destinations: [new DestinationConfig(DestinationId.New(), "http://app:8080")]);
        config.Document = new ConfigDocument(Upstreams: [app]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Hosts>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=host] input[data-field=name-0]").Count == 1);
        await TypeAsync(drawers, "[data-drawer=host] input[data-field=name-0]", "app.home.test");
        var picker = drawers.FindComponents<AuthProviderField>().Single(field => field.Instance.Field == "auth-provider");
        var choosing = drawers.InvokeAsync(() => picker.Instance.OnNew.InvokeAsync());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=auth-provider] input[data-field=name]").Count == 1);
        await TypeAsync(drawers, "[data-drawer=auth-provider] input[data-field=name]", "Spare");
        await TypeAsync(drawers, "[data-drawer=auth-provider] input[data-field=url]", "http://auth:9091");
        await drawers.Find("form[data-drawer=auth-provider]").SubmitAsync();
        await choosing.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);

        // The host picks no sign-in after all: the provider is no longer used, and the host goes alone.
        await drawers.InvokeAsync(() => drawers.FindComponents<MudSelect<string>>().Single(select => select.Instance.Label == "Sign-in").Instance.ValueChanged.InvokeAsync("off"));

        Assert.Equal("false", drawers.Find("[data-staged='Spare']").GetAttribute("data-used"));
        Assert.Contains("Won't be created", drawers.Find("[data-staged='Spare']").TextContent, StringComparison.Ordinal);
        _ = drawers.Find("form[data-drawer=host]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);
        Assert.IsType<UpsertHostCommand>(config.Executed[0]);
    }

    [Fact]
    public async Task ARuleSetMadeFromTheHostDrawer_IsStagedAndSelected_AndAppliedWithTheHost()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        UpstreamConfig app = new(UpstreamId.New(), "app", Destinations: [new DestinationConfig(DestinationId.New(), "http://app:8080")]);
        config.Document = new ConfigDocument(Upstreams: [app]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Hosts>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=host] input[data-field=name-0]").Count == 1);
        await TypeAsync(drawers, "[data-drawer=host] input[data-field=name-0]", "app.home.test");

        // "New rule set..." on the host's Access row stacks the rule editor on the host's.
        var picker = drawers.FindComponents<RuleSetField>().Single(field => field.Instance.Field == "rule-set");
        var choosing = drawers.InvokeAsync(() => picker.Instance.OnNew.InvokeAsync());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=rule-set] input[data-field=name]").Count == 1);
        Assert.Equal(2, drawers.FindAll(".pdk-sheet").Count);
        Assert.Equal("Back to New host", drawers.Find("[data-drawer=rule-set] [data-drawer-action=back]").GetAttribute("aria-label"));
        await TypeAsync(drawers, "[data-drawer=rule-set] input[data-field=name]", "Block admin");
        await drawers.Find("[data-action=add-rule]").ClickAsync(new());
        await drawers.InvokeAsync(() => drawers.FindComponents<MudSelect<RulePathKind?>>().Single().Instance.ValueChanged.InvokeAsync(RulePathKind.Prefix));
        await TypeAsync(drawers, "[data-drawer=rule-set] input[data-field=pattern-0]", "/admin");
        await drawers.Find("form[data-drawer=rule-set]").SubmitAsync();
        await choosing.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);

        // Staged with the host, and the host's picker now names it; nothing is sent yet.
        var hosted = drawers.FindComponent<HostForm>().Instance.Model;
        var chosen = hosted.Settings.RuleSet;
        Assert.True(chosen?.Enabled);
        Assert.NotNull(chosen?.RuleSetId);
        Assert.Single(drawers.FindAll("[data-staged='Block admin']"));
        Assert.Contains("Block admin", drawers.Find("[data-field=rule-set]").OuterHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(chosen!.RuleSetId.ToString()!, drawers.Markup, StringComparison.Ordinal);
        Assert.Empty(config.Executed);

        // The host and the rule set go in one revision.
        _ = drawers.Find("form[data-drawer=host]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);
        await drawers.Find("[data-review=apply]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => config.Executed.Count == 2);

        var applied = Assert.IsType<ReplaceConfigCommand>(config.Executed[^1]);
        Assert.False(applied.DryRun);
        var set = Assert.Single(applied.Document.RuleSets);
        Assert.Equal("Block admin", set.Name);
        Assert.Equal(new RulePathConfig(RulePathKind.Prefix, "/admin"), Assert.Single(set.Rules).Match.Path);
        Assert.Equal(Assert.Single(applied.Document.Hosts).Settings.RuleSet?.RuleSetId, chosen.RuleSetId);
    }

    /// <summary>What a level handed back; fails rather than waits forever when it is still open.</summary>
    private static Task<DrawerResult> Done(DrawerLevel level) => level.Result.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);

    /// <summary>Types into a MudBlazor field and leaves it (which sends what was typed at once, instead of after the field's debounce).</summary>
    private static async Task TypeAsync(IRenderedComponent<DrawerHost> drawers, string selector, string value)
    {
        await drawers.Find(selector).InputAsync(new() { Value = value });
        await drawers.Find(selector).BlurAsync(new());
    }

    /// <summary>Chooses "New upstream..." on the host's first route, fills the upstream drawer and hands it back.</summary>
    private static async Task<UpstreamConfig> MakeUpstreamAsync(IRenderedComponent<DrawerHost> drawers, string name, string address)
    {
        var form = drawers.FindComponent<HostForm>().Instance;
        var route = Assert.Single(form.Model.Routes);
        var choosing = drawers.InvokeAsync(() => form.OnNewUpstream.InvokeAsync(route));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=upstream] input[data-field=name]").Count == 1);
        Assert.Equal(2, drawers.FindAll(".pdk-sheet").Count);
        Assert.Equal("Back to New host", drawers.Find("[data-drawer=upstream] [data-drawer-action=back]").GetAttribute("aria-label"));

        await TypeAsync(drawers, "[data-drawer=upstream] input[data-field=name]", name);
        await TypeAsync(drawers, "[data-drawer=upstream] input[data-field=destination-0]", address);
        await drawers.Find("form[data-drawer=upstream]").SubmitAsync();
        await choosing.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);

        var id = route.UpstreamId ?? throw new InvalidOperationException("The route didn't get the new upstream.");
        return new UpstreamConfig(id, name, Destinations: [new DestinationConfig(DestinationId.New(), address)]);
    }
}
