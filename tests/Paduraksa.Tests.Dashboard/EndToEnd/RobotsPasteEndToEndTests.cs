using System.Text.RegularExpressions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// iOS puts a multi-line text that starts like a URL scheme ("User-agent: *") on the clipboard as a URL too, and Safari pastes that
/// form, percent-encoded; the robots.txt text area decodes such a paste and leaves every other one alone. Skipped when no Chromium is
/// installed (see <see cref="PlaywrightBrowser"/>).
/// </summary>
public sealed class RobotsPasteEndToEndTests
{
    private const string Field = "textarea[data-field='robots-custom-text'], [data-field='robots-custom-text'] textarea";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APercentEncodedPasteGoesInAsTheTextItEncodes_AndAnyOtherPasteAsItIs()
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var host = await DashboardHost.StartAsync(Ct);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = host.Admin.ToString(), IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await SetUpAsync(page, host);

        await page.GotoAsync("/settings/defaults");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        var mode = page.Locator("[data-field='robots-mode']").First;
        await mode.ScrollIntoViewIfNeededAsync();
        await mode.Locator("xpath=ancestor-or-self::div[contains(@class,'mud-select')][1]").ClickAsync();
        await page.Locator(".mud-popover-open .mud-list-item", new() { HasText = "Custom" }).First.ClickAsync();
        var text = page.Locator(Field).First;
        await Expect(text).ToBeVisibleAsync(new() { Timeout = 15_000 });

        // What Safari on iOS pasted, byte for byte.
        await PasteAsync(text, "user-agent:%20*%0AAllow:%20/%0ADisallow:%20/prefix");
        await Expect(text).ToHaveValueAsync("user-agent: *\nAllow: /\nDisallow: /prefix");

        // Text that already has its line breaks, and a lone URL, go in untouched.
        await text.FillAsync(string.Empty);
        await PasteAsync(text, "Disallow: /a%20b\nAllow: /");
        await Expect(text).ToHaveValueAsync("Disallow: /a%20b\nAllow: /");
        await text.FillAsync(string.Empty);
        await PasteAsync(text, "https://example.test/a%20b");
        await Expect(text).ToHaveValueAsync("https://example.test/a%20b");
    }

    /// <summary>A paste as the browser raises it: the event carries the clipboard; when nothing cancels it, the text goes in.</summary>
    private static async Task PasteAsync(ILocator field, string clipboard)
    {
        await field.FocusAsync();
        await field.EvaluateAsync(
            """
            (field, text) => {
                const data = new DataTransfer();
                data.setData("text/plain", text);
                const event = new ClipboardEvent("paste", { clipboardData: data, bubbles: true, cancelable: true });
                if (field.dispatchEvent(event)) {
                    document.execCommand("insertText", false, text);
                }
            }
            """,
            clipboard);
    }

    private static async Task SetUpAsync(IPage page, DashboardHost host)
    {
        await page.GotoAsync("/");
        await Expect(page).ToHaveURLAsync(new Regex("/setup$"));
        await page.FillAsync("input[name=setupToken]", await host.SetupTokenAsync(Ct));
        await page.FillAsync("input[name=username]", DashboardHost.OwnerName);
        await page.FillAsync("input[name=displayName]", "Ada Lovelace");
        await page.FillAsync("input[name=password]", DashboardHost.OwnerPassword);
        await page.FillAsync("input[name=confirmPassword]", DashboardHost.OwnerPassword);
        await page.ClickAsync("button[type=submit]");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
    }
}
