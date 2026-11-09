using System.Diagnostics;
using System.Text.RegularExpressions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// First-run setup in a real browser from a fresh instance (PLAN §12.1, M5): setup token and Owner, then straight to the overview, whose
/// checklist names what is left and links to the pages that do it; two-step sign-in turned on from My account (the QR step, the recovery
/// codes once); a local-CA certificate and an uploaded PEM from the certificates page; a DNS provider added and tested (only with
/// PADURAKSA_E2E_LIVE_DNS=1). Skipped without Chromium; screenshots of each page when <see cref="PlaywrightBrowser.ScreenshotVariable"/> is set.
/// </summary>
public sealed class FirstRunEndToEndTests
{
    private const string HostName = "app.home.arpa";

    /// <summary>Set to <c>1</c> to also add and test a DNS provider against the real Cloudflare API.</summary>
    private const string LiveDnsVariable = "PADURAKSA_E2E_LIVE_DNS";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Setup_LandsOnTheOverview_ThenTwoStepAndCertificatesFromTheirPages()
    {
        (IPlaywright playwright, IBrowser browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using IPlaywright ownedPlaywright = playwright;
        await using IBrowser ownedBrowser = browser;
        await using DashboardHost host = await DashboardHost.StartAsync(Ct);
        await using IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.Admin.ToString(),
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            ColorScheme = ColorScheme.Dark,
        });
        IPage page = await context.NewPageAsync();
        List<string> problems = PlaywrightBrowser.CollectProblems(page);

        // Token and Owner, on the plain setup page, with the strength hint.
        await page.GotoAsync("/");
        await Expect(page).ToHaveURLAsync(new Regex("/setup$"));
        await page.FillAsync("input[name=setupToken]", await host.SetupTokenAsync(Ct));
        await page.FillAsync("input[name=username]", DashboardHost.OwnerName);
        await page.FillAsync("input[name=displayName]", "Ada Lovelace");
        await page.FillAsync("input[name=password]", DashboardHost.OwnerPassword);
        await page.FillAsync("input[name=confirmPassword]", DashboardHost.OwnerPassword);
        await Expect(page.Locator("[data-strength-label]")).ToHaveTextAsync("Strong");
        await PlaywrightBrowser.ScreenshotAsync(page, "setup");
        await page.ClickAsync("button[type=submit]");

        // Straight to the overview: the checklist names what is left (the DNS provider waits for an ACME account).
        await Expect(page).ToHaveURLAsync(new Regex("/$"));
        await ExpectLiveAsync(page);
        await Expect(page.Locator(".pdk-checklist-item[data-step=two-step]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-checklist-item[data-step=acme]")).ToBeVisibleAsync();
        await Expect(page.Locator(".pdk-checklist-item[data-step=certificate]")).ToBeVisibleAsync();
        await Expect(page.Locator(".pdk-checklist-item[data-step=host]")).ToBeVisibleAsync();
        await Expect(page.Locator(".pdk-checklist-item[data-step=dns]")).ToHaveCountAsync(0);
        await PlaywrightBrowser.ScreenshotAsync(page, "home-checklist");

