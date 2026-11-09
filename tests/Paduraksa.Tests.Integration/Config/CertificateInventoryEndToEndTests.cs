using System.Net;
using Microsoft.Data.Sqlite;
using Paduraksa.Application.Certificates;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Proxy.Tls;
using Paduraksa.Server.Hosting;
using Paduraksa.Server.Placeholders;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Integration.Proxy;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Integration.Config.ConfigTestSupport;

namespace Paduraksa.Tests.Integration.Config;

public sealed class CertificateInventoryEndToEndTests
{
    private static readonly CertificateId A = TestIds.Certificate("inv-a");

    [Fact]
    public async Task ACertificateChangeRepublishesTheLatestRevisionWithoutANewOne()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using TestCertificates certificates = new();
        await using PaduraksaHost host = TestHost.Create();
        await host.StartAsync(ct);
        CertificateService service = host.Shared.CertificateService;

        // Issued: the vault would create the key and chain, then the service records them, and the Auto host is covered.
        ServerCertificate v1 = certificates.Create("a.test v1", "a.test");
        host.Shared.Certificates.Provide(A, v1);
        await RegisterAsync(service, A, v1);
        RevisionRef revision = await CommitAsync(host, new UpsertHostCommand(RespondHost("a", "a.test", "a")), ct);
        Assert.Equal("a.test v1", await EdgeClient.ServedSubjectAsync(host, "a.test"));
        Assert.Empty(host.Shared.Config.Warnings);

        // Renewed under the same ID: no new revision, a new snapshot generation, and new handshakes get the new certificate.
        long generation = host.Shared.SnapshotStore.Current.Generation;
        ServerCertificate v2 = certificates.Create("a.test v2", "a.test");
        host.Shared.Certificates.Provide(A, v2);
        CertificateChangeResult renewed = await RegisterAsync(service, A, v2);

        Assert.Equal(CertificateChangeStatus.Committed, renewed.Status);
        Assert.True(renewed.Publish!.Succeeded);
        Assert.Equal(revision, (await host.Shared.Config.GetCurrentAsync(ct)).Revision);
        Assert.True(host.Shared.SnapshotStore.Current.Generation > generation);
        Assert.Equal("a.test v2", await EdgeClient.ServedSubjectAsync(host, "a.test"));

