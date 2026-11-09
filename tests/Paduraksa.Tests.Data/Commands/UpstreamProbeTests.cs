using System.Net;
using System.Net.Sockets;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Probes;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Commands;

/// <summary>The first-run wizard's upstream check: a bare TCP connection, reachable or not, for a caller who may add a host.</summary>
public sealed class UpstreamProbeTests
{
    [Fact]
    public async Task AListeningPortIsReachable()
    {
        await using ConfigServiceHarness h = await ConfigServiceHarness.CreateAsync();
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        UpstreamProbeResult result = await new UpstreamProbe(h.Service).CheckAsync($"http://127.0.0.1:{port}/app", ConfigServiceHarness.Admin, Ct);

        Assert.Equal(UpstreamProbeOutcome.Reachable, result.Outcome);
        Assert.True(result.Reachable);
    }

    [Fact]
    public async Task AClosedPortIsUnreachable()
    {
        await using ConfigServiceHarness h = await ConfigServiceHarness.CreateAsync();
        int port;
        using (TcpListener listener = new(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        UpstreamProbeResult result = await new UpstreamProbe(h.Service).CheckAsync($"http://127.0.0.1:{port}", ConfigServiceHarness.Admin, Ct);

        Assert.Equal((UpstreamProbeOutcome.Unreachable, null), (result.Outcome, result.Message));
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.20:8080")]
    [InlineData("ftp://192.168.1.20")]
    [InlineData("http://user:secret@192.168.1.20")]
    public async Task AnythingButAnHttpUrlIsNotTried(string address)
    {
        await using ConfigServiceHarness h = await ConfigServiceHarness.CreateAsync();

        UpstreamProbeResult result = await new UpstreamProbe(h.Service).CheckAsync(address, ConfigServiceHarness.Admin, Ct);

        Assert.Equal(UpstreamProbeOutcome.Invalid, result.Outcome);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task ACallerWhoMayNotAddHostsIsRefusedAndAudited()
    {
        await using ConfigServiceHarness h = await ConfigServiceHarness.CreateAsync();

        UpstreamProbeResult result = await new UpstreamProbe(h.Service, new Nobody()).CheckAsync("http://127.0.0.1:1", ConfigServiceHarness.Admin, Ct);

        Assert.Equal(UpstreamProbeOutcome.Denied, result.Outcome);
        Assert.Equal(1L, await h.Db.ScalarAsync("SELECT count(*) FROM AuditEvents WHERE Action = 'upstream.probe' AND Outcome = 'denied'"));
    }

    private sealed class Nobody : IPermissionEvaluator
    {
        public ValueTask<IReadOnlySet<Permission>> GetPermissionsAsync(Actor actor, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlySet<Permission>>(new HashSet<Permission>());

        public ValueTask<bool> IsAllowedAsync(CommandOrigin origin, Permission permission, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }
}
