using System.Net;
using System.Net.Sockets;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Paduraksa.Server.Configuration;
using Paduraksa.Server.Hosting;
using Paduraksa.Server.Placeholders;

namespace Paduraksa.Tests.Integration.Hosting;

/// <summary>Spike S5: the real core + public + admin hosts in one process, on ephemeral ports.</summary>
public sealed class DualHostTests
{
    private const string NoHostConfigured = "no host configured\n";

    [Fact]
    public async Task AdminServesHealthEndpointsAndPlaceholderPage()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PaduraksaHost host = TestHost.Create();
        await host.StartAsync(ct);
        using HttpClient client = TestHost.CreateClient(host);
        Uri admin = TestHost.Endpoint(host, HostRole.Admin, "https");

        using HttpResponseMessage healthz = await client.GetAsync(new Uri(admin, "/healthz"), ct);
        Assert.Equal(HttpStatusCode.OK, healthz.StatusCode);
        Assert.Equal("ok\n", await healthz.Content.ReadAsStringAsync(ct));

        using HttpResponseMessage readyz = await client.GetAsync(new Uri(admin, "/readyz"), ct);
        Assert.Equal(HttpStatusCode.OK, readyz.StatusCode);
        Assert.Equal("ready\n", await readyz.Content.ReadAsStringAsync(ct));

