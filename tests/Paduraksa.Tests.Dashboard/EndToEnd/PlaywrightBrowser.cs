using Microsoft.Playwright;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// Launches headless Chromium for the end-to-end tests, or skips the test when there is none.
/// </summary>
/// <remarks>
/// Install the browser Playwright pins once, after building this project:
/// <code>
/// pwsh tests/Paduraksa.Tests.Dashboard/bin/Release/net10.0/playwright.ps1 install chromium
/// </code>
/// or, without PowerShell, <c>dotnet tool install --global Microsoft.Playwright.CLI</c> and then
/// <c>playwright install chromium</c> (on Linux add <c>--with-deps</c> for the system libraries). A Chromium that is already installed can
/// be used instead by pointing <see cref="ExecutableVariable"/> at it (<c>PADURAKSA_E2E_CHROMIUM=/usr/bin/chromium</c>). Set
/// <see cref="ScreenshotVariable"/> to a directory to keep screenshots of the pages the test visits.
/// </remarks>
internal static class PlaywrightBrowser
{
    /// <summary>The path of a Chromium to use instead of Playwright's own.</summary>
    public const string ExecutableVariable = "PADURAKSA_E2E_CHROMIUM";

    /// <summary>A directory to write screenshots to (none are taken without it).</summary>
    public const string ScreenshotVariable = "PADURAKSA_E2E_SCREENSHOTS";

    public static async Task<(IPlaywright Playwright, IBrowser Browser)> LaunchOrSkipAsync()
    {
        IPlaywright playwright;
        try
        {
            playwright = await Playwright.CreateAsync();
        }
        catch (PlaywrightException ex)
        {
            Assert.Skip($"Playwright's driver is not available ({FirstLine(ex.Message)}).");
            throw;
        }

        string? executable = Environment.GetEnvironmentVariable(ExecutableVariable);
        try
        {
            IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                ExecutablePath = string.IsNullOrWhiteSpace(executable) ? null : executable,
            });
            return (playwright, browser);
        }
        catch (PlaywrightException ex) when (string.IsNullOrWhiteSpace(executable) && IsMissingBrowser(ex))
        {
            playwright.Dispose();
            Assert.Skip(
                "No Chromium for Playwright: run `pwsh bin/Release/net10.0/playwright.ps1 install chromium` in tests/Paduraksa.Tests.Dashboard "
                + $"(or set {ExecutableVariable} to an installed Chromium).");
            throw;
        }
    }

    /// <summary>Saves a full-page screenshot when <see cref="ScreenshotVariable"/> names a directory.</summary>
    public static async Task ScreenshotAsync(IPage page, string name)
    {
        string? directory = Environment.GetEnvironmentVariable(ScreenshotVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(directory, $"paduraksa-dashboard-{name}.png"), FullPage = true });
    }

    private static bool IsMissingBrowser(PlaywrightException ex) =>
        ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string message) => message.Split('\n', 2)[0].Trim();
}
