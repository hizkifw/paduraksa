using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Dashboard.Drawers;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>A drawer that throws is replaced by a compact error and Close: the page and the levels under it carry on.</summary>
public sealed class DrawerErrorBoundaryTests
{
    private sealed class ThrowingDrawer : ComponentBase
    {
        protected override void OnInitialized() => throw new InvalidOperationException("boom");
    }

    private static (IRenderedComponent<DrawerHost> Host, DrawerStack Stack, DashboardTestContext Context) Open()
    {
        DashboardTestContext context = new();
        context.SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        var host = context.Render<DrawerHost>();
        return (host, context.Services.GetRequiredService<DrawerStack>(), context);
    }

    [Fact]
    public async Task AThrowingDrawer_ShowsTheErrorStateInItsOwnLevel_AndTheLevelBelowIsUntouched()
    {
        var (host, stack, context) = Open();
        await using var _ = context;
        DrawerLevel top = null!;
        await host.InvokeAsync(() =>
        {
            stack.Push<NotFoundDrawer>(NotFoundDrawer.For("New host", "b"));
            top = stack.Push<ThrowingDrawer>();
        });

        Assert.Equal(2, host.FindAll(".pdk-sheet").Count);
        Assert.Equal("This drawer couldn't be shown", host.Find("[data-level='1'] .pdk-drawer-title").TextContent);
        Assert.Contains("server log", host.Find("[data-level='1'] [data-drawer=error]").TextContent, StringComparison.Ordinal);
        Assert.Equal("New host", host.Find("[data-level='0'] .pdk-drawer-title").TextContent);
        Assert.Same(top, stack.Top);
    }

    [Fact]
    public async Task Close_ClosesOnlyTheFailedLevel_WithoutAskingAboutWhatItHeld()
    {
        var (host, stack, context) = Open();
        await using var _ = context;
        DrawerLevel bottom = null!;
        DrawerLevel top = null!;
        await host.InvokeAsync(() =>
        {
            bottom = stack.Push<NotFoundDrawer>(NotFoundDrawer.For("New host", "b"));
            top = stack.Push<ThrowingDrawer>();
            top.IsDirty = true;
        });

        await host.Find("[data-level='1'] [data-action=close]").ClickAsync(new());

        Assert.Empty(host.FindAll("[data-drawer=discard]"));
        Assert.False(top.IsOpen);
        Assert.Same(bottom, stack.Top);
        Assert.Single(host.FindAll(".pdk-sheet"));
        Assert.True(bottom.IsOpen);
    }

    [Fact]
    public async Task TheErrorStateIsLabelledAsTheSheet_SoTheDialogKeepsItsName()
    {
        var (host, stack, context) = Open();
        await using var _ = context;
        await host.InvokeAsync(() => stack.Push<ThrowingDrawer>());

        var id = host.Find(".pdk-sheet").GetAttribute("aria-labelledby");
        Assert.Equal("This drawer couldn't be shown", host.Find($"#{id}").TextContent);
    }
}
