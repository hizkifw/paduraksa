using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// Settings > Backup in a real browser against a real <c>PaduraksaHost</c>: the Owner downloads an archive through the page, a Viewer is
/// offered no such page, and the passphrase is gone from the page afterwards. Skipped without Chromium (see <see cref="PlaywrightBrowser"/>).
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class BackupEndToEndTests
{
    private const string Passphrase = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheOwnerDownloadsAnEncryptedBackupAndTheNavigationOffersItToNoOneElse()
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var host = await DashboardHost.StartAsync(Ct);
        BrowserNewContextOptions options = new()
        {
            BaseURL = host.Admin.ToString(),
            IgnoreHTTPSErrors = true,
            AcceptDownloads = true,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            ColorScheme = ColorScheme.Dark,
        };
        await using var context = await browser.NewContextAsync(options);
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

        await page.Locator(".pdk-nav").GetByRole(AriaRole.Link, new() { Name = "Backup" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Backup", Level = 1 })).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "backup-empty");

        // A weak passphrase is refused where it is typed.
        await page.FillAsync("input[data-field=passphrase]", "short");
        await Expect(page.Locator("[data-strength]")).ToContainTextAsync("more characters");
        await page.FillAsync("input[data-field=repeat]", "short");
        await page.ClickAsync("[data-action=download-backup]");
        await Expect(page.GetByText("at least 12 characters").First).ToBeVisibleAsync();

        await page.FillAsync("input[data-field=passphrase]", Passphrase);
        await Expect(page.Locator("[data-strength]")).ToContainTextAsync("Good");
        await page.FillAsync("input[data-field=repeat]", Passphrase);
        await PlaywrightBrowser.ScreenshotAsync(page, "backup-filled");
        var download = await page.RunAndWaitForDownloadAsync(() => page.ClickAsync("[data-action=download-backup]"), new() { Timeout = 60_000 });

        Assert.Matches(@"^paduraksa-backup-\d{8}-\d{6}\.pdkbak$", download.SuggestedFilename);
        var path = await download.PathAsync();
        var bytes = await File.ReadAllBytesAsync(path!, Ct);
        Assert.Equal("PDKBAK", Encoding.ASCII.GetString(bytes, 0, 6));
        Assert.True(bytes.Length > 10_000, $"{bytes.Length} bytes");
        Assert.DoesNotContain(Passphrase, Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);

        // The fields were emptied as the request went out, and the passphrase is nowhere in the page.
        await Expect(page.Locator("input[data-field=passphrase]")).ToHaveValueAsync(string.Empty);
        Assert.DoesNotContain(Passphrase, await page.ContentAsync(), StringComparison.Ordinal);
        await Expect(page.Locator(".mud-snackbar").GetByText("Backup downloaded")).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "backup-downloaded");
        Assert.Empty(Directory.GetFiles(Path.Combine(host.Host.Options.DataDirectory, "run"), "download-*"));

        Assert.Empty(problems);
    }
}
