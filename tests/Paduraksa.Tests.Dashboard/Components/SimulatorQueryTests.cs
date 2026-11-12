using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class SimulatorQueryTests
{
    [Fact]
    public async Task TheSimulatorStartsFromTheRequestTheTailLinksTo()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Owner);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/simulator?url=https%3A%2F%2Fapp.test%2Fcart&clientIp=203.0.113.9&method=post");

        var page = context.Render<Simulator>();
        await page.WaitForStateAsync(() => page.Find("input[data-field=url]").GetAttribute("value") == "https://app.test/cart");

        Assert.Equal("203.0.113.9", page.Find("input[data-field=client-ip]").GetAttribute("value"));
        Assert.Contains("value=\"POST\"", page.Markup, StringComparison.Ordinal);
    }
}
