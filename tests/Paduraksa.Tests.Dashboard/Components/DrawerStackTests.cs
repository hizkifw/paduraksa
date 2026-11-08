using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Dashboard.Drawers;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The drawer stack (PLAN §12): levels push and pop and hand a result back to the level below, Escape and the backdrop act on the top
/// level only, and a level with unsaved changes asks before it goes.
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

    /// <summary>What a level handed back; fails rather than waits forever when it is still open.</summary>
    private static Task<DrawerResult> Done(DrawerLevel level) => level.Result.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
}
