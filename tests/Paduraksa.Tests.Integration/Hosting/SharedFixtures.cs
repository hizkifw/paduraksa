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

    public async ValueTask InitializeAsync()
    {
        _host = TestHost.Create();
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
/// A started host with real authentication for every role (<see cref="AdminApiHarness"/>): the Owner from first-run setup,
/// a user per role and a token per user. For classes whose tests read the API, or make their own uniquely named resources.
/// </summary>
public sealed class SharedAdminApi : IAsyncLifetime
{
    private AdminApiHarness? _api;

    internal AdminApiHarness Api => _api ?? throw new InvalidOperationException("The shared admin API has not started.");

    public async ValueTask InitializeAsync() => _api = await AdminApiHarness.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }
    }
}
