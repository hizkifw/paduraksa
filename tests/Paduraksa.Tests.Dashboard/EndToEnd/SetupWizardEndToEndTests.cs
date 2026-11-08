using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The first-run wizard in a real browser from a fresh instance (PLAN §12.1, M5): setup token, Owner, two-step sign-in with the QR step,
/// ACME and DNS skipped, a local-CA certificate, the first host to a test upstream (checked, reviewed, applied), done; then the
/// certificates page lists the certificate, a PEM is uploaded, a DNS provider is added and tested (only with PADURAKSA_E2E_LIVE_DNS=1), and the overview's checklist names
/// what was skipped. Skipped without Chromium; screenshots of every step when <see cref="PlaywrightBrowser.ScreenshotVariable"/> is set.
/// </summary>
public sealed class SetupWizardEndToEndTests
{
    private const string HostName = "app.home.arpa";

    /// <summary>Set to <c>1</c> to also add and test a DNS provider against the real Cloudflare API.</summary>
    private const string LiveDnsVariable = "PADURAKSA_E2E_LIVE_DNS";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheWizard_FromTokenToFirstHost_ThenCertificatesUploadAndDns()
    {
        (IPlaywright playwright, IBrowser browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using IPlaywright ownedPlaywright = playwright;
        await using IBrowser ownedBrowser = browser;
        await using DashboardHost host = await DashboardHost.StartAsync(Ct);
        using TcpListener upstream = new(IPAddress.Loopback, 0);
        upstream.Start();
        int upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        await using IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.Admin.ToString(),
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            ColorScheme = ColorScheme.Dark,
        });
        IPage page = await context.NewPageAsync();
        List<string> problems = [];
        page.Console += (_, message) =>
        {
            if (message.Text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase) || message.Type == "error")
            {
                problems.Add(message.Text);
            }
        };

        // 1-2. Token and Owner, on the plain setup page, with the strength hint.
        await page.GotoAsync("/");
        await Expect(page).ToHaveURLAsync(new Regex("/setup$"));
        await Expect(page.Locator(".pdk-wizard-rail [data-step=token]")).ToHaveAttributeAsync("aria-current", "step");
        await page.FillAsync("input[name=setupToken]", await host.SetupTokenAsync(Ct));
        await page.FillAsync("input[name=username]", DashboardHost.OwnerName);
        await page.FillAsync("input[name=displayName]", "Ada Lovelace");
        await page.FillAsync("input[name=password]", DashboardHost.OwnerPassword);
        await page.FillAsync("input[name=confirmPassword]", DashboardHost.OwnerPassword);
        await Expect(page.Locator("[data-strength-label]")).ToHaveTextAsync("Strong");
        await PlaywrightBrowser.ScreenshotAsync(page, "wizard-1-token-owner");
        await page.ClickAsync("button[type=submit]");

        // 3. Two-step sign-in: password, QR code (with the key for typing), a code, the recovery codes once.
        await Expect(page).ToHaveURLAsync(new Regex("/welcome$"));
        await ExpectStepAsync(page, "two-step");
        await PlaywrightBrowser.ScreenshotAsync(page, "wizard-2-two-step");
        await page.FillAsync(".pdk-totp-password input", DashboardHost.OwnerPassword);
        await page.ClickAsync(".pdk-totp-begin");
        await Expect(page.Locator("svg.pdk-qr")).ToBeVisibleAsync();
        await page.Locator(".pdk-totp-manual summary").ClickAsync();
        string secret = (await page.Locator(".pdk-totp-secret").TextContentAsync())!.Replace(" ", string.Empty, StringComparison.Ordinal);
        await PlaywrightBrowser.ScreenshotAsync(page, "wizard-2-two-step-qr");
        await page.FillAsync(".pdk-totp-code input", TotpOracle.CodeFor($"otpauth://totp/x?secret={secret}"));
        await page.ClickAsync(".pdk-totp-confirm");
        await Expect(page.Locator(".pdk-recovery-codes li")).ToHaveCountAsync(10);
        await PlaywrightBrowser.ScreenshotAsync(page, "wizard-2-two-step-codes");
        await page.Locator(".pdk-totp-saved input").CheckAsync();
        await page.ClickAsync(".pdk-totp-finish");
        // The secret is gone from the page once the step moved on.
        await Expect(page.Locator(".pdk-totp-secret")).ToHaveCountAsync(0);
        await Expect(page.Locator(".pdk-totp-on")).ToBeVisibleAsync();
        await page.ClickAsync(".pdk-wizard-next");

        // 4-5. ACME account and DNS provider: skipped (a LAN-only start).
        await ExpectStepAsync(page, "acme");
        await PlaywrightBrowser.ScreenshotAsync(page, "wizard-3-acme");
        await page.ClickAsync(".pdk-wizard-skip");
        await ExpectStepAsync(page, "dns");
        await PlaywrightBrowser.ScreenshotAsync(page, "wizard-4-dns");
        await page.ClickAsync(".pdk-wizard-skip");

        // 6. A certificate from the local CA (the only choice without an ACME account).
        await ExpectStepAsync(page, "certificate");
        await Expect(page.Locator(".pdk-wizard-rail [data-step=acme]")).ToHaveClassAsync(new Regex("pdk-wizard-skipped"));
        await page.FillAsync(".pdk-local-names textarea", HostName);
        await PlaywrightBrowser.ScreenshotAsync(page, "wizard-5-certificate");
        await page.ClickAsync(".pdk-local-submit");
        await Expect(page.Locator(".pdk-cert-issued")).ToContainTextAsync(HostName, new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "wizard-5-certificate-issued");
        await page.ClickAsync(".pdk-wizard-next");

        // 7. The first host: its name comes from the certificate; the upstream is checked, then reviewed and applied.
        await ExpectStepAsync(page, "host");
        await Expect(page.Locator(".pdk-host-name input")).ToHaveValueAsync(HostName);
        await page.FillAsync(".pdk-host-upstream input", $"http://127.0.0.1:{upstreamPort}");
        await page.ClickAsync(".pdk-host-check");
        await Expect(page.Locator(".pdk-probe-result")).ToHaveAttributeAsync("data-reachable", "true", new() { Timeout = 10_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "wizard-6-host");
        await page.ClickAsync(".pdk-host-review");
        ILocator apply = page.Locator(".pdk-sheet [data-review=apply]");
        await Expect(apply).ToBeEnabledAsync(new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "wizard-6-host-review", fullPage: false);
        await apply.ClickAsync();
        await Expect(page.Locator(".pdk-wizard-host-created")).ToContainTextAsync(HostName, new() { Timeout = 15_000 });
        await page.ClickAsync(".pdk-wizard-next");

        // Done: the summary, with what was skipped, and the root CA to trust.
        await ExpectStepAsync(page, "done");
        await Expect(page.Locator(".pdk-summary-item[data-step=host]")).ToHaveClassAsync(new Regex("pdk-summary-done"));
        await Expect(page.Locator(".pdk-summary-item[data-step=acme]")).ToContainTextAsync("Skipped");
        await Expect(page.Locator(".pdk-root-crt")).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "wizard-7-done");

        // The overview's checklist names the skipped ACME account (the DNS provider waits for one).
        await page.ClickAsync(".pdk-wizard-finish");
        await Expect(page.Locator(".pdk-checklist-item[data-step=acme]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-checklist-item[data-step=two-step]")).ToHaveCountAsync(0);
        await PlaywrightBrowser.ScreenshotAsync(page, "home-checklist");

        // The certificates page lists the new certificate and the hosts that use it.
        await page.GotoAsync("/certificates");
        ILocator row = page.Locator(".pdk-cert-row", new() { HasText = HostName });
        await Expect(row).ToContainTextAsync("Local CA");
        await Expect(row).ToContainTextAsync(HostName);
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "certificates");
        await row.ClickAsync();
        await Expect(page.Locator(".pdk-detail")).ToContainTextAsync("best match for");
        await PlaywrightBrowser.ScreenshotAsync(page, "certificates-detail", fullPage: false);
        await page.Keyboard.PressAsync("Escape");
        await page.GotoAsync("/certificates");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();

        // Upload a PEM (made by openssl, when it is installed: the test code can't export a key).
        if (await MakePemAsync() is { } pem)
        {
            try
            {
                await page.Locator(".pdk-add-menu button").First.ClickAsync();
                await page.Locator(".pdk-add-upload").ClickAsync();
                await page.Locator("input[type=file][aria-label='Certificate file']").SetInputFilesAsync(pem);
                await page.FillAsync(".pdk-upload-label input", "Uploaded in the test");
                await PlaywrightBrowser.ScreenshotAsync(page, "certificates-upload", fullPage: false);
                await page.ClickAsync(".pdk-upload-submit");
                await Expect(page.Locator(".pdk-cert-row", new() { HasText = "Uploaded in the test" })).ToContainTextAsync("Uploaded", new() { Timeout = 15_000 });
            }
            finally
            {
                File.Delete(pem);
            }
        }

        // A DNS provider, tested right after it is added. The test calls the real Cloudflare API (no fake is reachable through the
        // vault's handler, which only speaks https to the real host), so it runs only on request, like the live provider tests.
        if (Environment.GetEnvironmentVariable(LiveDnsVariable) == "1")
        {
            await page.GotoAsync("/settings/dns-providers");
            await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
            await page.ClickAsync(".pdk-add-dns");
            await page.FillAsync(".pdk-dns-label input", "Cloudflare - test");
            await page.FillAsync(".pdk-dns-credential input", "not-a-real-token");
            await page.ClickAsync(".pdk-dns-submit");
            await Expect(page.Locator(".pdk-dns-row")).ToContainTextAsync("Cloudflare - test");
            await Expect(page.Locator(".pdk-test-result:not(.pdk-test-running)")).ToBeVisibleAsync(new() { Timeout = 45_000 });
            await Expect(page.Locator(".pdk-dns-row")).Not.ToContainTextAsync("not-a-real-token");
            await PlaywrightBrowser.ScreenshotAsync(page, "dns-providers");
        }

        await page.GotoAsync("/settings/acme-accounts");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "acme-accounts");
        await page.GotoAsync("/settings");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "settings");

        Assert.Empty(problems);
    }

    private static async Task ExpectStepAsync(IPage page, string slug)
    {
        await Expect(page.Locator($".pdk-wizard-card[data-step={slug}]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
    }

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
