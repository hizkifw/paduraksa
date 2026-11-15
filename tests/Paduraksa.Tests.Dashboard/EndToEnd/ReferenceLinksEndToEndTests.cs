using System.Text;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Issuance;
using Paduraksa.Application.Notifications;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Data.Issuance;
using Paduraksa.Server.Hosting;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// References between resources, in a real browser: a certificate's drawer to the host that uses it and back with the browser's Back, and an ACME
/// account's drawer to a certificate and on to the account again; a DNS provider is renamed in its drawer. The account and the order are written the
/// way the services leave them (no CA is reachable from a test); the certificate is a real local-CA leaf and the host a real config commit.
/// Skipped when no Chromium is installed (see <see cref="PlaywrightBrowser"/>).
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class ReferenceLinksEndToEndTests
{
    private const string HostName = "app.home.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACertificateLinksItsHostAndBack_AnAccountLinksItsCertificateAndBack_AndAProviderIsRenamed()
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
            ColorScheme = ColorScheme.Dark,
        });
        var page = await context.NewPageAsync();
        var problems = PlaywrightBrowser.CollectProblems(page);
        await SetUpAsync(page, host);
        var seeded = await SeedAsync(host);

        // A certificate to the host that uses it, and back: the browser's Back returns to the certificate's own address, with its drawer open.
        await page.GotoAsync($"/certificates/{seeded.Certificate}");
        var certificate = page.Locator(".pdk-sheet[data-top] [data-drawer=certificate]");
        await Expect(certificate).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "references-certificate-drawer");
        await certificate.Locator($"a[data-use='{seeded.Host}']").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex($"/hosts/{seeded.Host}$"));
        await Expect(page.Locator(".pdk-sheet[data-top] [data-drawer=host]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-sheet[data-top] .pdk-drawer-title")).ToHaveTextAsync(HostName);
        await PlaywrightBrowser.ScreenshotAsync(page, "references-host-drawer");
        await page.GoBackAsync();
        await Expect(page).ToHaveURLAsync(new Regex($"/certificates/{seeded.Certificate}$"));
        await Expect(page.Locator(".pdk-sheet[data-top] [data-drawer=certificate]")).ToBeVisibleAsync(new() { Timeout = 15_000 });

        // The certificate names its account and its DNS provider, each a link. Scoped to the top sheet: after Back the one the page left is still in the
        // document for a moment (the server's stack is already right), and a locator that matches two elements is an error, not a wait.
        await Expect(page.Locator(".pdk-sheet[data-top] [data-drawer=certificate] a[data-link=acme-account]")).ToHaveAttributeAsync("href", $"/certificates/acme/{seeded.Account}");
        await Expect(page.Locator(".pdk-sheet[data-top] [data-drawer=certificate] a[data-link=dns-provider]")).ToHaveAttributeAsync("href", $"/certificates/dns/{seeded.Provider}");

        // An account to a certificate, and on to the account again.
        await page.GotoAsync($"/certificates/acme/{seeded.Account}");
        var account = page.Locator(".pdk-sheet[data-top] [data-drawer=acme-account-details]");
        await Expect(account).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(account.Locator("[data-field=status]")).ToHaveTextAsync("Active");
        await PlaywrightBrowser.ScreenshotAsync(page, "references-acme-account-drawer");
        await account.Locator($"[data-certificate='{seeded.Certificate}'] a").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex($"/certificates/{seeded.Certificate}$"));
        await Expect(page.Locator(".pdk-sheet[data-top] [data-drawer=certificate]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await page.Locator(".pdk-sheet[data-top] [data-drawer=certificate] a[data-link=acme-account]").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex($"/certificates/acme/{seeded.Account}$"));
        await Expect(page.Locator(".pdk-sheet[data-top] [data-drawer=acme-account-details]")).ToBeVisibleAsync(new() { Timeout = 15_000 });

        // The accounts list: the Deactivated tab is a line, not a table.
        await page.Keyboard.PressAsync("Escape");
        await Expect(page).ToHaveURLAsync(new Regex("/certificates/acme$"));
        await page.Locator("[data-field=filter] [data-value=deactivated]").ClickAsync();
        await Expect(page.Locator("[data-empty=deactivated]")).ToHaveTextAsync("No deactivated accounts.");
        await PlaywrightBrowser.ScreenshotAsync(page, "references-acme-accounts-deactivated");

        // The audit event's drawer has no address of its own; its target link opens the host's drawer, and only that one stays.
        await page.GotoAsync("/audit");
        await Expect(page.Locator("[data-action='config.host.create']").First).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await page.Locator("[data-action='config.host.create'] button").First.ClickAsync();
        var event_ = page.Locator(".pdk-sheet[data-top] [data-drawer=audit-event]");
        await Expect(event_).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "references-audit-drawer", fullPage: false);
        await event_.Locator($"a[href='/hosts/{seeded.Host}']").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex($"/hosts/{seeded.Host}$"));
        await Expect(page.Locator(".pdk-sheet[data-top] [data-drawer=host]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(1);
        await Expect(page.Locator("[data-drawer=audit-event]")).ToHaveCountAsync(0);

        // A DNS provider: its drawer lists the certificate, and the name is changed there.
        await page.GotoAsync($"/certificates/dns/{seeded.Provider}");
        var provider = page.Locator(".pdk-sheet[data-top] [data-drawer=dns-provider-details]");
        await Expect(provider).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(provider.Locator($"[data-certificate='{seeded.Certificate}'] a")).ToBeVisibleAsync();
        await provider.Locator("input[data-field=label]").FillAsync("Cloudflare (lab)");
        await provider.Locator("[data-action=rename]").ClickAsync();
        await Expect(page.Locator(".pdk-sheet[data-top] .pdk-drawer-title")).ToHaveTextAsync("Cloudflare (lab)", new() { Timeout = 15_000 });
        var renamed = await host.Host.GetServices(HostRole.Admin).GetRequiredService<DnsProviderService>().GetAsync(seeded.Provider, Ct);
        Assert.Equal("Cloudflare (lab)", renamed!.Label);
        await PlaywrightBrowser.ScreenshotAsync(page, "references-dns-provider-drawer");

        Assert.Empty(problems);
    }

    [Fact]
    public async Task AChannelsAddressOpensItsDrawerForAnOwner_AndTheListAloneForAViewer()
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var host = await DashboardHost.StartAsync(Ct);
        var options = new BrowserNewContextOptions { BaseURL = host.Admin.ToString(), IgnoreHTTPSErrors = true, ViewportSize = new ViewportSize { Width = 1366, Height = 900 }, ColorScheme = ColorScheme.Dark };
        await using var context = await browser.NewContextAsync(options);
        var page = await context.NewPageAsync();
        var problems = PlaywrightBrowser.CollectProblems(page);
        await SetUpAsync(page, host);
        var created = await host.Host.GetServices(HostRole.Admin).GetRequiredService<NotificationService>().CreateAsync(
            new NotificationChannelBody(NotificationChannelKind.Webhook, "Ops hook", [NotificationEventKind.UpstreamDown], Secret: "http://127.0.0.1:1/hook"), CommandOrigin.Cli, Ct);
        Assert.Equal(IssuanceChangeStatus.Committed, created.Status);
        var channel = created.Channel!.Id;

        await page.GotoAsync($"/settings/notifications/{channel}");
        await Expect(page.Locator(".pdk-sheet[data-top] [data-drawer=notification-channel]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "references-channel-drawer", fullPage: false);
        await page.GotoAsync("/settings/notifications");
        await Expect(page.Locator("[data-channel] a[data-link=channel]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "references-notifications");

        // A Viewer reads the channels; the drawer is a form, so its address gives them the list and no drawer, and no link.
        await page.GotoAsync("/settings/users");
        await page.ClickAsync("[data-action=add-user]");
        await page.FillAsync("input[data-field=username]", "viewer");
        await page.FillAsync("input[data-field=display-name]", "Vera Viewer");
        await page.FillAsync("input[data-field=password]", "a viewer's long password");
        await page.ClickAsync("[data-action=save-user]");
        await Expect(page.Locator("[data-user=viewer]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await using var viewerContext = await browser.NewContextAsync(options);
        var viewer = await viewerContext.NewPageAsync();
        await viewer.GotoAsync("/signin");
        await viewer.FillAsync("input[name=username]", "viewer");
        await viewer.FillAsync("input[name=password]", "a viewer's long password");
        await viewer.ClickAsync("button[type=submit]");
        await Expect(viewer.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await viewer.GotoAsync($"/settings/notifications/{channel}");
        await Expect(viewer.Locator("[data-channel]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(viewer.Locator(".pdk-sheet")).ToHaveCountAsync(0);
        await Expect(viewer.Locator("[data-channel] a[data-link=channel]")).ToHaveCountAsync(0);

        Assert.Empty(problems);
    }

    /// <summary>A local-CA certificate for <see cref="HostName"/>, a host that uses it, an ACME account and a DNS provider, and the order that ties the certificate to both.</summary>
    private static async Task<(CertificateId Certificate, HostId Host, AcmeAccountId Account, DnsProviderId Provider)> SeedAsync(DashboardHost host)
    {
        var services = host.Host.GetServices(HostRole.Admin);
        var operations = services.GetRequiredService<CertificateOperations>();
        var issued = await Paduraksa.Dashboard.Certificates.LocalCa.IssueAsync(
            operations, new Paduraksa.Dashboard.Certificates.LocalCertificateRequest([HostName], KeySpec.EcdsaP256, TimeSpan.FromDays(60), "Home certificate"), CommandOrigin.Cli);
        Assert.Equal(CertificateOperationStatus.Completed, issued.Status);
        var certificate = issued.Certificate!.Id;

        UpstreamConfig web = new(UpstreamId.New(), "web", Destinations: [new DestinationConfig(DestinationId.New(), "http://127.0.0.1:18090")]);
        Assert.Equal(ConfigCommandStatus.Committed, (await host.Host.Shared.Config.ExecuteAsync(new UpsertUpstreamCommand(web), CommandOrigin.Cli, Ct)).Status);
        HostConfig proxied = new(HostId.New(), [HostName], Routes: [new RouteConfig(RouteId.New(), web.Id)]);
        Assert.Equal(ConfigCommandStatus.Committed, (await host.Host.Shared.Config.ExecuteAsync(new UpsertHostCommand(proxied), CommandOrigin.Cli, Ct)).Status);

        var created = await services.GetRequiredService<DnsProviderService>().CreateWithCredentialAsync(
            DnsProviderKind.Cloudflare, "Cloudflare home", Encoding.UTF8.GetBytes("not-a-real-token"), CommandOrigin.Cli, Ct);
        Assert.Equal(IssuanceChangeStatus.Committed, created.Status);
        var provider = created.Provider!.Id;

        var now = DateTimeOffset.UtcNow;
        AcmeAccountId account = AcmeAccountId.New();
        await using (var db = await services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync(Ct))
        {
            db.AcmeAccounts.Add(new AcmeAccountRow
            {
                Id = account,
                DirectoryUrl = "https://acme-v02.api.letsencrypt.org/directory",
                Contacts = ["mailto:ops@home.test"],
                Status = AcmeAccountStatus.Active,
                AccountUrl = "https://acme-v02.api.letsencrypt.org/acme/acct/1",
                KeyAlgorithm = JwsAlgorithm.ES256,
                TermsOfServiceAgreed = true,
                Label = "Home",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.AcmeOrders.Add(new AcmeOrderRow
            {
                Id = AcmeOrderId.New(),
                CertificateId = certificate,
                AccountId = account,
                Status = AcmeOrderStatus.Valid,
                ChallengeType = AcmeChallengeType.Dns01,
                DnsProviderId = provider,
                KeySpec = KeySpec.EcdsaP256,
                Names = [HostName],
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(Ct);
        }

        return (certificate, proxied.Id, account, provider);
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
        await Expect(page).ToHaveURLAsync(new Regex("/$"));
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
    }
}
