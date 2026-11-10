using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Data.Commands;

using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Certificates;

/// <summary>
/// The certificate service on a real database and the real config writer: metadata changes are audited
/// in the transaction that makes them, never create a revision, and (when they affect serving) publish the latest
/// revision again.
/// </summary>
public sealed class CertificateServiceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow.AddDays(-1);

    private static CertificateId Id(string label) => CertificateId.FromValue(new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(label)).AsSpan(0, 16)));

    private static CertificateInfo Info(string fingerprint, params string[] names) =>
        new($"CN={names.FirstOrDefault()}", names, Start, Start.AddDays(90), "CN=Test CA", "00AB", "cd", fingerprint, "ECDSA P-256", ["CN=Test CA"], "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n");

    private static CertificateRegistration Registration(string label, string fingerprint, params string[] names) =>
        new(Id(label), CertificateSource.Acme, Info(fingerprint, names));

    /// <summary>A committed revision to republish: with none (and no last-known-good), a certificate change publishes nothing.</summary>
    private static Task<ConfigCommandResult> Seed(ConfigServiceHarness harness) =>
        harness.RunAsync(new UpsertHostCommand(Objects.RespondHost(HostId.New(), "seed.example.com")));

    private static (ConfigServiceHarness Harness, CertificateService Certificates) Create(ConfigServiceHarness harness) =>
        (harness, new CertificateService(harness.Db.Factory, harness.Service, TimeProvider.System, NullLogger<CertificateService>.Instance));

    [Fact]
    public async Task RegisteringAddsTheCertificateAuditsItAndRepublishesWithoutARevision()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (_, certificates) = Create(harness);
        await harness.RunAsync(new UpsertHostCommand(Objects.RespondHost(HostId.New(), "app.example.com")));
        var revisions = (await harness.RevisionsAsync()).Count;
        var generation = harness.Store.Current.Generation;

        var result = await certificates.RegisterAsync(
            Registration("a", "f1", "App.Example.com.", "*.EXAMPLE.com", "192.0.2.7", "2001:DB8::1", "not a name", "app.example.com"),
            ConfigServiceHarness.Admin,
            Ct);

        Assert.Equal(CertificateChangeStatus.Committed, result.Status);
        Assert.True(result.Publish!.Succeeded);
        Assert.Equal(["*.example.com", "192.0.2.7", "2001:db8::1", "app.example.com"], result.Certificate!.Names);
        Assert.Equal(revisions, (await harness.RevisionsAsync()).Count);
        Assert.True(harness.Store.Current.Generation > generation);
        var audit = Assert.Single(await harness.AuditAsync(), e => e.Action == "certificate.add");
        Assert.Equal((AuditOutcome.Success, Id("a").ToString()), (audit.Outcome, audit.Target));
        Assert.DoesNotContain("BEGIN CERTIFICATE", audit.Details, StringComparison.Ordinal);
        await harness.AssertChainIntactAsync();
    }

    [Fact]
    public async Task RegisteringTheSameMetadataAgainChangesNothing()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (_, certificates) = Create(harness);
        await certificates.RegisterAsync(Registration("a", "f1", "a.example.com"), ConfigServiceHarness.Admin, Ct);
        var generation = harness.Store.Current.Generation;
        var audits = (await harness.AuditAsync()).Count;

        var again = await certificates.RegisterAsync(Registration("a", "f1", "a.example.com"), ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateChangeStatus.Unchanged, again.Status);
        Assert.Null(again.Publish);
        Assert.Equal(generation, harness.Store.Current.Generation);
        Assert.Equal(audits, (await harness.AuditAsync()).Count);
    }

    [Fact]
    public async Task ReplacingKeepsTheIdLabelAndCreationTimeAndRepublishes()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (_, certificates) = Create(harness);
        await Seed(harness);
        var first = await certificates.RegisterAsync(Registration("a", "f1", "a.example.com") with { Label = "main", Notes = "n" }, ConfigServiceHarness.Admin, Ct);
        var generation = harness.Store.Current.Generation;

        var renewed = await certificates.RegisterAsync(Registration("a", "f2", "a.example.com", "www.example.com"), ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateChangeStatus.Committed, renewed.Status);
        Assert.True(harness.Store.Current.Generation > generation);
        Assert.Equal(("main", "n", first.Certificate!.CreatedAt, "f2"), (renewed.Certificate!.Label, renewed.Certificate.Notes, renewed.Certificate.CreatedAt, renewed.Certificate.Sha256Fingerprint));
        Assert.Equal(["a.example.com", "www.example.com"], renewed.Certificate.Names);
        Assert.Equal(["certificate.add", "certificate.update"], (await harness.AuditAsync()).Select(e => e.Action).Where(a => a.StartsWith("certificate.", StringComparison.Ordinal)));
        Assert.Single(await certificates.ListAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task ABuiltInCertificatesIdCantBeRegisteredAndRefusalsAreAuditedAsWhatWasAttempted()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var builtIn = new BuiltIns(Id("built-in"));
        var certificates = new CertificateService(harness.Db.Factory, harness.Service, TimeProvider.System, NullLogger<CertificateService>.Instance, builtIn);

        var registered = await certificates.RegisterAsync(Registration("built-in", "f1", "a.example.com"), ConfigServiceHarness.Admin, Ct);
        var pending = await certificates.RegisterPendingAsync(Id("built-in"), CertificateSource.Acme, ["a.example.com"], null, ConfigServiceHarness.Admin, Ct);
        await certificates.RegisterAsync(Registration("real", "f2", "b.example.com"), ConfigServiceHarness.Admin, Ct);
        var invalidUpdate = await certificates.RegisterAsync(Registration("real", "f3", "not a name"), ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateErrorCodes.BuiltIn, Assert.Single(registered.Errors).Code);
        Assert.Equal(CertificateErrorCodes.BuiltIn, Assert.Single(pending.Errors).Code);
        Assert.Equal(CertificateChangeStatus.Invalid, invalidUpdate.Status);
        Assert.Equal(
            [("certificate.add", AuditOutcome.Invalid), ("certificate.add", AuditOutcome.Invalid), ("certificate.add", AuditOutcome.Success), ("certificate.update", AuditOutcome.Invalid)],
            (await harness.AuditAsync()).Select(e => (e.Action, e.Outcome)));
        Assert.Single(await certificates.ListAsync(cancellationToken: Ct));
    }

    private sealed class BuiltIns(CertificateId id) : IBuiltInCertificates
    {
        public IReadOnlyList<BuiltInCertificate> Current => [new BuiltInCertificate(id, Info("built-in"))];
    }

    [Fact]
    public async Task ACertificateWithoutAUsableNameIsInvalid()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (_, certificates) = Create(harness);

        var result = await certificates.RegisterAsync(Registration("a", "f1", "not a name", "bad_@x"), ConfigServiceHarness.Admin, Ct);

        Assert.Equal((CertificateChangeStatus.Invalid, CertificateErrorCodes.NamesRequired), (result.Status, Assert.Single(result.Errors).Code));
        Assert.Empty(await certificates.ListAsync(cancellationToken: Ct));
        Assert.Equal(AuditOutcome.Invalid, Assert.Single(await harness.AuditAsync()).Outcome);
    }

    [Fact]
    public async Task DeleteIsRefusedWhileASpecificBindingOrTheDefaultUsesItAndNamesTheHosts()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (_, certificates) = Create(harness);
        await certificates.RegisterAsync(Registration("a", "f1", "a.example.com"), ConfigServiceHarness.Admin, Ct);
        var binding = new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = Id("a") };
        var other = Objects.RespondHost(HostId.New(), "other.example.com");
        var bound = Objects.RespondHost(HostId.New(), "a.example.com") with { Certificate = binding };
        await harness.RunAsync(new UpsertHostCommand(other));
        await harness.RunAsync(new UpsertHostCommand(bound));
        var settings = (await harness.CurrentAsync()).Settings;
        await harness.RunAsync(new UpdateSettingsCommand(settings with { DefaultCertificateId = Id("a") }));
        var revisions = (await harness.RevisionsAsync()).Count;

        var refused = await certificates.DeleteAsync(Id("a"), ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateChangeStatus.Invalid, refused.Status);
        var current = await harness.CurrentAsync();
        var path = $"/hosts/{current.Hosts.ToList().FindIndex(h => h.Id == bound.Id)}/certificate";
        Assert.Equal([path, "/settings/defaultCertificateId"], refused.Errors.Select(e => e.Path));
        Assert.All(refused.Errors, e => Assert.Equal(CertificateErrorCodes.InUse, e.Code));
        Assert.Contains("a.example.com", refused.Errors[0].Message, StringComparison.Ordinal);
        Assert.NotNull(await certificates.GetAsync(Id("a"), Ct));
        Assert.Equal(revisions, (await harness.RevisionsAsync()).Count);
        var audit = (await harness.AuditAsync()).Last(e => e.Action == "certificate.delete");
        Assert.Equal(AuditOutcome.Invalid, audit.Outcome);
    }

    [Fact]
    public async Task DeletingAnUnboundCertificateRemovesItAndItsNamesAndRepublishes()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (_, certificates) = Create(harness);
        await Seed(harness);
        await certificates.RegisterAsync(Registration("a", "f1", "a.example.com", "*.example.com"), ConfigServiceHarness.Admin, Ct);
        var generation = harness.Store.Current.Generation;

        var deleted = await certificates.DeleteAsync(Id("a"), ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateChangeStatus.Committed, deleted.Status);
        Assert.True(deleted.Publish!.Succeeded);
        Assert.True(harness.Store.Current.Generation > generation);
        Assert.Equal(0L, await harness.CountAsync("Certificates") + await harness.CountAsync("CertificateNames"));
        Assert.Equal(AuditOutcome.Success, (await harness.AuditAsync()).Last().Outcome);
        Assert.Equal(CertificateChangeStatus.NotFound, (await certificates.DeleteAsync(Id("a"), ConfigServiceHarness.Admin, Ct)).Status);
    }

    [Fact]
    public async Task PendingCertificatesAreRecordedWithoutServingAndCompleteWhenTheChainArrives()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (_, certificates) = Create(harness);
        await Seed(harness);
        var generation = harness.Store.Current.Generation;

        var pending = await certificates.RegisterPendingAsync(Id("a"), CertificateSource.Acme, ["A.example.com"], "order", ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateChangeStatus.Committed, pending.Status);
        Assert.Null(pending.Publish);
        Assert.Equal(generation, harness.Store.Current.Generation);
        Assert.Equal((CertificateStatus.Pending, CertificateValidity.NotYetValid), (pending.Certificate!.Status, pending.Certificate.Validity));
        Assert.Equal(["a.example.com"], pending.Certificate.Names);
        Assert.Equal(CertificateChangeStatus.Invalid, (await certificates.RegisterPendingAsync(Id("a"), CertificateSource.Acme, ["a.example.com"], null, ConfigServiceHarness.Admin, Ct)).Status);
        var activate = await certificates.SetStatusAsync(Id("a"), CertificateStatus.Active, null, ConfigServiceHarness.Admin, Ct);
        Assert.Equal(CertificateErrorCodes.NoChain, Assert.Single(activate.Errors).Code);

        var issued = await certificates.RegisterAsync(Registration("a", "f1", "a.example.com"), ConfigServiceHarness.Admin, Ct);

        Assert.Equal((CertificateChangeStatus.Committed, CertificateStatus.Active, "order"), (issued.Status, issued.Certificate!.Status, issued.Certificate.Label));
        Assert.NotNull(issued.Publish);
    }

    [Fact]
    public async Task FailingAndRecoveringACertificateChangesWhatIsServed()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (_, certificates) = Create(harness);
        await Seed(harness);
        await certificates.RegisterAsync(Registration("a", "f1", "a.example.com"), ConfigServiceHarness.Admin, Ct);
        var generation = harness.Store.Current.Generation;

        var failed = await certificates.SetStatusAsync(Id("a"), CertificateStatus.Failed, "renewal failed: rate limited", ConfigServiceHarness.Admin, Ct);

        Assert.Equal((CertificateChangeStatus.Committed, CertificateStatus.Failed, "renewal failed: rate limited"), (failed.Status, failed.Certificate!.Status, failed.Certificate.StatusMessage));
        Assert.True(harness.Store.Current.Generation > generation);
        Assert.Equal(CertificateChangeStatus.Unchanged, (await certificates.SetStatusAsync(Id("a"), CertificateStatus.Failed, "renewal failed: rate limited", ConfigServiceHarness.Admin, Ct)).Status);
        Assert.Equal(CertificateStatus.Active, (await certificates.SetStatusAsync(Id("a"), CertificateStatus.Active, null, ConfigServiceHarness.Admin, Ct)).Certificate!.Status);
    }

    [Fact]
    public async Task DetailsChangeWithoutPublishing()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (_, certificates) = Create(harness);
        await certificates.RegisterAsync(Registration("a", "f1", "a.example.com"), ConfigServiceHarness.Admin, Ct);
        var generation = harness.Store.Current.Generation;

        var result = await certificates.UpdateDetailsAsync(Id("a"), " Main site ", "notes", ConfigServiceHarness.Admin, Ct);

        Assert.Equal(("Main site", "notes", null), (result.Certificate!.Label, result.Certificate.Notes, result.Publish));
        Assert.Equal(generation, harness.Store.Current.Generation);
        Assert.Equal(CertificateChangeStatus.NotFound, (await certificates.UpdateDetailsAsync(Id("zzz"), null, null, ConfigServiceHarness.Admin, Ct)).Status);
        Assert.Equal(CertificateErrorCodes.TextTooLong, Assert.Single((await certificates.UpdateDetailsAsync(Id("a"), new string('x', 300), null, ConfigServiceHarness.Admin, Ct)).Errors).Code);
    }

    [Fact]
    public async Task RemovingTheLastCertificateCoveringTheAdminHostIsRefusedThroughTheProxy()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (_, certificates) = Create(harness);
        await harness.RunAsync(new UpsertHostCommand(new HostConfig(HostId.New(), ["admin.example.com"], Kind: HostKind.Admin)));
        var proxied = new CommandOrigin(Actor.Cli, Channel.Api, IPAddress.Loopback, new AdminRequestFacts("admin.example.com", Https: true, IPAddress.Loopback));

        await certificates.RegisterAsync(Registration("admin", "f1", "admin.example.com"), ConfigServiceHarness.Admin, Ct);
        var refusedDelete = await certificates.DeleteAsync(Id("admin"), proxied, Ct);
        Assert.Equal(CertificateChangeStatus.Invalid, refusedDelete.Status);
        Assert.Equal(CertificateErrorCodes.AdminCoverage, Assert.Single(refusedDelete.Errors).Code);

        var refusedReplace = await certificates.RegisterAsync(Registration("admin", "f2", "other.example.com"), proxied, Ct);
        Assert.Equal(CertificateChangeStatus.Invalid, refusedReplace.Status);
        Assert.Equal(CertificateErrorCodes.AdminCoverage, Assert.Single(refusedReplace.Errors).Code);

        // Another usable certificate still covers the name: the first can go.
        await certificates.RegisterAsync(Registration("other", "f3", "admin.example.com"), ConfigServiceHarness.Admin, Ct);
        var deleted = await certificates.DeleteAsync(Id("admin"), proxied, Ct);

        Assert.Equal(CertificateChangeStatus.Committed, deleted.Status);
    }

    [Fact]
    public async Task CertificatesAreListedAndFoundByTheNamesTheyCover()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (_, certificates) = Create(harness);
        await certificates.RegisterAsync(Registration("exact", "f1", "www.example.com"), ConfigServiceHarness.Admin, Ct);
        await certificates.RegisterAsync(Registration("wild", "f2", "*.example.com") with { Source = CertificateSource.Uploaded }, ConfigServiceHarness.Admin, Ct);
        await certificates.RegisterAsync(Registration("other", "f3", "other.test"), ConfigServiceHarness.Admin, Ct);

        async Task<string[]> Covering(string name) =>
            [.. (await certificates.ListAsync(new CertificateQuery(CoversName: name), Ct)).Select(c => c.Sha256Fingerprint)];

        Assert.Equal(["f1", "f2"], (await Covering("WWW.example.com.")).Order(StringComparer.Ordinal));
        Assert.Equal(["f2"], await Covering("api.example.com"));
        Assert.Empty(await Covering("example.com"));
        Assert.Empty(await Covering("not a name"));
        Assert.Equal(3, (await certificates.ListAsync(cancellationToken: Ct)).Count);
        Assert.Equal(Id("wild"), Assert.Single(await certificates.ListAsync(new CertificateQuery(Source: CertificateSource.Uploaded), Ct)).Id);
        Assert.Equal(3, (await certificates.ListAsync(new CertificateQuery(Status: CertificateStatus.Active), Ct)).Count);

        var detail = await certificates.GetAsync(Id("exact"), Ct);
        Assert.Equal(("CN=Test CA", CertificateValidity.Valid), (detail!.ChainIssuers.Single(), detail.Summary.Validity));
        Assert.Contains("BEGIN CERTIFICATE", detail.PublicChainPem, StringComparison.Ordinal);
        Assert.Null(await certificates.GetAsync(Id("nope"), Ct));
    }
}
