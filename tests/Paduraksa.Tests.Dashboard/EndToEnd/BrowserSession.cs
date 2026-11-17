using Microsoft.Playwright;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// What every browser test starts with: a Chromium (or a skip), a <see cref="DashboardHost"/>, and a context with one page open on it that
/// trusts the admin's certificate and collects the console's problems. Disposing it tears them down in the reverse order.
/// </summary>
internal sealed class BrowserSession : IAsyncDisposable
{
    private readonly BrowserNewContextOptions _options;

    private BrowserSession(IPlaywright playwright, IBrowser browser, DashboardHost host, BrowserNewContextOptions options, IBrowserContext context, IPage page, List<string> problems)
    {
        Playwright = playwright;
        _options = options;
        Browser = browser;
        Host = host;
        Context = context;
        Page = page;
        Problems = problems;
    }

    public IPlaywright Playwright { get; }

    public IBrowser Browser { get; }

    public DashboardHost Host { get; }

    public IBrowserContext Context { get; }

    public IPage Page { get; }

    /// <summary>The console problems the page has logged so far (see <see cref="PlaywrightBrowser.CollectProblems"/>).</summary>
    public List<string> Problems { get; }

    /// <summary>
    /// Starts the session. <paramref name="options"/> is the context's (a desktop one in dark mode when null); the base address and
    /// the certificate trust are set here. <paramref name="expectedProblems"/> are console errors the test asks for itself.
    /// </summary>
    public static async Task<BrowserSession> StartAsync(BrowserNewContextOptions? options = null, params string[] expectedProblems)
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        var host = await DashboardHost.StartAsync(TestContext.Current.CancellationToken);
        options ??= Desktop();
        options.BaseURL = host.Admin.ToString();
        options.IgnoreHTTPSErrors = true;
        var context = await browser.NewContextAsync(options);
        var page = await context.NewPageAsync();
        return new BrowserSession(playwright, browser, host, options, context, page, PlaywrightBrowser.CollectProblems(page, expectedProblems));
    }

    /// <summary>Context options for a desktop window, in dark mode unless <paramref name="colorScheme"/> says otherwise (null leaves the browser's default).</summary>
    public static BrowserNewContextOptions Desktop(int width = 1366, int height = 900, ColorScheme? colorScheme = ColorScheme.Dark, bool? acceptDownloads = null) => new()
    {
        ViewportSize = new ViewportSize { Width = width, Height = height },
        ColorScheme = colorScheme,
        AcceptDownloads = acceptDownloads,
    };

    /// <summary>Another browser of its own (cookies and all), set up like the first: a second person signing in.</summary>
    public Task<IBrowserContext> NewContextAsync() => Browser.NewContextAsync(_options);

    public void Deconstruct(out DashboardHost host, out IPage page)
    {
        host = Host;
        page = Page;
    }

    public void Deconstruct(out DashboardHost host, out IPage page, out List<string> problems)
    {
        Deconstruct(out host, out page);
        problems = Problems;
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await Host.DisposeAsync();
        await Browser.DisposeAsync();
        Playwright.Dispose();
    }
}
