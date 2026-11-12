using System.Text.RegularExpressions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// Export and import in a real browser against a real <c>PaduraksaHost</c>: pasted YAML with a mistake is refused with its line, the fixed YAML is reviewed as a diff and
/// applied as one revision (the hosts page lists it), and the page's download is the YAML the config now is. Skipped without Chromium (see <see cref="PlaywrightBrowser"/>).
/// </summary>
public sealed class ConfigTransferEndToEndTests
{
    private const string Yaml = """
        apiVersion: paduraksa.dev/v1
        kind: Config
        upstreams:
          - name: jellyfin
            destinations:
              - address: http://127.0.0.1:8096
        hosts:
          - names: [jellyfin.example.test]
            routes:
              - upstream: jellyfin
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PasteReviewApplyAndDownload()
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var host = await DashboardHost.StartAsync(Ct);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.Admin.ToString(),
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            AcceptDownloads = true,
        });
        var page = await context.NewPageAsync();
        var problems = PlaywrightBrowser.CollectProblems(page);

        await page.GotoAsync("/");
        await Expect(page).ToHaveURLAsync(new Regex("/setup$"));
        await page.FillAsync("input[name=setupToken]", await host.SetupTokenAsync(Ct));
        await page.FillAsync("input[name=username]", DashboardHost.OwnerName);
        await page.FillAsync("input[name=displayName]", "Ada Lovelace");
        await page.FillAsync("input[name=password]", DashboardHost.OwnerPassword);
        await page.FillAsync("input[name=confirmPassword]", DashboardHost.OwnerPassword);
        await page.ClickAsync("button[type=submit]");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });

        await page.Locator(".pdk-nav").GetByRole(AriaRole.Link, new() { Name = "Export and import" }).ClickAsync();
        await Expect(page.Locator("[data-card=import]")).ToBeVisibleAsync(new() { Timeout = 15_000 });

        // A mistake is refused where it is, with its line, and nothing is reviewed.
        await page.FillAsync("textarea", Yaml.Replace("upstream: jellyfin", "upstream: jelly", StringComparison.Ordinal));
        await page.ClickAsync("[data-action=review-import]");
        await Expect(page.Locator("[data-form=errors]")).ToContainTextAsync("/hosts/0/routes/0/upstream");
        await Expect(page.Locator("[data-form=errors]")).ToContainTextAsync("line 10");
        await Expect(page.Locator("[data-review=apply]")).ToHaveCountAsync(0);

        // The fixed YAML is reviewed as a diff, then applied as one revision.
        await page.FillAsync("textarea", Yaml);
        await page.ClickAsync("[data-action=review-import]");
        await Expect(page.Locator("[data-review=apply]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-diff-title", new() { HasText = "jellyfin.example.test" })).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "transfer-review", fullPage: false);
        await page.ClickAsync("[data-review=apply]");
        await Expect(page.Locator("[data-review=apply]")).ToHaveCountAsync(0, new() { Timeout = 15_000 });

        await page.GotoAsync("/hosts");
        await Expect(page.GetByText("jellyfin.example.test")).ToBeVisibleAsync(new() { Timeout = 15_000 });

        // The download is the YAML the config now is, and reads back as a no-op import.
        await page.GotoAsync("/settings/transfer");
        await Expect(page.Locator("[data-card=export]")).ToContainTextAsync("Revision #1", new() { Timeout = 15_000 });
        var download = await page.RunAndWaitForDownloadAsync(() => page.ClickAsync("[data-action=export]"));
        Assert.Equal("paduraksa-config-r1.yaml", download.SuggestedFilename);
        var text = await File.ReadAllTextAsync((await download.PathAsync())!, Ct);
        Assert.StartsWith("apiVersion: paduraksa.dev/v1\nkind: Config\n", text, StringComparison.Ordinal);
        Assert.Contains("jellyfin.example.test", text, StringComparison.Ordinal);

        await page.FillAsync("textarea", text);
        await page.ClickAsync("[data-action=review-import]");
        await Expect(page.GetByText("Nothing to change")).ToBeVisibleAsync(new() { Timeout = 15_000 });

        // A file is streamed in pieces, not sent as one circuit message: 150 KB, far over the 32 KiB a message may be, arrives whole.
        await page.GotoAsync("/settings/transfer");
        await Expect(page.Locator("[data-card=import]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "big.yaml",
            MimeType = "application/yaml",
            Buffer = System.Text.Encoding.UTF8.GetBytes(text + string.Concat(Enumerable.Repeat("# padding padding padding padding padding padding padding\n", 2800))),
        });
        await Expect(page.Locator("[data-field=import-file]")).ToHaveTextAsync("big.yaml");
        await page.ClickAsync("[data-action=review-import]");
        await Expect(page.GetByText("Nothing to change")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        Assert.Empty(problems);
    }
}
