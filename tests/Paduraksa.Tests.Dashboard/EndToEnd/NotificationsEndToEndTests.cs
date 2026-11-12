using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// Adding a notification channel sends it one real test message at once, so the operator learns whether it works; editing one does not.
/// Skipped without Chromium (see <see cref="PlaywrightBrowser"/>).
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class NotificationsEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AddingAChannelSendsATestMessageAtOnce_AndEditingOneDoesNot()
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var host = await DashboardHost.StartAsync(Ct);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = host.Admin.ToString(), IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await SetUpAsync(page, host);
        using var ntfy = ListenOnLoopback(out var port);
        var posts = 0;
        var serving = Task.Run(async () =>
        {
            while (!Ct.IsCancellationRequested)
            {
                var request = await ntfy.GetContextAsync().WaitAsync(Ct);
                // A port prober on a developer machine sends GET / to a new port: only what the sender posts counts.
                if (request.Request.HttpMethod == "POST")
                {
                    Interlocked.Increment(ref posts);
                }

                request.Response.StatusCode = 200;
                request.Response.Close();
            }
        }, Ct);

        await page.GotoAsync("/settings/notifications");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await page.Locator("[data-action=add-channel]").First.ClickAsync();
        await page.Locator(Field("label")).First.FillAsync("Lab ntfy");
        await page.Locator(Field("server-url")).First.FillAsync($"http://127.0.0.1:{port}");
        await page.Locator(Field("topic")).First.FillAsync("lab");
        await page.ClickAsync(".pdk-notify-submit");

        await Expect(page.Locator(".pdk-test-ok")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        Assert.Equal(1, Volatile.Read(ref posts));

        await page.Locator("[data-action=edit]").First.ClickAsync();
        await Expect(page.Locator(Field("label")).First).ToHaveValueAsync("Lab ntfy", new() { Timeout = 15_000 });
        var label = page.Locator(Field("label")).First;
        await label.ClickAsync();
        await label.PressAsync("Control+A");
        await label.PressSequentiallyAsync("Lab ntfy 2");
        await label.PressAsync("Tab");
        await Expect(label).ToHaveValueAsync("Lab ntfy 2");
        await page.ClickAsync(".pdk-notify-submit");
        await Expect(page.Locator(".pdk-provider-title")).ToHaveTextAsync("Lab ntfy 2", new() { Timeout = 15_000 });
        Assert.Equal(1, Volatile.Read(ref posts));
        await Expect(page.Locator(".pdk-test-result")).ToHaveCountAsync(0);
    }

    private static string Field(string name) => $"input[data-field={name}], [data-field={name}] input";

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
