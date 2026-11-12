using System.Security.Claims;

using Bunit;

using Microsoft.AspNetCore.Components;

using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Layout;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class PermissionVisibilityTests
{
    [Theory]
    [InlineData(Role.Owner, new[] { "Overview", "Hosts", "Upstreams", "Profiles", "Security overview", "Rate limits", "Access lists", "Rules", "Sign-in", "Certificates", "Local CA", "ACME accounts", "DNS providers", "Analytics", "Simulator", "History", "Audit", "Logs", "Live tail", "Global defaults", "Users and roles", "Notifications", "Notification history", "Export and import", "API tokens", "Backup" })]
    [InlineData(Role.Viewer, new[] { "Overview", "Hosts", "Upstreams", "Profiles", "Security overview", "Rate limits", "Access lists", "Rules", "Sign-in", "Certificates", "Local CA", "ACME accounts", "DNS providers", "Analytics", "Simulator", "History", "Global defaults", "Notifications", "Notification history", "Export and import", "API tokens" })]
    [InlineData(Role.Auditor, new[] { "Overview", "Hosts", "Upstreams", "Profiles", "Security overview", "Rate limits", "Access lists", "Rules", "Sign-in", "Certificates", "Local CA", "ACME accounts", "DNS providers", "Analytics", "History", "Audit", "Logs", "Live tail", "Global defaults", "Notifications", "Notification history", "Export and import", "API tokens" })]
    public void TheNavigation_ListsThePagesTheRoleMaySee(Role role, string[] expected)
    {
        using DashboardTestContext context = new();
        var menu = context.Render<NavMenu>(parameters =>
            parameters.AddCascadingValue(DashboardTestContext.StateOf(TestUsers.User(role))));

        Assert.Equal(expected, menu.FindAll(".pdk-nav-label").Select(label => label.TextContent));
    }

    [Fact]
    public void TheNavigation_ShowsNothingPermissionBoundToAnAnonymousState()
    {
        using DashboardTestContext context = new();
        var menu = context.Render<NavMenu>(parameters =>
            parameters.AddCascadingValue(DashboardTestContext.StateOf(null)));

        Assert.Equal(["Overview"], menu.FindAll(".pdk-nav-label").Select(label => label.TextContent));
    }

    [Fact]
    public void PlaceholderPages_AreMarkedSoon()
    {
        using DashboardTestContext context = new();
        var menu = context.Render<NavMenu>(parameters =>
            parameters.AddCascadingValue(DashboardTestContext.StateOf(TestUsers.User(Role.Owner))));

        Assert.Equal(DashboardNavigation.Entries.Count(entry => !entry.Available), menu.FindAll(".pdk-soon").Count);
    }

    [Theory]
    [InlineData(Role.Operator, true)]
    [InlineData(Role.Viewer, false)]
    public void IfPermitted_ShowsItsContentOnlyWithThePermission(Role role, bool shown)
    {
        using DashboardTestContext context = new();
        var gate = RenderGate(context, TestUsers.User(role));

        Assert.Equal(shown, gate.FindAll("button.add").Count == 1);
        Assert.Equal(!shown, gate.FindAll("span.why").Count == 1);
    }

    [Fact]
    public void IfPermitted_ShowsNothingToAnAnonymousState()
    {
        using DashboardTestContext context = new();
        var gate = RenderGate(context, null);

        Assert.Empty(gate.FindAll("button.add"));
    }

    private static IRenderedComponent<IfPermitted> RenderGate(DashboardTestContext context, ClaimsPrincipal? user) =>
        context.Render<IfPermitted>(parameters => parameters
            .AddCascadingValue(DashboardTestContext.StateOf(user))
            .Add(gate => gate.Permission, Permission.ConfigWrite)
            .Add(gate => gate.ChildContent, (RenderFragment)(builder => builder.AddMarkupContent(0, "<button class=\"add\">Add a host</button>")))
            .Add(gate => gate.Otherwise, (RenderFragment)(builder => builder.AddMarkupContent(0, "<span class=\"why\">Read-only</span>"))));
}