        // Two-step sign-in, from the checklist to My account: password, QR code (with the key for typing), a code, the recovery codes once.
        await page.Locator(".pdk-checklist-item[data-step=two-step] a").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/account$"));
        await ExpectLiveAsync(page);
        await page.ClickAsync("[data-action=enrol]");
        await page.FillAsync(".pdk-totp-password input", DashboardHost.OwnerPassword);
        await page.ClickAsync(".pdk-totp-begin");
        await Expect(page.Locator("svg.pdk-qr")).ToBeVisibleAsync();
        await page.Locator(".pdk-totp-manual summary").ClickAsync();
        string secret = (await page.Locator(".pdk-totp-secret").TextContentAsync())!.Replace(" ", string.Empty, StringComparison.Ordinal);
        await PlaywrightBrowser.ScreenshotAsync(page, "two-step-qr", fullPage: false);
        await page.FillAsync(".pdk-totp-code input", TotpOracle.CodeFor($"otpauth://totp/x?secret={secret}"));
        await page.ClickAsync(".pdk-totp-confirm");
        await Expect(page.Locator(".pdk-recovery-codes li")).ToHaveCountAsync(10);
        await PlaywrightBrowser.ScreenshotAsync(page, "two-step-codes", fullPage: false);
        await page.Locator(".pdk-totp-saved input").CheckAsync();
        await page.ClickAsync(".pdk-totp-finish");
        await Expect(page.Locator("[data-card=two-factor]")).ToHaveAttributeAsync("data-state", "on", new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-totp-secret")).ToHaveCountAsync(0);

        // A certificate from the local CA, from the certificates page's Add.
        await page.GotoAsync("/certificates");
        await ExpectLiveAsync(page);
        await page.ClickAsync("[data-action=add-certificate]");
        await page.ClickAsync(".pdk-sheet[data-top] [data-method=localcaleaf]");
        await page.FillAsync(".pdk-sheet[data-top] .pdk-local-names textarea", HostName);
        await PlaywrightBrowser.ScreenshotAsync(page, "certificate-local", fullPage: false);
        await page.ClickAsync(".pdk-sheet[data-top] .pdk-local-submit");
        ILocator row = page.Locator(".pdk-certs-table .pdk-dt-row", new() { HasText = HostName });
        await Expect(row).ToContainTextAsync("Local CA", new() { Timeout = 15_000 });
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(0);
        await row.ClickAsync();
        await Expect(page.Locator(".pdk-sheet[data-top] .pdk-detail")).ToBeVisibleAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/certificates/crt_"));
        await PlaywrightBrowser.ScreenshotAsync(page, "certificates-detail", fullPage: false);
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(0);
        await Expect(page).ToHaveURLAsync(new Regex("/certificates$"));

        // Upload a PEM (made by openssl, when it is installed: the test code can't export a key).
        if (await MakePemAsync() is { } pem)
        {
            try
            {
                await page.ClickAsync("[data-action=add-certificate]");
                await page.ClickAsync(".pdk-sheet[data-top] [data-method=upload]");
                await page.Locator("input[type=file][aria-label='Certificate file']").SetInputFilesAsync(pem);
                await page.FillAsync(".pdk-upload-label input", "Uploaded in the test");
                await PlaywrightBrowser.ScreenshotAsync(page, "certificates-upload", fullPage: false);
                await page.ClickAsync(".pdk-upload-submit");
                await Expect(page.Locator(".pdk-certs-table .pdk-dt-row", new() { HasText = "Uploaded in the test" })).ToContainTextAsync("Uploaded", new() { Timeout = 15_000 });
            }
            finally
            {
                File.Delete(pem);
            }
        }

        // Back on the overview, the checklist lists only what is still open.
        await page.GotoAsync("/");
        await ExpectLiveAsync(page);
        await Expect(page.Locator(".pdk-checklist-item[data-step=acme]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-checklist-item[data-step=two-step]")).ToHaveCountAsync(0);
        await Expect(page.Locator(".pdk-checklist-item[data-step=certificate]")).ToHaveCountAsync(0);

        // A DNS provider, tested right after it is added. The test calls the real Cloudflare API (no fake is reachable through the
        // vault's handler, which only speaks https to the real host), so it runs only on request, like the live provider tests.
        if (Environment.GetEnvironmentVariable(LiveDnsVariable) == "1")
        {
            await page.GotoAsync("/certificates/dns");
            await ExpectLiveAsync(page);
            await page.ClickAsync(".pdk-add-dns");
            await page.FillAsync(".pdk-dns-label input", "Cloudflare - test");
            await page.FillAsync(".pdk-dns-credential input", "not-a-real-token");
            await page.ClickAsync(".pdk-dns-submit");
            await Expect(page.Locator(".pdk-dns-row")).ToContainTextAsync("Cloudflare - test");
            await Expect(page.Locator(".pdk-test-result:not(.pdk-test-running)")).ToBeVisibleAsync(new() { Timeout = 45_000 });
            await Expect(page.Locator(".pdk-dns-row")).Not.ToContainTextAsync("not-a-real-token");
            await PlaywrightBrowser.ScreenshotAsync(page, "dns-providers");
        }

        await page.GotoAsync("/certificates/acme");
        await ExpectLiveAsync(page);
        await PlaywrightBrowser.ScreenshotAsync(page, "acme-accounts");

        Assert.Empty(problems);
    }

    private static Task ExpectLiveAsync(IPage page) => Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });

    /// <summary>A self-signed certificate with its PKCS#8 key in one PEM file, or null without openssl.</summary>
    private static async Task<string?> MakePemAsync()
    {
        string directory = Directory.CreateTempSubdirectory("paduraksa-pem-").FullName;
        string key = Path.Combine(directory, "key.pem");
        string certificate = Path.Combine(directory, "cert.pem");
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("openssl")
            {
                ArgumentList =
                {
                    "req", "-x509", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:P-256", "-nodes", "-days", "30",
                    "-subj", "/CN=upload.home.arpa", "-addext", "subjectAltName=DNS:upload.home.arpa", "-addext", "basicConstraints=critical,CA:FALSE", "-keyout", key, "-out", certificate,
                },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            if (process is null)
            {
                return null;
            }

            await process.WaitForExitAsync(Ct);
            if (process.ExitCode != 0)
            {
                return null;
            }

            string combined = Path.Combine(Directory.CreateTempSubdirectory("paduraksa-upload-").FullName, "upload.pem");
            await File.WriteAllTextAsync(combined, await File.ReadAllTextAsync(certificate, Ct) + await File.ReadAllTextAsync(key, Ct), Ct);
            return combined;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
