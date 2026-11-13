using System.Diagnostics;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Application.Logging;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Logging;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.LogFiles;
using Paduraksa.Dashboard.Pages;
using Paduraksa.Proxy.Logging;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The live tail page: what it shows, the filters, pause and the hidden tab, the batching that keeps a busy site from setting the render rate.</summary>
public sealed class LiveTailPageTests
{
    private static readonly TimeSpan Batch = TimeSpan.FromMilliseconds(40);

    private static AccessLogEntry Entry(string path, int status = 200, string host = "app.test", string method = "GET", string client = "198.51.100.7") => new()
    {
        TimestampUtcTicks = new DateTime(2026, 11, 12, 10, 30, 15, 123, DateTimeKind.Utc).Ticks,
        ClientAddress = client,
        Host = host,
        Method = method,
        Path = path,
        Protocol = "HTTP/2",
        StatusCode = status,
        TotalTicks = 12_340,
        UpstreamTicks = 9_870,
        UpstreamAddress = "http://10.0.0.2:8080/",
        RequestId = "rid-" + path,
    };

    private static (AccessLogTail Hub, DashboardTestContext Context, FakeDashboardConfig Config) SignIn(Role role, int capacity = 4_096)
    {
        DashboardTestContext context = new();
        AccessLogTail hub = new(capacity, AccessLogFormatOptions.Default);
        context.Services.AddSingleton(hub);
        context.Services.AddSingleton(new LogTailService(hub));
        var (_, _, config, _) = IdentityPages.SignIn(context, role);
        context.Render<MudPopoverProvider>();
        return (hub, context, config);
    }

    private static IRenderedComponent<CountedLiveTail> Render(DashboardTestContext context) =>
        context.Render<CountedLiveTail>(parameters => parameters.Add(p => p.BatchInterval, Batch));

    /// <summary>The page counts a render after the renderer has applied it, which is after a wait on the markup has been satisfied.</summary>
    private static async Task Rendered(IRenderedComponent<CountedLiveTail> page, int after)
    {
        for (var attempt = 0; attempt < 200 && page.Instance.Renders <= after; attempt++)
        {
            await Task.Delay(10, Xunit.TestContext.Current.CancellationToken);
        }
    }

    private static string[] Paths(IRenderedComponent<CountedLiveTail> page) =>
        [.. page.FindAll("[data-seq] .pdk-tail-path").Select(link => link.TextContent.Trim())];

