using System.Text.RegularExpressions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;
using static Paduraksa.Tests.Dashboard.EndToEnd.DashboardPage;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// A link to one server setting (<c>/settings/server#mcp-enabled</c>) in a real browser: the row is scrolled clear of the app bar, takes focus
/// and shows the highlight for a moment, whether the page is loaded with the fragment or reached by a navigation inside the app. Skipped
/// without Chromium (see <see cref="PlaywrightBrowser"/>).
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class SettingLinkEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Short enough that the row of the setting (the integrations are the fourth card) is below the fold.
    private static readonly ViewportSize Desktop = new() { Width = 1366, Height = 640 };
    private static readonly ViewportSize Phone = new() { Width = 390, Height = 760 };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OpeningTheAddressWithTheFragmentScrollsToTheRowFocusesItAndHighlightsItForAMoment(bool phone, bool reducedMotion)
    {
        await using var session = await SignedInAsync(phone, reducedMotion);
        var page = session.Page;

        await page.GotoAsync("/settings/server#mcp-enabled");

        await ExpectLandedOnAsync(page, "mcp-enabled");
    }

    [Fact]
    public async Task FollowingTheTokensPagesMcpLinkLandsOnTheSameRow()
    {
        await using var session = await SignedInAsync(phone: false, reducedMotion: false);
        var page = session.Page;
        await page.GotoAsync("/settings/tokens");
        var link = page.Locator("[data-section=mcp-off] a[data-action=open-setting]");
        await Expect(link).ToBeVisibleAsync(new() { Timeout = 15_000 });
        Assert.Equal("/settings/server#mcp-enabled", await link.GetAttributeAsync("href"));

        await link.ClickAsync();

        await Expect(page).ToHaveURLAsync(new Regex("/settings/server#mcp-enabled$"));
        await ExpectLandedOnAsync(page, "mcp-enabled");
    }

    [Fact]
    public async Task ANavigationToAnotherSettingOnTheSamePageMovesTheHighlight()
    {
        await using var session = await SignedInAsync(phone: false, reducedMotion: false);
        var page = session.Page;
        await page.GotoAsync("/settings/server#mcp-enabled");
        await ExpectLandedOnAsync(page, "mcp-enabled");

        await page.EvaluateAsync("() => window.Blazor.navigateTo('/settings/server#limits-max-connections')");

        await ExpectLandedOnAsync(page, "limits-max-connections");
    }

    private static async Task ExpectLandedOnAsync(IPage page, string anchor)
    {
        var row = page.Locator("#" + anchor);
        var highlighted = new Regex(@"\bpdk-highlight\b");
        await Expect(row).ToHaveClassAsync(highlighted, new() { Timeout = 20_000 });

        // Below the app bar, and wholly in the window.
        var bar = await page.Locator(".pdk-appbar").BoundingBoxAsync() ?? throw new InvalidOperationException("No app bar.");
        var box = await row.BoundingBoxAsync() ?? throw new InvalidOperationException("The row has no box.");
        var viewport = page.ViewportSize!;
        Assert.True(box.Y >= bar.Y + bar.Height, $"The row starts at {box.Y}, under the app bar that ends at {bar.Y + bar.Height}.");
        Assert.True(box.Y + box.Height <= viewport.Height, $"The row ends at {box.Y + box.Height}, below the window's {viewport.Height}.");

        Assert.True(
            await page.EvaluateAsync<bool>($"() => document.getElementById('{anchor}').contains(document.activeElement)"),
            "Focus isn't inside the row.");

        await Expect(row).Not.ToHaveClassAsync(highlighted, new() { Timeout = 6_000 });
    }

    private static async Task<BrowserSession> SignedInAsync(bool phone, bool reducedMotion)
    {
        var session = await BrowserSession.StartAsync(new()
        {
            ViewportSize = phone ? Phone : Desktop,
            ReducedMotion = reducedMotion ? ReducedMotion.Reduce : ReducedMotion.NoPreference,
        });
        await SetUpAsync(session.Page, session.Host, displayName: null);
        return session;
    }
}
