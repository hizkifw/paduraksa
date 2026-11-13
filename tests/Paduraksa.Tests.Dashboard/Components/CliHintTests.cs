using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Components;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>Every command the dashboard tells a person to run is shown in the same two forms, through the same component.</summary>
public sealed class CliHintTests
{
    private static DashboardTestContext Context()
    {
        DashboardTestContext context = new();
        context.Services.AddScoped<DashboardBrowser>();
        context.Render<MudPopoverProvider>();
        return context;
    }

    [Fact]
    public async Task ACommandOnARunningServer_IsShownForDockerThroughExec_AndBareForSystemd()
    {
        await using var context = Context();

        var hint = context.Render<CliHint>(p => p.Add(c => c.Arguments, "setup-token"));

        Assert.Equal("docker compose exec paduraksa paduraksa setup-token", hint.Find("[data-form=docker] code").TextContent);
        Assert.Equal("paduraksa setup-token", hint.Find("[data-form=systemd] code").TextContent);
    }

    [Fact]
    public async Task ACommandThatNeedsTheServerStopped_RunsATemporaryContainerInstead_WithItsOwnPathInside()
    {
        await using var context = Context();

        var hint = context.Render<CliHint>(p => p
            .Add(c => c.Arguments, "restore <file>")
            .Add(c => c.DockerArguments, "restore /data/backups/<file>")
            .Add(c => c.ServerStopped, true));

        Assert.Equal("docker compose run --rm paduraksa restore /data/backups/<file>", hint.Find("[data-form=docker] code").TextContent);
        Assert.Equal("paduraksa restore <file>", hint.Find("[data-form=systemd] code").TextContent);
    }

    [Fact]
    public async Task OnAPageWithoutEvents_EachFormHasANativeCopyButtonThatCarriesTheText()
    {
        await using var context = Context();

        var hint = context.Render<CliHint>(p => p.Add(c => c.Arguments, "admin reset-password <user>").Add(c => c.Static, true));

        var docker = hint.Find("[data-form=docker] button.pdk-copy-static");
        Assert.Equal("docker compose exec paduraksa paduraksa admin reset-password <user>", docker.GetAttribute("data-copy-value"));
        Assert.Equal("Copy Docker", docker.GetAttribute("aria-label"));
        Assert.Equal("paduraksa admin reset-password <user>", hint.Find("[data-form=systemd] button.pdk-copy-static").GetAttribute("data-copy-value"));
    }
}
