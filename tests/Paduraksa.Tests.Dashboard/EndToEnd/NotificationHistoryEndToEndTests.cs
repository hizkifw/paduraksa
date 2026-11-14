using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Notifications;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Notifications;
using Paduraksa.Server.Hosting;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The notification history in a real browser on a real host: an event whose delivery is refused shows as failed with the error in its tooltip, a click on
/// "send again" delivers it (the same event ID, twice at the receiver), and the channel's row and drawer show the outcome. Skipped without Chromium (see <see cref="PlaywrightBrowser"/>).
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class NotificationHistoryEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFailedDeliveryIsSentAgainFromTheHistoryAndTheChannelShowsTheOutcome()
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var host = await DashboardHost.StartAsync(Ct);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = host.Admin.ToString(), IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await SetUpAsync(page, host);

        using var receiver = ListenOnLoopback(out var port);
        var status = 401;
        List<string?> eventIds = [];
        var serving = Task.Run(async () =>
        {
            while (!Ct.IsCancellationRequested)
            {
                var request = await receiver.GetContextAsync().WaitAsync(Ct);
                // A port prober on a developer machine sends GET / to a new port: only what the sender posts counts.
                if (request.Request.HttpMethod == "POST")
                {
                    lock (eventIds)
                    {
                        eventIds.Add(request.Request.Headers["X-Paduraksa-Event-Id"]);
                    }

                    request.Response.StatusCode = Volatile.Read(ref status);
                }

                request.Response.Close();
            }
        }, Ct);

        var services = host.Host.GetServices(HostRole.Admin);
        var created = await services.GetRequiredService<NotificationService>().CreateAsync(
            new NotificationChannelBody(NotificationChannelKind.Webhook, "Ops hook", [NotificationEventKind.UpstreamDown], Secret: $"http://127.0.0.1:{port}/hook"), CommandOrigin.Cli, Ct);
        Assert.Equal(Paduraksa.Contracts.Issuance.IssuanceChangeStatus.Committed, created.Status);
        services.GetRequiredService<INotificationEvents>().Publish(new NotificationEvent(
            NotificationEventKind.UpstreamDown, "Upstream media is down", "No destination takes traffic.", NotificationSeverity.Critical, DateTimeOffset.UtcNow, "/upstreams", null, "ups_media"));

        await page.GotoAsync("/settings/notifications/history");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        var failed = page.Locator("[data-event] [data-chip=delivery-failed]");
        await Expect(failed).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(failed).ToHaveTextAsync("Ops hook failed");
        await Expect(failed).ToHaveAttributeAsync("title", new Regex("HTTP 401"));
        await Expect(page.Locator("[data-event] [data-part=subject]")).ToHaveTextAsync("ups_media");

        Volatile.Write(ref status, 200);
        await page.Locator("[data-event] [data-action=retry]").ClickAsync();
        await Expect(page.Locator("[data-event] [data-chip=delivery-sent]")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(page.Locator("[data-action=retry]")).ToHaveCountAsync(0);
        lock (eventIds)
        {
            Assert.Equal(2, eventIds.Count);
            Assert.Single(eventIds.Distinct());
            Assert.StartsWith("nev_", eventIds[0], StringComparison.Ordinal);
        }

        // The prerendered page has no buttons yet: one clicked before the circuit is live would do nothing. Read in one script run, so the circuit can't go live between the two checks.
        await page.GotoAsync("/settings/notifications", new() { WaitUntil = WaitUntilState.Commit });
        await page.WaitForFunctionAsync("document.querySelector('.pdk-provider-list') !== null");
        Assert.True(await page.EvaluateAsync<bool>("document.querySelector('.pdk-shell').dataset.circuit !== 'prerendered' || document.querySelector('[data-action=edit]') === null"));
        await Expect(page.Locator("[data-channel] [data-part=success-rate]")).ToHaveTextAsync(new Regex("100% delivered"), new() { Timeout = 15_000 });
        await page.Locator("[data-action=edit]").First.ClickAsync();
        await Expect(page.Locator("[data-section=recent-deliveries] [data-event]")).ToHaveCountAsync(1, new() { Timeout = 15_000 });
        await Expect(page.Locator("[data-section=recent-deliveries] [data-chip=delivery-sent]")).ToHaveTextAsync("Sent");
    }

    private static HttpListener ListenOnLoopback(out int port)
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
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
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
    }
}
