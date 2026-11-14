using Microsoft.Playwright;

namespace Paduraksa.Bench.Soak;

/// <summary>
/// Two dashboard tabs in a headless Chromium (the system's, as the dashboard's own end-to-end tests use) held open for the whole run: the overview and Analytics. Each is a
/// Blazor circuit that polls the server on a timer, so they are the admin load a person leaves running. Every half minute each tab is checked to still have a live circuit.
/// </summary>
internal sealed class BrowserTabs(SoakOptions options, Recorder recorder, Events events) : IAsyncDisposable
{
    private readonly List<string> _problems = [];
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _overview;
    private IPage? _analytics;
    private long _checks;
    private long _stale;
    private long _reloads;

    public bool Running => _browser is not null;

    public string Mode { get; private set; } = "off";

    public IReadOnlyList<string> Problems => _problems;

    public long Checks => Interlocked.Read(ref _checks);

    public long Stale => Interlocked.Read(ref _stale);

    public long Reloads => Interlocked.Read(ref _reloads);

    public async Task<bool> StartAsync(CancellationToken ct)
    {
        try
        {
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, ExecutablePath = options.ChromiumPath });
            var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true, ViewportSize = new ViewportSize { Width = 1440, Height = 900 } });
            _overview = await context.NewPageAsync();
            Watch(_overview, "overview");
            await _overview.GotoAsync($"https://127.0.0.1:{options.Admin}/signin");
            await _overview.FillAsync("input[name=username]", Control.OwnerName);
            await _overview.FillAsync("input[name=password]", Control.OwnerSecret);
            await _overview.ClickAsync("button[type=submit]");
            await WaitLiveAsync(_overview);
            _analytics = await context.NewPageAsync();
            Watch(_analytics, "analytics");
            await _analytics.GotoAsync($"https://127.0.0.1:{options.Admin}/analytics");
            await WaitLiveAsync(_analytics);
            Mode = "chromium";
            events.Write("browser.ready", new { tabs = 2, executable = options.ChromiumPath ?? "playwright's" });
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Mode = "unavailable: " + ex.Message.Split('\n')[0];
            events.Write("browser.unavailable", new { reason = Mode });
            await DisposeAsync();
            return false;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_browser is null)
        {
            return;
        }

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await CheckAsync(_overview!, "overview");
                await CheckAsync(_analytics!, "analytics");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>After a restart the circuits are gone: reload both tabs, the way a person returning to them would.</summary>
    public async Task ReloadAsync()
    {
        foreach (var page in new[] { _overview, _analytics })
        {
            if (page is null)
            {
                continue;
            }

            try
            {
                Interlocked.Increment(ref _reloads);
                await page.ReloadAsync();
                await WaitLiveAsync(page);
            }
            catch (PlaywrightException ex)
            {
                Problem("reload", ex.Message.Split('\n')[0]);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_browser is not null)
            {
                await _browser.CloseAsync();
            }
        }
        catch (PlaywrightException)
        {
        }

        _playwright?.Dispose();
        _browser = null;
    }

    private static Task WaitLiveAsync(IPage page) =>
        page.WaitForSelectorAsync(".pdk-shell[data-circuit=live]", new PageWaitForSelectorOptions { Timeout = 30_000 });

    private async Task CheckAsync(IPage page, string name)
    {
        Interlocked.Increment(ref _checks);
        try
        {
            var live = await page.QuerySelectorAsync(".pdk-shell[data-circuit=live]");
            if (live is null)
            {
                Interlocked.Increment(ref _stale);
                if (!recorder.Tolerance.Applies(events.ElapsedSeconds, "admin"))
                {
                    recorder.Fail("browser-circuit", "admin", name, "browser", "the dashboard tab has no live circuit");
                }

                await ReloadOneAsync(page);
            }
        }
        catch (PlaywrightException ex)
        {
            Problem(name, ex.Message.Split('\n')[0]);
        }
    }

    private async Task ReloadOneAsync(IPage page)
    {
        try
        {
            Interlocked.Increment(ref _reloads);
            await page.ReloadAsync();
            await WaitLiveAsync(page);
        }
        catch (PlaywrightException ex)
        {
            Problem("reload", ex.Message.Split('\n')[0]);
        }
    }

    private void Watch(IPage page, string name)
    {
        page.Console += (_, message) =>
        {
            // A policy violation is a bug; so is any console error, except Blazor reporting its own circuit reconnecting, which is what a restart does.
            if (message.Text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase)
                || (message.Type == "error" && !message.Text.Contains("Connection disconnected", StringComparison.Ordinal)
                    && !message.Text.Contains("Failed to load resource", StringComparison.Ordinal)))
            {
                Problem(name, message.Text);
            }
        };
        page.PageError += (_, error) => Problem(name, "page error: " + error);
    }

    /// <summary>The load is over: the server is about to stop, and a tab that loses it is not a finding.</summary>
    public void StopWatching() => _watching = false;

    private volatile bool _watching = true;

    private void Problem(string name, string text)
    {
        if (!_watching || recorder.Tolerance.Applies(events.ElapsedSeconds, "admin"))
        {
            return;
        }

        lock (_problems)
        {
            if (_problems.Count < 50)
            {
                _problems.Add($"{events.ElapsedSeconds:F0}s {name}: {text}");
            }
        }
    }
}
