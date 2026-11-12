using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Admin;

namespace Paduraksa.Tests.Integration.Hosting;

/// <summary>
/// Class fixtures that start one real host for a whole test class (<c>IClassFixture</c>). A host costs about a second to
/// start and stop, so a class whose tests only read, or whose tests each make their own uniquely named resources, shares
/// one. The tests that keep a host of their own are the ones that change global settings, users or the vault, that
/// restart, fail or stop the host, or that count something global: a shared host would make them order-dependent.
/// </summary>
public sealed class SharedHost : IAsyncLifetime
{
    private PaduraksaHost? _host;

    /// <summary>The running host, with ephemeral ports, a loopback admin listener and its own data directory.</summary>
    internal PaduraksaHost Host => _host ?? throw new InvalidOperationException("The shared host has not started.");

    /// <summary>The lifecycle probe's record: the host is built with one, so a test can read what it saw.</summary>
    internal EventLog Events { get; } = new();

    public async ValueTask InitializeAsync()
    {
        _host = TestHost.Create(Events);
        await _host.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(CancellationToken.None);
            await _host.DisposeAsync();
        }
    }
}

/// <summary>
/// A started admin host whose first-run setup is done: <see cref="OwnerCookie"/> is the Owner's session cookie from it.
/// For classes whose tests only need an Owner to sign in as.
/// </summary>
/// <remarks>
/// The Owner is one account for the whole class, and five wrong passwords lock it: the wrong-password attempts of a class's
/// tests add up, so a test that sends one should have a user of its own (<see cref="SignInAsync"/> or a created user) when
/// more than a couple of tests do, and a test that locks an account needs its own host.
/// </remarks>
public sealed class SharedOwnerSession : IAsyncLifetime
{
    private AdminHarness? _harness;
    private string? _ownerCookie;

    internal AdminHarness Harness => _harness ?? throw new InvalidOperationException("The shared admin host has not started.");

    /// <summary>The Owner's session cookie from first-run setup.</summary>
    internal string OwnerCookie => _ownerCookie ?? throw new InvalidOperationException("The shared admin host has not started.");

    /// <summary>A new session for the Owner, for a test that logs out or revokes sessions.</summary>
    internal async Task<string> SignInAsync() => await Harness.SignInAsync(AdminHarness.OwnerName);

    public async ValueTask InitializeAsync()
    {
        _harness = await AdminHarness.StartAsync();
        _ownerCookie = await _harness.SetUpOwnerAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }
}

/// <summary>
/// A started admin host reached through the public listener (<see cref="AdminProxyHarness"/>): its config names the admin
/// host, the Owner is signed in through the proxy, and a test that signs out asks for a session of its own.
/// </summary>
public sealed class SharedAdminProxy : IAsyncLifetime
{
    private AdminProxyHarness? _harness;
    private AdminProxyHarness? _configured;

    internal AdminProxyHarness Harness => _harness ?? throw new InvalidOperationException("The shared admin proxy has not started.");

    /// <summary>
    /// A second host whose admin host carries its own HSTS and <c>Server</c> header settings, for the tests of those two
    /// layers through the proxy: one host serves both of them, and neither starts one of its own.
    /// </summary>
    internal AdminProxyHarness Configured => _configured ?? throw new InvalidOperationException("The shared admin proxy has not started.");

    /// <summary>A session of this test's own, as a <c>Cookie</c> header value, for a test that signs out.</summary>
    internal async Task<string> NewSessionAsync() => await Harness.NewSessionAsync();

    public async ValueTask InitializeAsync()
    {
        _harness = await AdminProxyHarness.StartAsync();
        _configured = await AdminProxyHarness.StartAsync(adminHostSettings: new HostSettings
        {
            Hsts = new HstsSettings { Enabled = true, MaxAge = 600 },
            ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom, Value = "admin-edge/1.0" },
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_configured is not null)
        {
            await _configured.DisposeAsync();
        }

        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }
}

/// <summary>
/// A started admin host whose plain-HTTP loopback fallback listener is on <see cref="Port"/>: that listener treats 0 as
/// "off", so it needs a fixed number, and the shared helper retries the start with a new one if the port is taken.
/// </summary>
public sealed class SharedLoopbackHost : IAsyncLifetime
{
    private AdminHarness? _harness;

    internal AdminHarness Harness => _harness ?? throw new InvalidOperationException("The shared loopback host has not started.");

    /// <summary>The loopback listener's port.</summary>
    internal int Port { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var (host, port) = await TestPorts.StartOnFreePortAsync(port => TestHost.Create(settings: new Dictionary<string, string?>
        {
            ["Paduraksa:Listen:AdminLoopbackHttp"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));
        Port = port;
        _harness = AdminHarness.For(host);
    }

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }
}

/// <summary>
/// A started admin host whose request body limit is 2 KB, so an over-limit body trips it: the hardening tests that check the
/// limit share one instead of starting a host each.
/// </summary>
public sealed class SharedSmallBodyHost : IAsyncLifetime
{
    private AdminHarness? _harness;

    internal AdminHarness Harness => _harness ?? throw new InvalidOperationException("The shared admin host has not started.");

    public async ValueTask InitializeAsync()
    {
        _harness = await AdminHarness.StartAsync(new Dictionary<string, string?> { ["Paduraksa:Admin:MaxRequestBodyBytes"] = "2048" });
        await _harness.SetUpOwnerAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }
}

/// <summary>
/// A started host with real authentication for every role (<see cref="AdminApiHarness"/>): the Owner from first-run setup,
/// a user per role and a token per user. For classes whose tests read the API, or make their own uniquely named resources.
/// </summary>
public sealed class SharedAdminApi : IAsyncLifetime
{
    private AdminApiHarness? _api;

    private Task<byte[]>? _openApi;

    internal AdminApiHarness Api => _api ?? throw new InvalidOperationException("The shared admin API has not started.");

    /// <summary>
    /// The API description, fetched once for every test of the class. The server builds it on the first request (a few hundred operations, cold
    /// code) and serves it from a cache after that, so the first fetch is the slow one (2 s on a quiet 4-core machine) and a loaded runner can take longer than the harness client's
    /// 30 s over it: the fetch gets a client of its own with time to spare, and no test pays for it twice.
    /// </summary>
    internal Task<byte[]> OpenApiAsync()
    {
        return _openApi ??= FetchAsync();

        async Task<byte[]> FetchAsync()
        {
            using var client = TestHost.CreateAdminClient(Api.Host, static handler => handler.UseCookies = false);
            client.Timeout = TimeSpan.FromMinutes(3);
            return await client.GetByteArrayAsync(new Uri(Api.BaseAddress, "/api/v1/openapi.json"), TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask InitializeAsync() => _api = await AdminApiHarness.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }
    }
}
