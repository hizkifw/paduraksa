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

        var executable = Environment.GetEnvironmentVariable(ExecutableVariable);
        try
        {
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
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

    /// <summary>
    /// Waits until the page's finite animations (a drawer sliding in, a sheet opening) have finished, so a measurement or a
    /// screenshot sees where things settle: the signal is the animations' own state, not a guessed delay.
    /// </summary>
    public static async Task SettleAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        await page.WaitForFunctionAsync(
            "() => document.getAnimations().every(a => a.playState === 'finished')",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    /// <summary>
    /// Starts collecting the page's console problems: a Content Security Policy violation, or a console error, apart from
    /// <paramref name="expected"/> (a failure the test asks for itself) and the browser's own report of a lost circuit.
    /// </summary>
    public static List<string> CollectProblems(IPage page, params string[] expected)
    {
        ArgumentNullException.ThrowIfNull(page);
        List<string> problems = [];
        page.Console += (_, message) =>
        {
            if (message.Text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase)
                || (message.Type == "error" && !IsExpected(message.Text, expected)))
            {
                problems.Add(message.Text);
            }
        };
        return problems;
    }

    private static bool IsExpected(string text, string[] expected) =>
        IsCircuitReconnect(text) || expected.Any(fragment => text.Contains(fragment, StringComparison.Ordinal));

    /// <summary>
    /// Blazor's "Error: Connection disconnected with error ...": the circuit lost its WebSocket and the page reconnects. Under
    /// a loaded machine that happens, and it is the transport's business, not an app error.
    /// </summary>
    private static bool IsCircuitReconnect(string text) => text.Contains("Connection disconnected", StringComparison.Ordinal);

    /// <summary>
    /// Saves a screenshot when <see cref="ScreenshotVariable"/> names a directory: the full page, or only the window (for a drawer, which
    /// sits fixed over the page).
    /// </summary>
    public static async Task ScreenshotAsync(IPage page, string name, bool fullPage = true)
    {
        var directory = Environment.GetEnvironmentVariable(ScreenshotVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        // Finite animations (the drawer sliding in, a dialog appearing) are taken at their end, so a shot shows where things settle.
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(directory, $"paduraksa-dashboard-{name}.png"),
            FullPage = fullPage,
            Animations = ScreenshotAnimations.Disabled,
        });
    }

    private static bool IsMissingBrowser(PlaywrightException ex) =>
        ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string message) => message.Split('\n', 2)[0].Trim();
}