        // The same metadata again changes nothing and publishes nothing.
        generation = host.Shared.SnapshotStore.Current.Generation;
        Assert.Equal(CertificateChangeStatus.Unchanged, (await RegisterAsync(service, A, v2)).Status);
        Assert.Equal(generation, host.Shared.SnapshotStore.Current.Generation);
    }

    [Fact]
    public async Task ADeletedCertificateLeavesItsHostsOnTheDefaultWithAWarningAndIsRefusedWhileBound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using TestCertificates certificates = new();
        await using PaduraksaHost host = TestHost.Create();
        await host.StartAsync(ct);
        CertificateService service = host.Shared.CertificateService;
        ServerCertificate cert = certificates.Create("a.test", "a.test");
        host.Shared.Certificates.Provide(A, cert);
        await RegisterAsync(service, A, cert);
        await CommitAsync(host, new UpsertHostCommand(RespondHost("a", "a.test", "a") with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = A } }), ct);
        Assert.Equal("a.test", await EdgeClient.ServedSubjectAsync(host, "a.test"));

        CertificateChangeResult refused = await service.DeleteAsync(A, Origin, ct);

        Assert.Equal(CertificateChangeStatus.Invalid, refused.Status);
        ConfigError error = Assert.Single(refused.Errors);
        Assert.Equal(("/hosts/0/certificate", CertificateErrorCodes.InUse), (error.Path, error.Code));
        Assert.Equal("a.test", await EdgeClient.ServedSubjectAsync(host, "a.test"));

        // Back to Auto, then the delete goes through; nothing covers the host any more.
        HostConfig a = (await host.Shared.Config.GetCurrentAsync(ct)).Document.Hosts.Single();
        await CommitAsync(host, new UpsertHostCommand(a with { Certificate = new CertificateBinding() }), ct);
        CertificateChangeResult deleted = await service.DeleteAsync(A, Origin, ct);

        Assert.Equal(CertificateChangeStatus.Committed, deleted.Status);
        Assert.True(deleted.Publish!.Succeeded);
        Assert.Equal("Paduraksa placeholder", await EdgeClient.ServedSubjectAsync(host, "a.test"));
        Assert.Equal("certificate.auto.noCoverage", Assert.Single(host.Shared.Config.Warnings).Code);
        Assert.DoesNotContain(await service.ListAsync(cancellationToken: ct), c => c.Id == A);
    }

    [Fact]
    public async Task ThePlaceholderKeepsAConfigThatNamesNoDefaultOrItsStableIdStarting()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string data = TestDataDirectories.NewPath();
        CertificateBinding placeholder = new() { Mode = CertificateBindingMode.Specific, CertificateId = PlaceholderCertificates.Id };
        await using (PaduraksaHost first = TestHost.Create(dataDirectory: data))
        {
            await first.StartAsync(ct);
            await CommitAsync(first, new UpsertHostCommand(RespondHost("p", "p.test", "p") with { Certificate = placeholder }), ct);
        }

        // A new process has a new placeholder certificate under the same ID: the stored document still compiles and serves.
        await using PaduraksaHost host = TestHost.Create(dataDirectory: data);
        await host.StartAsync(ct);

        Assert.False(host.Shared.Config.Status.IsDegraded);
        Assert.Equal("certificate.specific.noCoverage", Assert.Single(host.Shared.Config.Warnings).Code); // it is usable, it just names localhost
        Assert.Equal("Paduraksa placeholder", await EdgeClient.ServedSubjectAsync(host, "p.test"));
        Assert.Equal("Paduraksa placeholder", await EdgeClient.ServedSubjectAsync(host, "unknown.test")); // the fallback default
    }

    [Fact]
    public async Task ARestoreAfterALostDatabaseKeepsHostsWithSpecificBindingsAndTheNextCertificateChangeKeepsServingThem()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using TestCertificates certificates = new();
        string data = TestDataDirectories.NewPath();
        CertificateBinding specific = new() { Mode = CertificateBindingMode.Specific, CertificateId = A };
        await using (PaduraksaHost first = TestHost.Create(dataDirectory: data))
        {
            await first.StartAsync(ct);
            ServerCertificate original = certificates.Create("one.test", "one.test");
            first.Shared.Certificates.Provide(A, original);
            await RegisterAsync(first.Shared.CertificateService, A, original);
            await CommitAsync(first, new UpsertHostCommand(RespondHost("one", "one.test", "one") with { Certificate = specific }), ct);
            await first.Shared.LastKnownGood.FlushAsync();
        }

        SqliteConnection.ClearAllPools();
        foreach (string file in Directory.GetFiles(data, ConfigDatabase.FileName + "*"))
        {
            File.Delete(file);
        }

        // The inventory went with the database: the restore degrades the binding with a warning instead of failing.
        await using PaduraksaHost host = TestHost.Create(dataDirectory: data);
        await host.StartAsync(ct);
        using HttpClient client = TestHost.CreateClient(host);
        Assert.False(host.Shared.Config.Status.IsDegraded);
        RevisionRef restored = host.Shared.Config.Status.Revision!;
        Assert.Equal(1, restored.Seq);
        Assert.Contains(host.Shared.Config.Warnings, w => w.Code == "certificate.specific.unusable");
        Assert.Equal((HttpStatusCode.OK, "one"), await GetAsync(client, host, "one.test", "/", ct));

        // The certificate is registered again (the vault would re-describe it): the host is served, nothing else changes.
        ServerCertificate again = certificates.Create("one.test", "one.test");
        host.Shared.Certificates.Provide(A, again);
        CertificateChangeResult result = await RegisterAsync(host.Shared.CertificateService, A, again);

        Assert.True(result.Publish!.Succeeded);
        Assert.Equal(restored, (await host.Shared.Config.GetCurrentAsync(ct)).Revision);
        Assert.Equal((HttpStatusCode.OK, "one"), await GetAsync(client, host, "one.test", "/", ct));
        Assert.Equal("one.test", await EdgeClient.ServedSubjectAsync(host, "one.test"));
        Assert.DoesNotContain(host.Shared.Config.Warnings, w => w.Code == "certificate.specific.unusable");
    }

    private static Task<CertificateChangeResult> RegisterAsync(CertificateService service, CertificateId id, ServerCertificate certificate) =>
        service.RegisterAsync(
            new CertificateRegistration(id, CertificateSource.Uploaded, PlaceholderCertificates.InfoOf(certificate.Leaf)),
            Origin,
            TestContext.Current.CancellationToken);
}