    [Fact]
    public async Task RequestsAreListedNewestFirstWithAStatusColouredByClass()
    {
        var (hub, context, _) = SignIn(Role.Owner);
        await using (context)
        {
            hub.Offer(Entry("/old"));
            var page = Render(context);
            hub.Offer(Entry("/ok"));
            hub.Offer(Entry("/moved", 301));
            hub.Offer(Entry("/missing", 404));
            hub.Offer(Entry("/broken", 502));

            await page.WaitForStateAsync(() => Paths(page).Length == 5);

            Assert.Equal(["/broken", "/missing", "/moved", "/ok", "/old"], Paths(page));
            Assert.Contains("pdk-tone-success", page.Find("[data-seq][data-status='200'] .pdk-badge").ClassName, StringComparison.Ordinal);
            Assert.Contains("pdk-tone-info", page.Find("[data-status='301'] .pdk-badge").ClassName, StringComparison.Ordinal);
            Assert.Contains("pdk-tone-warning", page.Find("[data-status='404'] .pdk-badge").ClassName, StringComparison.Ordinal);
            Assert.Contains("pdk-tone-error", page.Find("[data-status='502'] .pdk-badge").ClassName, StringComparison.Ordinal);
            var row = page.Find("[data-status='502']");
            Assert.Equal("10:30:15.123", row.QuerySelector(".pdk-tail-time")!.TextContent);
            Assert.Contains("1.2 ms", row.TextContent, StringComparison.Ordinal);
            Assert.Contains("10.0.0.2:8080", row.TextContent, StringComparison.Ordinal);
            Assert.Contains("rid-/broken", row.TextContent, StringComparison.Ordinal);
            Assert.Contains("198.51.100.7", row.TextContent, StringComparison.Ordinal);
            Assert.Contains("Following", page.Find("[data-part=state]").TextContent, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnEmptyTailSaysWhatToExpect()
    {
        var (_, context, _) = SignIn(Role.Owner);
        await using (context)
        {
            var page = Render(context);

            Assert.Contains("No requests yet", page.Markup, StringComparison.Ordinal);
            Assert.Contains("A host with its access log off never does.", page.Markup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ABurstIsRenderedOnceAndTheTableKeepsOnlyTheNewestRows()
    {
        var (hub, context, _) = SignIn(Role.Owner);
        await using (context)
        {
            var page = Render(context);
            var rendersBefore = page.Instance.Renders;

            for (var i = 0; i < 1_500; i++)
            {
                hub.Offer(Entry("/burst/" + i));
            }

            await page.WaitForStateAsync(() => page.FindAll("[data-seq]").Count == LiveTail.MaxRows);
            await Rendered(page, rendersBefore);

            Assert.Equal("/burst/1499", Paths(page)[0]);
            Assert.Equal("/burst/1000", Paths(page)[^1]);
            // Fifteen hundred requests, a handful of renders: a batch is one render, never one a request.
            Assert.InRange(page.Instance.Renders - rendersBefore, 1, 8);
            // How many are skipped depends on how the burst fell into batches (one read of 1,500, or several on a slow machine);
            // that some were, and said so, is the point.
            Assert.Matches(@"^[\d,]+ lines skipped$", page.Find("[data-part=skipped]").TextContent.Trim());
        }
    }

    [Fact]
    public async Task PausingKeepsTheViewWhileTheBufferMovesOnAndFollowingCatchesUp()
    {
        var (hub, context, _) = SignIn(Role.Owner);
        await using (context)
        {
            var page = Render(context);
            hub.Offer(Entry("/before"));
            await page.WaitForStateAsync(() => Paths(page).Length == 1);

            await page.Find("[data-action=pause]").ClickAsync(new());
            Assert.Equal("true", page.Find("[data-action=follow]").GetAttribute("aria-pressed"));
            hub.Offer(Entry("/while-paused-1"));
            hub.Offer(Entry("/while-paused-2"));
            await page.WaitForStateAsync(() => page.FindAll("[data-part=waiting]").Count == 1 && page.Find("[data-part=waiting]").TextContent.Contains("2 waiting", StringComparison.Ordinal));

            Assert.Equal(["/before"], Paths(page));
            Assert.Contains("2 waiting", page.Find("[data-part=waiting]").TextContent, StringComparison.Ordinal);
            Assert.Equal("Paused", page.Find("[data-part=state]").TextContent.Trim());

            await page.Find("[data-action=follow]").ClickAsync(new());
            await page.WaitForStateAsync(() => Paths(page).Length == 3);

            Assert.Equal(["/while-paused-2", "/while-paused-1", "/before"], Paths(page));
            Assert.Empty(page.FindAll("[data-part=waiting]"));
        }
    }

    [Fact]
    public async Task AHiddenTabRendersNothingAndShowsWhatWaitedWhenItIsShownAgain()
    {
        var (hub, context, _) = SignIn(Role.Owner);
        await using (context)
        {
            var page = Render(context);
            await page.InvokeAsync(() => page.Instance.PageVisibilityChangedAsync(false));
            var renders = page.Instance.Renders;
            hub.Offer(Entry("/while-hidden"));
            await Task.Delay(Batch * 6, Xunit.TestContext.Current.CancellationToken);

            Assert.Empty(Paths(page));
            Assert.Contains("hidden", page.Find("[data-part=state]").TextContent, StringComparison.Ordinal);

            await page.InvokeAsync(() => page.Instance.PageVisibilityChangedAsync(true));
            await page.WaitForStateAsync(() => Paths(page).Length == 1);

            Assert.Equal(["/while-hidden"], Paths(page));
            Assert.True(page.Instance.Renders > renders);
        }
    }

    [Fact]
    public async Task AFilterReachesTheTailAndClearingItBringsEverythingBack()
    {
        var (hub, context, _) = SignIn(Role.Owner);
        await using (context)
        {
            hub.Offer(Entry("/fine", 200));
            hub.Offer(Entry("/slow-fail", 504, method: "POST"));
            hub.Offer(Entry("/gone", 404));
            var page = Render(context);
            Assert.Equal(3, Paths(page).Length);

            await page.Find("input[data-field=status]").InputAsync(new() { Value = "5xx" });
            await page.WaitForStateAsync(() => Paths(page).Length == 1);
            Assert.Equal(["/slow-fail"], Paths(page));

            // A new request that doesn't match never reaches the page; one that does, does.
            hub.Offer(Entry("/another-ok", 200));
            hub.Offer(Entry("/another-bad", 500));
            await page.WaitForStateAsync(() => Paths(page).Length == 2);
            Assert.Equal(["/another-bad", "/slow-fail"], Paths(page));

            await page.Find("[data-action=clear-filters]").ClickAsync(new());
            await page.WaitForStateAsync(() => Paths(page).Length == 5);
            Assert.Empty(page.FindAll("[data-part=skipped]"));
        }
    }

    [Fact]
    public async Task TheMethodClientAndPathFiltersNarrowTheList()
    {
        var (hub, context, _) = SignIn(Role.Owner);
        await using (context)
        {
            hub.Offer(Entry("/get"));
            hub.Offer(Entry("/post", method: "POST", client: "10.1.2.3"));
            var page = Render(context);

            var method = page.FindComponents<MudSelect<string>>().Single(select => select.Instance.Label == "Method");
            await method.InvokeAsync(() => method.Instance.ValueChanged.InvokeAsync("POST"));
            await page.WaitForStateAsync(() => Paths(page).Length == 1);
            Assert.Equal(["/post"], Paths(page));

            await page.Find("[data-action=clear-filters]").ClickAsync(new());
            await page.WaitForStateAsync(() => Paths(page).Length == 2);
            await page.Find("input[data-field=client]").InputAsync(new() { Value = "10.1." });
            await page.WaitForStateAsync(() => Paths(page).SequenceEqual(["/post"]));

            await page.Find("[data-action=clear-filters]").ClickAsync(new());
            await page.WaitForStateAsync(() => Paths(page).Length == 2);
            await page.Find("input[data-field=path]").InputAsync(new() { Value = "GE" });
            await page.WaitForStateAsync(() => Paths(page).SequenceEqual(["/get"]));
        }
    }

    [Fact]
    public async Task AFilterTextOverTheCapOpensNoTailAndTheFieldStopsAtTheCap()
    {
        var (hub, context, _) = SignIn(Role.Owner);
        await using (context)
        {
            hub.Offer(Entry("/get"));
            var page = Render(context);
            await page.WaitForStateAsync(() => Paths(page).Length == 1);
            Assert.Equal(LogTailFilter.MaxTextLength.ToString(System.Globalization.CultureInfo.InvariantCulture), page.Find("input[data-field=path]").GetAttribute("maxlength"));

            // A client can send what the field would not let a person type; the page takes no subscription for it instead of faulting.
            await page.Find("input[data-field=path]").InputAsync(new() { Value = new string('a', LogTailFilter.MaxTextLength + 1) });
            await page.WaitForStateAsync(() => Paths(page).Length == 0);
            Assert.Equal(0, hub.SubscriberCount);

            await page.Find("input[data-field=path]").InputAsync(new() { Value = "get" });
            await page.WaitForStateAsync(() => Paths(page).SequenceEqual(["/get"]));
        }
    }

    [Fact]
    public async Task TheHostPickerOffersTheConfiguredHostsAndRequestsNoHostOwnedCanBeSelected()
    {
        var (hub, context, config) = SignIn(Role.Owner);
        await using (context)
        {
            config.Document = new ConfigDocument(Hosts:
            [
                new HostConfig(HostId.New(), ["shop.test", "www.shop.test"]),
                new HostConfig(HostId.New(), ["api.test"]),
            ]);
            hub.Offer(Entry("/scan", 404, host: "nobody.test"));
            var page = Render(context);

            var host = page.FindComponents<MudSelect<string>>().Single(select => select.Instance.Label == "Host");
            var offered = host.FindComponents<MudSelectItem<string>>().Select(item => item.Instance.Value).ToList();
            Assert.Equal([null, "(no host matched)", "api.test", "shop.test"], offered);

            // Nothing is configured to own the scanner's request here, so it is the one "no host matched" shows and a named host hides.
            await host.InvokeAsync(() => host.Instance.ValueChanged.InvokeAsync("(no host matched)"));
            await page.WaitForStateAsync(() => Paths(page).Length == 1);
            await host.InvokeAsync(() => host.Instance.ValueChanged.InvokeAsync("api.test"));
            await page.WaitForStateAsync(() => Paths(page).Length == 0);
            Assert.Contains("No request matches yet", page.Markup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ABadStatusIsFlaggedAndOpensNoNewSubscription()
    {
        var (hub, context, _) = SignIn(Role.Owner);
        await using (context)
        {
            hub.Offer(Entry("/a"));
            var page = Render(context);

            await page.Find("input[data-field=status]").InputAsync(new() { Value = "teapot" });
            await page.WaitForStateAsync(() => page.FindAll(".mud-input-error").Count > 0);

            Assert.Contains("A status such as 404", page.Markup, StringComparison.Ordinal);
            Assert.Equal(0, hub.SubscriberCount);
        }
    }

    [Fact]
    public async Task ARowOpensTheEntryWithTheRequestIdAndALinkToTheSimulatorPrefilledWithTheRequest()
    {
        var (hub, context, _) = SignIn(Role.Owner);
        await using (context)
        {
            hub.Offer(Entry("/shop/cart", 201, host: "app.test:8443", method: "POST") with { Query = "?item=7&token=hunter2", UserAgent = "agent/1.0", ResponseBytes = 2_048, RequestBytes = 1_234_567 });
            var drawers = context.Render<DrawerHost>();
            var page = Render(context);

            await page.Find("[data-seq]").ClickAsync(new());
            await drawers.WaitForStateAsync(() => drawers.FindAll("[data-detail]").Count == 1);

            Assert.Equal("rid-/shop/cart", drawers.Find("[data-secret=request-id] .pdk-copy-text").TextContent);
            Assert.Single(drawers.FindAll("[data-secret=request-id] [data-action=copy]"));
            var shown = drawers.Find("[data-detail]").TextContent;
            Assert.Contains("POST /shop/cart?item=7&token=REDACTED HTTP/2", shown, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", drawers.Markup, StringComparison.Ordinal);
            Assert.Contains("agent/1.0", shown, StringComparison.Ordinal);
            Assert.Contains("Response size2,048 bytes", shown, StringComparison.Ordinal);
            Assert.Contains("Request size1,234,567 bytes", shown, StringComparison.Ordinal);
            var link = drawers.Find("[data-action=simulate]").GetAttribute("href")!;
            Assert.StartsWith("/simulator?", link, StringComparison.Ordinal);
            Assert.Contains("url=" + Uri.EscapeDataString("https://app.test:8443/shop/cart?item=7&token=REDACTED"), link, StringComparison.Ordinal);
            Assert.Contains("clientIp=198.51.100.7", link, StringComparison.Ordinal);
            Assert.Contains("method=POST", link, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ARoleThatCantReadTheLogsGetsNoTailAndNoSubscription()
    {
        var (hub, context, _) = SignIn(Role.Operator);
        await using (context)
        {
            hub.Offer(Entry("/secret"));
            var page = Render(context);

            Assert.Contains("Your role can't read the logs", page.Markup, StringComparison.Ordinal);
            Assert.Empty(page.FindAll("[data-seq]"));
            Assert.Equal(0, hub.SubscriberCount);
        }
    }

    [Fact]
    public async Task LeavingThePageStopsTheSubscription()
    {
        var (hub, context, _) = SignIn(Role.Owner);
        await using (context)
        {
            var page = Render(context);
            Assert.Equal(1, hub.SubscriberCount);

            await page.Instance.DisposeAsync();

            Assert.Equal(0, hub.SubscriberCount);
        }
    }

    [Fact]
    public async Task WhenTheProcessServesAsManyTailsAsItAllowsThePageSaysSo()
    {
        var (hub, context, _) = SignIn(Role.Owner);
        await using (context)
        {
            var held = Enumerable.Range(0, AccessLogTail.MaxSubscribers).Select(_ => hub.Subscribe(AccessLogTailFilter.All, 0)!).ToList();
            var page = Render(context);

            Assert.Contains("Too many live tails are open", page.Find("[data-alert=busy]").TextContent, StringComparison.Ordinal);
            held[0].Dispose();
            await page.Find("[data-alert=busy] button").ClickAsync(new());
            Assert.Empty(page.FindAll("[data-alert=busy]"));
            held.ForEach(subscription => subscription.Dispose());
        }
    }

    /// <summary>
    /// A bench-level rate through the hub while the page is open: thousands of entries a second for a couple of seconds. The page
    /// renders a few times a second whatever the rate is, keeps at most <see cref="LiveTail.MaxRows"/> rows, and everything it
    /// didn't show is counted, so the arithmetic closes.
    /// </summary>
    [Fact]
    public async Task ThousandsOfRequestsASecondStayBoundedAndRenderInBatches()
    {
        var (hub, context, _) = SignIn(Role.Owner);
        await using (context)
        {
            var page = Render(context);
            var rendersBefore = page.Instance.Renders;
            const int PerSecond = 20_000;
            const int Seconds = 2;
            var clock = Stopwatch.StartNew();
            var sent = 0;
            while (clock.Elapsed < TimeSpan.FromSeconds(Seconds))
            {
                var due = (int)(clock.Elapsed.TotalSeconds * PerSecond);
                for (; sent < due; sent++)
                {
                    hub.Offer(Entry("/load/" + sent));
                }

                await Task.Delay(1, Xunit.TestContext.Current.CancellationToken);
            }

            await page.WaitForStateAsync(() => Paths(page).Length > 0 && Paths(page)[0] == "/load/" + (sent - 1), TimeSpan.FromSeconds(10));
            await Rendered(page, rendersBefore);

            var renders = page.Instance.Renders - rendersBefore;
            var maxRenders = (int)(clock.Elapsed.TotalSeconds / Batch.TotalSeconds) + 5;
            Assert.True(renders <= maxRenders, $"{renders} renders for {sent} requests in {clock.Elapsed.TotalSeconds:0.0} s; at most {maxRenders} are one a batch");
            Assert.True(sent > 10_000, $"only {sent} requests were pushed");
            Assert.Equal(LiveTail.MaxRows, page.FindAll("[data-seq]").Count);
            // What the table doesn't show is counted, and what it shows is the newest, in order.
            var skippedText = page.Find("[data-part=skipped]").TextContent;
            var skipped = long.Parse(new string(skippedText.TakeWhile(c => char.IsDigit(c) || c == ',').Where(char.IsDigit).ToArray()), System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(skipped, 1, sent - LiveTail.MaxRows);
            var sequences = page.FindAll("[data-seq]").Select(row => long.Parse(row.GetAttribute("data-seq")!, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            Assert.Equal(sent, sequences[0]);
            Assert.Equal(sequences.OrderByDescending(sequence => sequence), sequences);
        }
    }

    [Fact]
    public async Task ALinkFromTheAnalyticsStartsTheTailFilteredToAPath()
    {
        var (hub, context, _) = SignIn(Role.Owner);
        await using (context)
        {
            hub.Offer(Entry("/cart/1"));
            hub.Offer(Entry("/about"));
            hub.Offer(Entry("/cart/2"));
            context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/logs/live?path=%2Fcart");

            var page = Render(context);
            await page.WaitForStateAsync(() => Paths(page).Length == 2);

            Assert.Equal(["/cart/2", "/cart/1"], Paths(page));
            Assert.Equal("/cart", page.Find("input[data-field=path]").GetAttribute("value"));
            Assert.Single(page.FindAll("[data-action=clear-filters]"));
        }
    }

    [Fact]
    public async Task ALinkNamesAHostThePickerOffersAndOneItDoesNotIsIgnoredAndAnOverlongPathIsCut()
    {
        var (_, context, config) = SignIn(Role.Owner);
        await using (context)
        {
            config.Document = new ConfigDocument(Hosts: [new HostConfig(HostId.New(), ["shop.test"])]);
            context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/logs/live?host=SHOP.test&path=" + new string('a', 400));

            var page = Render(context);
            await page.WaitForStateAsync(() => page.FindAll("input[data-field=path]").Count == 1);

            Assert.Equal("shop.test", page.Find("input[data-field=host]").GetAttribute("value"));
            Assert.Equal(LogTailFilter.MaxTextLength, page.Find("input[data-field=path]").GetAttribute("value")!.Length);
        }

        var (_, other, otherConfig) = SignIn(Role.Owner);
        await using (other)
        {
            otherConfig.Document = new ConfigDocument(Hosts: [new HostConfig(HostId.New(), ["shop.test"])]);
            other.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/logs/live?host=nobody.test");

            var page = Render(other);
            await page.WaitForStateAsync(() => page.FindAll("input[data-field=path]").Count == 1);

            Assert.Equal("Any host", page.Find("input[data-field=host]").GetAttribute("value"));
        }
    }

    /// <summary>The page, counting its own renders after the renderer has applied them (bUnit's count includes every child).</summary>
    private sealed class CountedLiveTail : LiveTail
    {
        public int Renders { get; private set; }

        protected override Task OnAfterRenderAsync(bool firstRender)
        {
            Renders++;
            return base.OnAfterRenderAsync(firstRender);
        }
    }
}
