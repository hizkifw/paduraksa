using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;
using static Paduraksa.Tests.Dashboard.EndToEnd.DashboardPage;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// Every outlined field's label sits on its border, empty or not (MudBlazor floats it only for a field with a value or a placeholder, so
/// app.css floats the rest), and the dense toolbar fields keep one height beside the 40 px segmented control. Skipped without Chromium (see <see cref="PlaywrightBrowser"/>).
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class FormLabelsEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnEmptyFieldFloatsItsLabelLikeAFilledOne_AndAToolbarKeepsItsHeights()
    {
        await using var session = await BrowserSession.StartAsync(BrowserSession.Desktop(colorScheme: null));
        var (host, page) = session;
        await SetUpAsync(page, host);

        // The ntfy form: Server has a value, Topic is empty and has no placeholder, Access token is empty and a password field.
        await page.GotoAsync("/settings/notifications");
        // A click before the new page's circuit is live is lost: nothing is listening yet.
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
        await page.ClickAsync("[data-action=add-channel]");
        await Expect(page.Locator("input[data-field=topic]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await PlaywrightBrowser.SettleAsync(page);
        var filled = await LabelOf(page, "input[data-field=server-url]");
        var empty = await LabelOf(page, "input[data-field=topic]");
        Assert.Equal(filled.Transform, empty.Transform);
        Assert.True(empty.NotchWidth > 0, "The border opens a notch for the floated label of an empty field.");
        Assert.Equal(filled.NotchWidth > 0, empty.NotchWidth > 0);

        // The audit filters: dense fields next to the segmented control, all one height, whatever their labels do.
        await page.GotoAsync("/audit");
        await Expect(page.Locator(".pdk-filters [data-field]").First).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await PlaywrightBrowser.SettleAsync(page);
        var heights = await page.EvaluateAsync<double[]>(
            "() => [...document.querySelectorAll('.pdk-filters .mud-input.mud-input-outlined')].map(e => e.getBoundingClientRect().height)");
        Assert.NotEmpty(heights);
        Assert.All(heights, h => Assert.InRange(h, heights[0] - 0.5, heights[0] + 0.5));
        var segment = await page.Locator(".pdk-filters .pdk-seg").BoundingBoxAsync();
        Assert.InRange(segment!.Height, 39.5, 40.5);
    }

    private static async Task<(string Transform, double NotchWidth)> LabelOf(IPage page, string input)
    {
        var state = await page.EvaluateAsync<string[]>(
            @"(selector) => {
                const control = document.querySelector(selector).closest('.mud-input-control');
                const label = control.querySelector('label.mud-input-label');
                const legend = control.querySelector('.mud-input-outlined-border legend');
                return [getComputedStyle(label).transform, String(legend.getBoundingClientRect().width)];
            }",
            input);
        return (state[0], double.Parse(state[1], System.Globalization.CultureInfo.InvariantCulture));
    }
}
