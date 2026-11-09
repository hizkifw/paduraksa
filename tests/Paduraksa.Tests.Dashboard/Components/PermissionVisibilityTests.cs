using System.Security.Claims;

using Bunit;

using Microsoft.AspNetCore.Components;

using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Layout;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>What a user may not do is not offered (PLAN §12): the navigation and actions follow the user's permissions.</summary>
public sealed class PermissionVisibilityTests
{
    [Theory]
    [InlineData(Role.Owner, new[] { "Overview", "Hosts", "Upstreams", "Profiles", "Certificates", "Local CA", "ACME accounts", "DNS providers", "Simulator", "History", "Audit", "Global defaults", "Users and roles", "API tokens" })]
    [InlineData(Role.Viewer, new[] { "Overview", "Hosts", "Upstreams", "Profiles", "Certificates", "Local CA", "ACME accounts", "DNS providers", "Simulator", "History", "Global defaults", "API tokens" })]
    [InlineData(Role.Auditor, new[] { "Overview", "Hosts", "Upstreams", "Profiles", "Certificates", "Local CA", "ACME accounts", "DNS providers", "History", "Audit", "Global defaults", "API tokens" })]
    public void TheNavigation_ListsThePagesTheRoleMaySee(Role role, string[] expected)
    {
        using DashboardTestContext context = new();
        IRenderedComponent<NavMenu> menu = context.Render<NavMenu>(parameters =>
            parameters.AddCascadingValue(DashboardTestContext.StateOf(TestUsers.User(role))));

        Assert.Equal(expected, menu.FindAll(".pdk-nav-label").Select(label => label.TextContent));
    }

    [Fact]
    public void TheNavigation_ShowsNothingPermissionBoundToAnAnonymousState()
    {
        using DashboardTestContext context = new();
        IRenderedComponent<NavMenu> menu = context.Render<NavMenu>(parameters =>
            parameters.AddCascadingValue(DashboardTestContext.StateOf(null)));

        Assert.Equal(["Overview"], menu.FindAll(".pdk-nav-label").Select(label => label.TextContent));
    }

    [Fact]
    public void PlaceholderPages_AreMarkedSoon()
    {
        using DashboardTestContext context = new();
        IRenderedComponent<NavMenu> menu = context.Render<NavMenu>(parameters =>
            parameters.AddCascadingValue(DashboardTestContext.StateOf(TestUsers.User(Role.Owner))));

        Assert.Equal(DashboardNavigation.Entries.Count(entry => !entry.Available), menu.FindAll(".pdk-soon").Count);
    }

    [Theory]
    [InlineData(Role.Operator, true)]
    [InlineData(Role.Viewer, false)]
    public void IfPermitted_ShowsItsContentOnlyWithThePermission(Role role, bool shown)
    {
        using DashboardTestContext context = new();
        IRenderedComponent<IfPermitted> gate = RenderGate(context, TestUsers.User(role));

        Assert.Equal(shown, gate.FindAll("button.add").Count == 1);
        Assert.Equal(!shown, gate.FindAll("span.why").Count == 1);
    }

    [Fact]
    public void IfPermitted_ShowsNothingToAnAnonymousState()
    {
        using DashboardTestContext context = new();
        IRenderedComponent<IfPermitted> gate = RenderGate(context, null);

        Assert.Empty(gate.FindAll("button.add"));
    }

    private static IRenderedComponent<IfPermitted> RenderGate(DashboardTestContext context, ClaimsPrincipal? user) =>
        context.Render<IfPermitted>(parameters => parameters
            .AddCascadingValue(DashboardTestContext.StateOf(user))
            .Add(gate => gate.Permission, Permission.ConfigWrite)
            .Add(gate => gate.ChildContent, (RenderFragment)(builder => builder.AddMarkupContent(0, "<button class=\"add\">Add a host</button>")))
            .Add(gate => gate.Otherwise, (RenderFragment)(builder => builder.AddMarkupContent(0, "<span class=\"why\">Read-only</span>"))));
}