        using HttpRequestMessage head = new(HttpMethod.Head, new Uri(admin, "/healthz"));
        using HttpResponseMessage headResponse = await client.SendAsync(head, ct);
        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);

        using HttpResponseMessage page = await client.GetAsync(admin, ct);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("http", "/healthz")]
    [InlineData("http", "/readyz")]
    [InlineData("http", "/")]
    [InlineData("https", "/healthz")]
    [InlineData("https", "/readyz")]
    [InlineData("https", "/")]
    public async Task AdminEndpointsAreNotRoutedOnPublicPorts(string scheme, string path)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PaduraksaHost host = TestHost.Create();
        await host.StartAsync(ct);
        using HttpClient client = TestHost.CreateClient(host);

        using HttpResponseMessage response = await client.GetAsync(new Uri(TestHost.Endpoint(host, HostRole.Public, scheme), path), ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(NoHostConfigured, await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task PublicHttpsNegotiatesHttp2()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PaduraksaHost host = TestHost.Create();
        await host.StartAsync(ct);
        using HttpClient client = TestHost.CreateClient(host);

        using HttpRequestMessage request = new(HttpMethod.Get, TestHost.Endpoint(host, HostRole.Public, "https"))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using HttpResponseMessage response = await client.SendAsync(request, ct);

        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SharedSingletonsAreTheSameInstancesInEveryHost()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        EventLog events = new();
        await using PaduraksaHost host = TestHost.Create(events);
        await host.StartAsync(ct);

        IServiceProvider[] containers = [host.GetServices(HostRole.Core), host.GetServices(HostRole.Public), host.GetServices(HostRole.Admin)];
        AssertSameEverywhere(host.Shared.SnapshotStore, containers);
        AssertSameEverywhere(host.Shared.Health, containers);
        AssertSameEverywhere(host.Shared.TimeProvider, containers);
        AssertSameEverywhere(host.Shared.ServerCertificate, containers);
        AssertSameEverywhere(containers[0].GetRequiredService<DisposalProbe>(), containers);

        // Every way of asking for the options yields the one validated instance (no unbound defaults).
        Assert.All(containers, c =>
        {
            Assert.Same(host.Options, c.GetRequiredService<PaduraksaOptions>());
            Assert.Same(host.Options, c.GetRequiredService<IOptions<PaduraksaOptions>>().Value);
            Assert.Same(host.Options, c.GetRequiredService<IOptionsMonitor<PaduraksaOptions>>().CurrentValue);
            using IServiceScope scope = c.CreateScope();
            Assert.Same(host.Options, scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<PaduraksaOptions>>().Value);

            // The factory too: the default one would build an unvalidated copy straight from configuration.
            IOptionsFactory<PaduraksaOptions> factory = c.GetRequiredService<IOptionsFactory<PaduraksaOptions>>();
            Assert.Same(host.Options, factory.Create(Options.DefaultName));
            Assert.Throws<InvalidOperationException>(() => factory.Create("named"));
            Assert.Throws<InvalidOperationException>(() => c.GetRequiredService<IOptionsMonitor<PaduraksaOptions>>().Get("named"));
        });

        // Each host gets its own role-tagged view of the one logger factory.
        ILoggerFactory[] loggerFactories = [.. containers.Select(static c => c.GetRequiredService<ILoggerFactory>())];
        Assert.All(loggerFactories, f => Assert.IsType<RoleLoggerFactory>(f));
        Assert.Equal(3, loggerFactories.Distinct().Count());

        // The core host published the snapshot before the web apps started; everyone sees generation 1.
        Assert.Equal(1, host.Shared.SnapshotStore.Current?.Generation);
    }

    [Fact]
    public async Task GracefulShutdownStopsPublicThenAdminThenCoreAndDisposesSharedOnce()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        EventLog events = new();
        PaduraksaHost host = TestHost.Create(events);
        DisposalProbe probe = host.GetServices(HostRole.Admin).GetRequiredService<DisposalProbe>();

        await host.StartAsync(ct);
        int publicHttpPort = TestHost.Endpoint(host, HostRole.Public, "http").Port;
        Assert.Equal(HostState.Running, host.State);
        Assert.True(host.Shared.Health.IsReady);

        await host.StopAsync(ct);
        Assert.Equal(HostState.Stopped, host.State);
        Assert.False(host.Shared.Health.IsReady);
        Assert.Equal(0, probe.DisposeCount); // Stopped, not yet disposed.
        await AssertRefusesConnectionsAsync(publicHttpPort, ct);

        await host.DisposeAsync();
        await host.DisposeAsync(); // Idempotent.

        Assert.Equal(1, probe.DisposeCount);
        List<string> log = [.. events.Events];
        Assert.True(log.IndexOf("Core:start") < log.IndexOf("Public:start"), string.Join(", ", log));
        Assert.True(log.IndexOf("Core:start") < log.IndexOf("Admin:start"), string.Join(", ", log));
        Assert.Equal(["Public:stop", "Admin:stop", "Core:stop", "shared:disposed"], log.Skip(3));
    }

    [Fact]
    public async Task AdminReportsNotReadyWhilePublicDrains()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TaskCompletionSource drainStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseDrain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using PaduraksaHost host = TestHost.Create(configureServices: (role, services) =>
        {
            if (role == HostRole.Public)
            {
                services.AddSingleton<IHostedService>(new LifecycleProbe(new EventLog(), role)
                {
                    OnStop = async stopToken =>
                    {
                        drainStarted.SetResult();
                        await releaseDrain.Task.WaitAsync(stopToken);
                    },
                });
            }
        });
        await host.StartAsync(ct);
        using HttpClient client = TestHost.CreateClient(host);
        Uri readyz = new(TestHost.Endpoint(host, HostRole.Admin, "https"), "/readyz");

        Task stopping = host.StopAsync(ct);
        await drainStarted.Task.WaitAsync(ct);

        using HttpResponseMessage response = await client.GetAsync(readyz, ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("not ready\n", await response.Content.ReadAsStringAsync(ct));

        releaseDrain.SetResult();
        await stopping;
        Assert.Equal(HostState.Stopped, host.State);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Http")]
    public async Task StartupFailureOfOneAppStopsEverythingElse(string occupiedPort)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using TcpListener blocker = BlockPort(occupiedPort);
        EventLog events = new();
        PaduraksaHost host = TestHost.Create(events, PortSetting(occupiedPort, blocker));
        DisposalProbe probe = host.GetServices(HostRole.Core).GetRequiredService<DisposalProbe>();

        await Assert.ThrowsAnyAsync<IOException>(() => host.StartAsync(ct));
        Assert.Equal(HostState.Faulted, host.State);
        Assert.False(host.Shared.Health.IsReady);

        // Whatever started was stopped again: every probe that logged a start also logged a stop.
        IReadOnlyList<string> log = events.Events;
        Assert.Contains("Core:start", log);
        foreach (HostRole role in Enum.GetValues<HostRole>())
        {
            if (log.Contains($"{role}:start"))
            {
                Assert.Contains($"{role}:stop", log);
            }
        }

        Assert.Equal("Core:stop", log[^1]);

        await host.DisposeAsync();
        Assert.Equal(1, probe.DisposeCount);
    }

    [Fact]
    public async Task AHostStoppingOnItsOwnRequestsProcessShutdown()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PaduraksaHost host = TestHost.Create();
        await host.StartAsync(ct);
        Assert.False(host.ShutdownRequested.IsCompleted);

        host.Admin.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        ShutdownRequest request = await host.ShutdownRequested.WaitAsync(ct);
        Assert.Equal(new ShutdownRequest(ShutdownReason.HostStopped, HostRole.Admin), request);

        await host.StopAsync(ct);
        Assert.Equal(HostState.Stopped, host.State);
    }

    [Fact]
    public async Task RunAsyncStopsGracefullyWhenCancelled()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PaduraksaHost host = TestHost.Create();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task<int> run = host.RunAsync(stop.Token);
        await WaitUntilAsync(() => host.State == HostState.Running, ct);
        await stop.CancelAsync();

        Assert.Equal(ExitCodes.Success, await run.WaitAsync(ct));
        Assert.Equal(HostState.Stopped, host.State);
    }

    [Fact]
    public async Task RunAsyncReturnsFailureWhenAHostStopsOnItsOwn()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PaduraksaHost host = TestHost.Create();

        Task<int> run = host.RunAsync(ct);
        await WaitUntilAsync(() => host.State == HostState.Running, ct);
        host.Public.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        Assert.Equal(ExitCodes.Failure, await run.WaitAsync(ct));
        Assert.Equal(HostState.Stopped, host.State);
    }

    [Fact]
    public async Task RunAsyncReturnsStartupFailedWhenAListenerCannotBind()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using TcpListener blocker = BlockPort("Admin");
        await using PaduraksaHost host = TestHost.Create(settings: PortSetting("Admin", blocker));

        Assert.Equal(ExitCodes.StartupFailed, await host.RunAsync(ct).WaitAsync(ct));
        Assert.Equal(HostState.Faulted, host.State);
    }

    [Fact]
    public async Task RunAsyncReturnsStartupFailedWhenAHostStopsItselfDuringStartup()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PaduraksaHost host = TestHost.Create(configureServices: static (role, services) =>
        {
            if (role == HostRole.Core)
            {
                services.AddHostedService<StopsHostOnStart>();
            }
        });

        Assert.Equal(ExitCodes.StartupFailed, await host.RunAsync(ct).WaitAsync(ct));
        Assert.Equal(HostState.Faulted, host.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsyncNeverReturnsSuccessWhenACoreBackgroundServiceCrashes(bool afterYield)
    {
        // Since .NET 10 a BackgroundService's ExecuteAsync always runs in the background, so its crash races the web
        // apps' start: either a startup failure (2) or a host stopping on its own right after start (1). Both are
        // failures; it must never be 0 (the reviewed bug).
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PaduraksaHost host = TestHost.Create(configureServices: (role, services) =>
        {
            if (role == HostRole.Core)
            {
                services.AddSingleton<IHostedService>(new CrashingService(afterYield));
            }
        });

        Assert.Contains(await host.RunAsync(ct).WaitAsync(ct), new[] { ExitCodes.Failure, ExitCodes.StartupFailed });
    }

    [Fact]
    public async Task InFlightPublicRequestCompletesDuringGracefulStop()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SlowEndpoint slow = new();
        await using PaduraksaHost host = TestHost.Create(configureServices: (role, services) =>
        {
            if (role == HostRole.Public)
            {
                services.AddSingleton<IStartupFilter>(slow);
            }
        });
        await host.StartAsync(ct);
        using HttpClient client = TestHost.CreateClient(host);

        Task<HttpResponseMessage> inFlight = client.GetAsync(new Uri(TestHost.Endpoint(host, HostRole.Public, "https"), SlowEndpoint.Path), ct);
        await slow.Entered.Task.WaitAsync(ct);

        Task stopping = host.StopAsync(ct);
        // Wait until the public app is actually draining, then let the request finish.
        IHostApplicationLifetime publicLifetime = host.Public.Services.GetRequiredService<IHostApplicationLifetime>();
        await WaitUntilAsync(() => publicLifetime.ApplicationStopping.IsCancellationRequested, ct);
        Assert.False(stopping.IsCompleted);
        slow.Release.SetResult();

        using HttpResponseMessage response = await inFlight.WaitAsync(ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("drained\n", await response.Content.ReadAsStringAsync(ct));
        await stopping.WaitAsync(ct);
        Assert.Equal(HostState.Stopped, host.State);
    }

    [Fact]
    public async Task HostingUrlKeysCannotMoveOrAddListeners()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Dictionary<string, string?> hostile = new()
        {
            ["urls"] = "http://127.0.0.1:0",
            ["http_ports"] = "0",
            ["https_ports"] = "0",
            ["preferHostingUrls"] = "true",
            ["Kestrel:Endpoints:Extra:Url"] = "http://127.0.0.1:0",
        };
        await using PaduraksaHost host = TestHost.Create(settings: hostile);
        await host.StartAsync(ct);

        IReadOnlyList<string> publicAddresses = host.GetListenAddresses(HostRole.Public);
        Assert.Equal(2, publicAddresses.Count);
        Assert.Single(publicAddresses, static a => a.StartsWith("http://[::]:", StringComparison.Ordinal));
        Assert.Single(publicAddresses, static a => a.StartsWith("https://[::]:", StringComparison.Ordinal));

        string admin = Assert.Single(host.GetListenAddresses(HostRole.Admin));
        Assert.StartsWith("https://127.0.0.1:", admin, StringComparison.Ordinal);
    }

    [Fact]
    public void EphemeralPortsAreRejectedUnlessExplicitlyAllowed()
    {
        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(
            () => TestHost.Create(allowEphemeralPorts: false));
        Assert.Equal(3, ex.Failures.Count());
    }

    [Theory]
    [InlineData("Paduraksa:Listen:Http", "70000")]
    [InlineData("Paduraksa:Listen:Https", "-1")]
    [InlineData("Paduraksa:Listen:Admin", "eighty")]
    [InlineData("Paduraksa:Listen:AdminBind", "not-an-ip")]
    [InlineData("Paduraksa:DataDirectory", " ")]
    [InlineData("Paduraksa:ShutdownTimeout", "00:00:00")]
    public void InvalidOptionsFailBeforeAnythingIsBuilt(string key, string value)
    {
        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(
            () => TestHost.Create(settings: new Dictionary<string, string?> { [key] = value }));
        Assert.NotEmpty(ex.Failures);
    }

    [Theory]
    [InlineData("./data", "data")]
    [InlineData("state/paduraksa", "state/paduraksa")]
    [InlineData("../shared-data", "../shared-data")]
    public async Task RelativeDataDirectoryResolvesAgainstTheContentRoot(string configured, string relativeToContentRoot)
    {
        // A content root that differs from the working directory, as under systemd with WorkingDirectory=/.
        using TempDirectory contentRoot = new();
        Assert.NotEqual(Path.GetFullPath(Environment.CurrentDirectory), contentRoot.Path);

        await using PaduraksaHost host = TestHost.Create(
            settings: new Dictionary<string, string?> { ["Paduraksa:DataDirectory"] = configured },
            contentRootPath: contentRoot.Path);

        Assert.Equal(Path.GetFullPath(Path.Combine(contentRoot.Path, relativeToContentRoot)), host.Options.DataDirectory);
    }

    [Fact]
    public async Task AbsoluteDataDirectoryIsUsedAsGiven()
    {
        using TempDirectory contentRoot = new();
        string absolute = Path.Combine(Path.GetTempPath(), "paduraksa-absolute-data");
        await using PaduraksaHost host = TestHost.Create(
            settings: new Dictionary<string, string?> { ["Paduraksa:DataDirectory"] = absolute },
            contentRootPath: contentRoot.Path);

        Assert.Equal(absolute, host.Options.DataDirectory);
    }

    [Fact]
    public void ListenPortsMustBeDistinct()
    {
        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(() => TestHost.Create(settings: new Dictionary<string, string?>
        {
            ["Paduraksa:Listen:Http"] = "8080",
            ["Paduraksa:Listen:Admin"] = "8080",
        }));
        Assert.Contains(ex.Failures, f => f.Contains("distinct", StringComparison.Ordinal));
    }

    /// <summary>
    /// Occupies a port the given listener will try to bind. Admin binds 127.0.0.1; the public listeners bind
    /// [::] dual-stack (or 0.0.0.0 without IPv6), and both fail when the IPv4 wildcard port is taken.
    /// </summary>
    private static TcpListener BlockPort(string listener)
    {
        TcpListener blocker = new(listener == "Admin" ? IPAddress.Loopback : IPAddress.Any, 0);
        blocker.Start();
        return blocker;
    }

    private static Dictionary<string, string?> PortSetting(string listener, TcpListener blocker) => new()
    {
        [$"Paduraksa:Listen:{listener}"] = ((IPEndPoint)blocker.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static void AssertSameEverywhere<T>(T expected, IServiceProvider[] containers)
        where T : class
    {
        Assert.All(containers, c => Assert.Same(expected, c.GetRequiredService<T>()));
    }

    private static async Task AssertRefusesConnectionsAsync(int port, CancellationToken ct)
    {
        using TcpClient client = new();
        await Assert.ThrowsAnyAsync<SocketException>(async () => await client.ConnectAsync(IPAddress.Loopback, port, ct));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
