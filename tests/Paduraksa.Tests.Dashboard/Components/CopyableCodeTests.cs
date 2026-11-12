using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Components;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class CopyableCodeTests
{
    private static DashboardTestContext Context()
    {
        DashboardTestContext context = new();
        context.Services.AddScoped<DashboardBrowser>();
        context.Render<MudPopoverProvider>();
        return context;
    }

    [Fact]
    public async Task TheTextAndItsCopyButtonAreOneUnit_TheButtonNamedAfterTheText()
    {
        await using var context = Context();

        var code = context.Render<CopyableCode>(p => p.Add(c => c.Value, "claude mcp add").Add(c => c.Label, "Claude Code").Add(c => c.Block, true).Add(c => c.CopyAction, "copy-claude"));

        Assert.Equal("claude mcp add", code.Find(".pdk-copy-box pre.pdk-copy-text").TextContent);
        Assert.Equal("Copy Claude Code", code.Find(".pdk-copy-box [data-action=copy-claude]").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task CopyingSaysSoAndSendsTheTextToTheClipboard()
    {
        await using var context = Context();
        var copy = context.JSInterop.Setup<bool>("paduraksa.copyText", "abc");
        copy.SetResult(true);
        var code = context.Render<CopyableCode>(p => p.Add(c => c.Value, "abc"));

        await code.Find("[data-action=copy]").ClickAsync(new());

        Assert.Equal("Copied", code.Find("[data-action=copy]").GetAttribute("aria-label"));
        Assert.Single(context.JSInterop.Invocations, i => i.Identifier == "paduraksa.copyText");
    }
}
