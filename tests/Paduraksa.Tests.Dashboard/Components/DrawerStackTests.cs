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
using Paduraksa.Dashboard.Editors;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The drawer stack (PLAN §12): levels push and pop and hand a result back to the level below, Escape and the backdrop act on the top
/// level only, a level with unsaved changes asks before it goes, and an upstream made from a host's drawer is staged into the host's
/// draft and applied with it as one revision (and goes with it when the host is cancelled).
/// </summary>
public sealed class DrawerStackTests
{
    [Fact]
    public async Task ALevel_HandsItsResultBack_AndClosingOneClosesTheLevelsAboveIt()
    {
        DrawerStack stack = new();
        DrawerLevel bottom = stack.Push<NotFoundDrawer>(NotFoundDrawer.For("Bottom", "b"));
        DrawerLevel middle = stack.Push<NotFoundDrawer>(NotFoundDrawer.For("Middle", "m"));
        DrawerLevel top = stack.Push<NotFoundDrawer>(NotFoundDrawer.For("Top", "t"));
        Assert.Same(top, stack.Top);
        Assert.Same(middle, top.Parent);
        Assert.Equal([0, 1, 2], new[] { bottom.Depth, middle.Depth, top.Depth });

        top.Close("made");

        Assert.Equal("made", (await Done(top)).As<string>());
        Assert.Same(middle, stack.Top);

        // A level closing takes the ones above it along, cancelled; the ones below stay.
        DrawerLevel again = stack.Push<NotFoundDrawer>(NotFoundDrawer.For("Again", "a"));
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
        IRenderedComponent<DrawerHost> host = context.Render<DrawerHost>();
        DrawerStack stack = context.Services.GetRequiredService<DrawerStack>();
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
        IRenderedComponent<DrawerHost> host = context.Render<DrawerHost>();
        DrawerStack stack = context.Services.GetRequiredService<DrawerStack>();
        DrawerLevel level = null!;
        await host.InvokeAsync(() => level = stack.Push<NotFoundDrawer>(NotFoundDrawer.For("New host", "b")));
        level.IsDirty = true;

        // Escape asks; Escape again is "keep editing".
        await host.InvokeAsync(host.Instance.EscapeAsync);
        Assert.Single(host.FindAll("[data-drawer=discard]"));
        Assert.True(level.IsOpen);
        await host.InvokeAsync(host.Instance.EscapeAsync);
        Assert.Empty(host.FindAll("[data-drawer=discard]"));
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
        DrawerLevel level = stack.Push<NotFoundDrawer>(NotFoundDrawer.For("Review", "r"));
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
        FakeDashboardConfig config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument();
        context.Render<MudPopoverProvider>();
        IRenderedComponent<DrawerHost> drawers = context.Render<DrawerHost>();
        context.Render<Hosts>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=host] input[data-field=name-0]").Count == 1);
        await TypeAsync(drawers, "[data-drawer=host] input[data-field=name-0]", "jelly.home.test");
        Assert.Equal("jelly.home.test", drawers.FindComponent<HostForm>().Instance.Model.Names[0].Value);

        // "New upstream..." on the first route stacks the upstream form on the host's.
        UpstreamConfig upstream = await MakeUpstreamAsync(drawers, "jellyfin", "http://10.0.0.5:8096");

        Assert.Single(drawers.FindAll(".pdk-sheet"));
        Assert.Contains("upstream jellyfin", drawers.Find("[data-section=staged]").TextContent, StringComparison.Ordinal);
        Assert.Empty(config.Executed);

        // Review changes stacks on the host and holds the host's submit until it closes.
        Task reviewing = drawers.Find("form[data-drawer=host]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);
        Assert.Equal(2, drawers.FindAll(".pdk-sheet").Count);
        await drawers.Find("[data-review=apply]").ClickAsync(new());
        await reviewing.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);

        // One dry run and one commit, both of the whole document: the new upstream and the host that routes to it, together.
        Assert.Equal(2, config.Executed.Count);
        ReplaceConfigCommand applied = Assert.IsType<ReplaceConfigCommand>(config.Executed[^1]);
        Assert.False(applied.DryRun);
        Assert.True(config.Executed[0].DryRun);
        Assert.Equal(upstream.Id, Assert.Single(applied.Document.Upstreams).Id);
        HostConfig host = Assert.Single(applied.Document.Hosts);
        Assert.Equal(["jelly.home.test"], host.Names);
        Assert.Equal(upstream.Id, Assert.Single(host.Routes).UpstreamId);
        Assert.Equal("New host jelly.home.test with upstream jellyfin", applied.Message);
        await drawers.WaitForStateAsync(() => drawers.FindAll(".pdk-sheet").Count == 0);
    }

    [Fact]
    public async Task CancellingTheHost_LeavesNoUpstreamBehind()
    {
        await using DashboardTestContext context = new();
        FakeDashboardConfig config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument();
        context.Render<MudPopoverProvider>();
        IRenderedComponent<DrawerHost> drawers = context.Render<DrawerHost>();
        context.Render<Hosts>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=host] input[data-field=name-0]").Count == 1);
        await MakeUpstreamAsync(drawers, "app", "http://10.0.0.7:8080");

        // The staged upstream is an unsaved change: closing asks, and discarding drops it with the host.
        await drawers.Find("[data-drawer=host] [data-action=cancel]").ClickAsync(new());
        await drawers.Find("[data-drawer=discard-confirm]").ClickAsync(new());

        Assert.Empty(drawers.FindAll(".pdk-sheet"));
        Assert.Empty(config.Executed);
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
        HostForm form = drawers.FindComponent<HostForm>().Instance;
        RouteEntry route = Assert.Single(form.Model.Routes);
        Task choosing = drawers.InvokeAsync(() => form.OnNewUpstream.InvokeAsync(route));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=upstream] input[data-field=name]").Count == 1);
        Assert.Equal(2, drawers.FindAll(".pdk-sheet").Count);
        Assert.Equal("Back to New host", drawers.Find("[data-drawer=upstream] [data-drawer-action=back]").GetAttribute("aria-label"));

        await TypeAsync(drawers, "[data-drawer=upstream] input[data-field=name]", name);
        await TypeAsync(drawers, "[data-drawer=upstream] input[data-field=destination-0]", address);
        await drawers.Find("form[data-drawer=upstream]").SubmitAsync();
        await choosing.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);

        UpstreamId id = route.UpstreamId ?? throw new InvalidOperationException("The route didn't get the new upstream.");
        return new UpstreamConfig(id, name, Destinations: [new DestinationConfig(DestinationId.New(), address)]);
    }
}
