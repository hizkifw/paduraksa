using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// "Review changes" (PLAN §12): the dry run's diff and warnings before anything commits, its errors handed back to the form, a stale
/// version as a friendly conflict, Apply with the version the form opened with, and a commit that couldn't go live said plainly.
/// </summary>
public sealed class ReviewDrawerTests
{
    private static readonly UpstreamConfig App = new(UpstreamId.New(), "app", Version: 1, Destinations: [new DestinationConfig(DestinationId.New(), "http://app:8080")]);

    private static readonly HostConfig Stored = new(HostId.New(), ["app.example.com"], Version: 2, Routes: [new RouteConfig(RouteId.New(), App.Id)]);

    private static readonly HostConfig Edited = Stored with { Names = ["app.example.com", "www.example.com"], Enabled = false };

    [Fact]
    public async Task TheDryRun_IsShownAsADiff_WithWarnings_BeforeAnythingCommits()
    {
        await using DashboardTestContext context = new();
        FakeDashboardConfig config = Arrange(context);
        config.Results = command => command.DryRun
            ? new ConfigCommandResult(
                ConfigCommandStatus.DryRun,
                Changes: [new HostChange(ConfigChangeKind.Modified, Stored.Id, "/hosts/0", ["/hosts/0/names", "/hosts/0/enabled"])],
                Warnings: [new ConfigWarning("/hosts/0", "certificate.auto.noCoverage", "no valid certificate covers www.example.com")])
            : throw new InvalidOperationException("Only the dry run should run.");

        IRenderedComponent<ReviewDrawer> drawer = Render(context, new UpsertHostCommand(Edited));

        await drawer.WaitForStateAsync(() => drawer.FindAll("[data-review=apply]").Count == 1);
        ConfigCommand ran = Assert.Single(config.Executed);
        Assert.True(ran.DryRun);
        Assert.Contains("app.example.com", drawer.Find(".pdk-diff-title").TextContent, StringComparison.Ordinal);
        Assert.Equal("Changed", drawer.Find(".pdk-diff-head .pdk-badge").TextContent);
        Assert.Contains("www.example.com", drawer.Find(".pdk-diff-new").TextContent, StringComparison.Ordinal);
        string notices = drawer.Find(".pdk-notices").TextContent;
        Assert.Contains("stop answering while the host is switched off", notices, StringComparison.Ordinal);
        Assert.Contains("No valid certificate covers www.example.com", notices, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_CommitsWithTheOpenedVersion_OffersUndo_AndClearsTheDraft()
    {
        await using DashboardTestContext context = new();
        FakeDashboardConfig config = Arrange(context);
        ConfigCommandResult? applied = null;
        IRenderedComponent<ReviewDrawer> drawer = Render(context, new UpsertHostCommand(Edited), onApplied: result => applied = result);
        await drawer.WaitForStateAsync(() => drawer.FindAll("[data-review=apply]").Count == 1);
        Assert.NotNull(context.Services.GetRequiredService<ReviewedDraft>().Command);

        await drawer.Find("[data-review=apply]").ClickAsync(new());

        UpsertHostCommand committed = Assert.IsType<UpsertHostCommand>(config.Executed[^1]);
        Assert.False(committed.DryRun);
        Assert.Equal(2, committed.Host.Version);
        Assert.Equal(ConfigCommandStatus.Committed, applied?.Status);
        Assert.Empty(drawer.FindAll(".pdk-sheet"));
        Assert.Null(context.Services.GetRequiredService<ReviewedDraft>().Command);
    }

    [Fact]
    public async Task Errors_AreListedByPlace_AndHandedToTheForm()
    {
        await using DashboardTestContext context = new();
        FakeDashboardConfig config = Arrange(context);
        config.Results = _ => new ConfigCommandResult(
            ConfigCommandStatus.Invalid,
            Errors: [new ConfigError("/names/1", "host.name.duplicate", "'www.example.com' is already served by another host")]);
        IReadOnlyList<ConfigError>? handed = null;

        IRenderedComponent<ReviewDrawer> drawer = Render(context, new UpsertHostCommand(Edited), onInvalid: errors => handed = errors);

        await drawer.WaitForStateAsync(() => drawer.FindAll("[data-review=invalid]").Count == 1);
        string text = drawer.Find("[data-review=invalid]").TextContent;
        Assert.Equal("Name 2", drawer.Find("[data-review=invalid] .pdk-error-where").TextContent);
        Assert.Contains("'www.example.com' is already served by another host.", text, StringComparison.Ordinal);
        Assert.Equal("/names/1", Assert.Single(handed!).Path);
        Assert.Empty(drawer.FindAll("[data-review=apply]"));
    }

    [Fact]
    public async Task AStaleVersion_IsAFriendlyConflict_WithReload()
    {
        await using DashboardTestContext context = new();
        FakeDashboardConfig config = Arrange(context);
        config.Results = _ => new ConfigCommandResult(
            ConfigCommandStatus.Conflict,
            Conflict: new ConfigConflict([], new RevisionRef(ConfigRevisionId.New(), 9), CurrentVersion: 9));
        bool reloaded = false;

        IRenderedComponent<ReviewDrawer> drawer = Render(context, new UpsertHostCommand(Edited), onReload: () => reloaded = true);

        await drawer.WaitForStateAsync(() => drawer.FindAll("[data-review=conflict]").Count == 1);
        string text = drawer.Find("[data-review=conflict]").TextContent;
        Assert.Contains("This changed since you opened it.", text, StringComparison.Ordinal);
        Assert.Contains("#9", text, StringComparison.Ordinal);
        await drawer.FindAll("button").Single(b => b.TextContent.Contains("Reload", StringComparison.Ordinal)).ClickAsync(new());
        Assert.True(reloaded);
    }

    [Fact]
    public async Task AConflictAtApply_IsShownTheSameWay()
    {
        await using DashboardTestContext context = new();
        FakeDashboardConfig config = Arrange(context);
        config.Results = command => command.DryRun
            ? new ConfigCommandResult(ConfigCommandStatus.DryRun)
            : new ConfigCommandResult(ConfigCommandStatus.Conflict, Conflict: new ConfigConflict([], CurrentVersion: 3));
        IRenderedComponent<ReviewDrawer> drawer = Render(context, new UpsertHostCommand(Edited));
        await drawer.WaitForStateAsync(() => drawer.FindAll("[data-review=apply]").Count == 1);

        await drawer.Find("[data-review=apply]").ClickAsync(new());

        Assert.Single(drawer.FindAll("[data-review=conflict]"));
    }

    [Fact]
    public async Task ACommitThatCouldNotGoLive_SaysSo()
    {
        await using DashboardTestContext context = new();
        FakeDashboardConfig config = Arrange(context);
        RevisionRef stored = new(ConfigRevisionId.New(), 5);
        config.Results = command => command.DryRun
            ? new ConfigCommandResult(ConfigCommandStatus.DryRun)
            : new ConfigCommandResult(
                ConfigCommandStatus.PublishFailed,
                stored,
                Publish: new PublishOutcome(PublishOutcomeStatus.Rejected, 0, ["YARP rejected the cluster"], stored.Id));
        IRenderedComponent<ReviewDrawer> drawer = Render(context, new UpsertHostCommand(Edited));
        await drawer.WaitForStateAsync(() => drawer.FindAll("[data-review=apply]").Count == 1);

        await drawer.Find("[data-review=apply]").ClickAsync(new());

        string text = drawer.Find("[data-review=publish-failed]").TextContent;
        Assert.Contains("Saved as #5, but it isn't live.", text, StringComparison.Ordinal);
        Assert.Contains("YARP rejected the cluster", text, StringComparison.Ordinal);
    }

    private static FakeDashboardConfig Arrange(DashboardTestContext context)
    {
        FakeDashboardConfig config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(Upstreams: [App], Hosts: [Stored]);
        return config;
    }

    private static IRenderedComponent<ReviewDrawer> Render(
        DashboardTestContext context,
        ConfigCommand command,
        Action<ConfigCommandResult>? onApplied = null,
        Action<IReadOnlyList<ConfigError>>? onInvalid = null,
        Action? onReload = null) =>
        context.Render<ReviewDrawer>(parameters => parameters
            .Add(d => d.Open, true)
            .Add(d => d.Title, "Edit app.example.com")
            .Add(d => d.Command, command)
            .Add(d => d.OnApplied, onApplied ?? (_ => { }))
            .Add(d => d.OnInvalid, onInvalid ?? (_ => { }))
            .Add(d => d.OnReload, onReload ?? (() => { })));
}
