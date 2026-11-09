using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Paduraksa.Application.Issuance;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Acme;

public sealed class AcmeHostingTests
{
    /// <summary>A self-signed certificate that is not a CA (public data only: the key was thrown away), for a "root" file that holds no root.</summary>
    private const string NotARootPem = """
        -----BEGIN CERTIFICATE-----
        MIIBezCCASGgAwIBAgIUQBDFnfqWvzBlGatSdFi8QHbpweYwCgYIKoZIzj0EAwIw
        FTETMBEGA1UEAwwKbm90IGEgcm9vdDAgFw0yNjExMDcxODA5MDhaGA8yMTI2MTAx
        NDE4MDkwOFowFTETMBEGA1UEAwwKbm90IGEgcm9vdDBZMBMGByqGSM49AgEGCCqG
        SM49AwEHA0IABPPVH3HiI/KRoTtqvECbqGKmC9CDCvFJnn1xyQDOQBR8ejrxU7kE
        AjS5qnwBhYcshqaU2KkgDVcNYcNi8xK5HAKjTTBLMB0GA1UdDgQWBBQyVlhxioMU
        Ke0vqmZSn6LFUl3BmjAfBgNVHSMEGDAWgBQyVlhxioMUKe0vqmZSn6LFUl3BmjAJ
        BgNVHRMEAjAAMAoGCCqGSM49BAMCA0gAMEUCIQDilBixabToZx6BKkJ6Nbr4T0+u
        tTK5uiABY+B4CcOAXAIgbHcQ93M+T7+6uVc5oju7RIG36wj0pIBKk0q3XSqmdws=
        -----END CERTIFICATE-----
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSchedulerRunsWithTheProcessAndStopsWithItBeforeThePublicAppDrains()
    {
        bool completedWhenPublicStopping = false;
        await using PaduraksaHost host = TestHost.Create(configureServices: (role, services) =>
        {
            if (role == HostRole.Public)
            {
                services.AddSingleton<IHostedService>(sp =>
                {
                    AcmeRenewalScheduler scheduler = sp.GetRequiredService<AcmeRenewalScheduler>();
                    return new PublicStoppingProbe(
                        sp.GetRequiredService<IHostApplicationLifetime>(),
                        () => completedWhenPublicStopping = scheduler.ExecuteTask?.IsCompleted == true);
                });
            }
        });
        await host.StartAsync(Ct);

        Assert.NotNull(host.Shared.AcmeScheduler.ExecuteTask);
        Assert.False(host.Shared.AcmeScheduler.ExecuteTask.IsCompleted);

        await host.StopAsync(Ct);

        // Recorded inside the public app's ApplicationStopping callback: the drain is before that callback, so the
        // scheduler must already have finished there, not merely by the time StopAsync returned.
        Assert.True(completedWhenPublicStopping, "the renewal scheduler was still running when the public app began stopping");
        Assert.True(host.Shared.AcmeScheduler.ExecuteTask.IsCompletedSuccessfully);
    }

    /// <summary>Runs an action when a host's <see cref="IHostApplicationLifetime.ApplicationStopping"/> fires.</summary>
    private sealed class PublicStoppingProbe(IHostApplicationLifetime lifetime, Action onStopping) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            lifetime.ApplicationStopping.Register(onStopping);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task WithTheSchedulerSwitchedOffNothingIsScheduled()
    {
        await using PaduraksaHost host = TestHost.Create(settings: new Dictionary<string, string?> { ["Paduraksa:Acme:Enabled"] = "false" });
        await host.StartAsync(Ct);

        // Never started: a hosted service would have an execution task by now. The services are there for an operator's request.
        Assert.Null(host.Shared.AcmeScheduler.ExecuteTask);
        Assert.NotNull(host.Shared.AcmeIssuance);
        await host.StopAsync(Ct);
    }

    [Theory]
    [InlineData("Paduraksa:Acme:SchedulerStartDelay", "02:00:00")]
    [InlineData("Paduraksa:Acme:SchedulerPollInterval", "00:00:00.500")]
    [InlineData("Paduraksa:Acme:SchedulerPollInterval", "2.00:00:00")]
    [InlineData("Paduraksa:Acme:DnsPropagationDelay", "02:00:00")]
    [InlineData("Paduraksa:Acme:DnsPropagationDelay", "-00:00:01")]
    public void PacingOutsideItsBoundsIsRefusedAtStartup(string key, string value)
    {
        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(() => TestHost.Create(settings: new Dictionary<string, string?> { [key] = value }));

        Assert.Contains(ex.Failures, f => f.StartsWith(key, StringComparison.Ordinal));
    }

    [Fact]
    public void ATrustedRootsFileThatHoldsNoRootStopsTheStartAndNamesTheFile()
    {
        using TempDirectory directory = new();
        string file = Path.Combine(directory.Path, "roots.pem");
        File.WriteAllText(file, NotARootPem);

        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(
            () => TestHost.Create(settings: new Dictionary<string, string?> { ["Paduraksa:Acme:TrustedRootsFile"] = file }));

        string failure = Assert.Single(ex.Failures);
        Assert.Contains("not a self-signed CA certificate", failure, StringComparison.Ordinal);
        Assert.Contains(file, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrustedRootsFileThatIsTooLargeOrNotPemIsRefused()
    {
        using TempDirectory directory = new();
        string large = Path.Combine(directory.Path, "large.pem");
        File.WriteAllBytes(large, new byte[(1024 * 1024) + 1]);
        string text = Path.Combine(directory.Path, "text.pem");
        File.WriteAllText(text, "this is not a certificate");

        Assert.Throws<OptionsValidationException>(() => TestHost.Create(settings: new Dictionary<string, string?> { ["Paduraksa:Acme:TrustedRootsFile"] = large }));
        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(() => TestHost.Create(settings: new Dictionary<string, string?> { ["Paduraksa:Acme:TrustedRootsFile"] = text }));
        Assert.Contains("holds no certificate", Assert.Single(ex.Failures), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task PlainHttpAcmeIsAWarnedOptInAndOffByDefault(string setting, bool warned)
    {
        LogCapture logs = new();
        await using PaduraksaHost host = TestHost.Create(
            settings: new Dictionary<string, string?> { ["Paduraksa:Development:AcmeInsecureHttp"] = setting },
            logs: logs);
        await host.StartAsync(Ct);

        Assert.Equal(warned, logs.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("ACME over plain http", StringComparison.Ordinal)));
        await host.StopAsync(Ct);
    }

    [Theory]
    [InlineData("Paduraksa:Acme:DnsPropagation:Timeout", "00:00:00")]
    [InlineData("Paduraksa:Acme:DnsPropagation:Timeout", "02:00:00")]
    [InlineData("Paduraksa:Acme:DnsPropagation:InitialInterval", "00:00:00.010")]
    [InlineData("Paduraksa:Acme:DnsPropagation:MaxInterval", "00:20:00")]
    [InlineData("Paduraksa:Acme:DnsPropagation:QueryTimeout", "00:01:00")]
    [InlineData("Paduraksa:Acme:DnsPropagation:UnreachableRounds", "0")]
    [InlineData("Paduraksa:Acme:DnsPropagation:UnreachableRounds", "50")]
    [InlineData("Paduraksa:Acme:DnsPropagation:Resolvers:0", "not-an-address")]
    public void InvalidDnsPropagationSettingsRefuseTheStart(string key, string value)
    {
        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(() => TestHost.Create(settings: new Dictionary<string, string?> { [key] = value }));

        Assert.Contains(ex.Failures, f => f.StartsWith(key.Replace(":Resolvers:0", ":Resolvers", StringComparison.Ordinal), StringComparison.Ordinal));
    }

    [Fact]
    public void AMaximumIntervalBelowTheInitialOneRefusesTheStart()
    {
        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(() => TestHost.Create(settings: new Dictionary<string, string?>
        {
            ["Paduraksa:Acme:DnsPropagation:InitialInterval"] = "00:00:30",
            ["Paduraksa:Acme:DnsPropagation:MaxInterval"] = "00:00:05",
        }));

        Assert.Contains(ex.Failures, f => f.Contains("MaxInterval must not be below", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DnsProvidersAreInstalledSoDns01IsNoLongerNotSupportedAndTheSettingsAreBound()
    {
        IDbContextFactory<ConfigDbContext>? contexts = null;
        await using PaduraksaHost host = TestHost.Create(
            settings: new Dictionary<string, string?>
            {
                ["Paduraksa:Acme:DnsPropagation:Timeout"] = "00:02:00",
                ["Paduraksa:Acme:DnsPropagation:Resolvers:0"] = "10.9.8.7",
            },
            configureShared: builder => contexts = (IDbContextFactory<ConfigDbContext>)builder.Registrations.First(r => r.Key == typeof(IDbContextFactory<ConfigDbContext>)).Value);
        await host.StartAsync(Ct);

        // A provider row whose secret is not in the vault: the real provider is built and asks the vault for a handler, which refuses
        // before anything is sent (no network). Without a registered factory the answer would be "No DNS provider implementations".
        // (A raw insert: the row type is internal to Data, and the vault's secret store is not reachable from here.)
        DnsProviderId id = DnsProviderId.New();
        byte[] idBytes = id.Value.ToByteArray(bigEndian: true);
        byte[] secretBytes = SecretId.New().Value.ToByteArray(bigEndian: true);
        await using (ConfigDbContext context = await contexts!.CreateDbContextAsync(Ct))
        {
            _ = await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO DnsProviders (Id, Kind, Label, CredentialSecretId, CreatedAt, UpdatedAt) VALUES ({idBytes}, 'cloudflare', 'test', {secretBytes}, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z')",
                Ct);
        }

        DnsProviderTestResult result = await host.Shared.DnsProviders.TestAsync(id, Paduraksa.Application.Commands.CommandOrigin.Cli, Ct);

        Assert.True(result.Found);
        Assert.False(result.Valid);
        Assert.DoesNotContain("No DNS provider implementations", result.Message, StringComparison.Ordinal);
        Assert.Contains("vault refused", result.Message, StringComparison.Ordinal);
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task TheDnsPropagationCheckCanBeSwitchedOff()
    {
        await using PaduraksaHost host = TestHost.Create(settings: new Dictionary<string, string?> { ["Paduraksa:Acme:DnsPropagation:Enabled"] = "false" });
        await host.StartAsync(Ct);
        await host.StopAsync(Ct);
    }
}
