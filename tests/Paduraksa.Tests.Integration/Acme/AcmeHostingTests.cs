using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Acme;

/// <summary>How the process hosts ACME (PLAN §9.3): the scheduler's switch and lifecycle, the trusted-roots file, and the plain-http development switch. No CA is involved.</summary>
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
        await using PaduraksaHost host = TestHost.Create();
        await host.StartAsync(Ct);

        Assert.NotNull(host.Shared.AcmeScheduler.ExecuteTask);
        Assert.False(host.Shared.AcmeScheduler.ExecuteTask.IsCompleted);

        await host.StopAsync(Ct);

        // Drained before the public app stopped (and so before its listener closed): it is finished by the time the hosts stopped.
        Assert.True(host.Shared.AcmeScheduler.ExecuteTask.IsCompletedSuccessfully);
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
}
