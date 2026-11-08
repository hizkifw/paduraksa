using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Data.Commands;

namespace Paduraksa.Tests.Data.Certificates;

/// <summary>
/// The vault flows (<see cref="CertificateOperations"/>) over a real config database and writer, with a fake vault that
/// records what it was asked and can be told to fail: what is left behind when a step fails, and the order of the steps.
/// </summary>
public sealed class CertificateOperationsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (CertificateOperations Operations, CertificateService Certificates, FakeVault Vault) Create(
        ConfigServiceHarness harness, ICertificateOperationAuthorizer? authorizer = null)
    {
        var vault = new FakeVault();
        var certificates = new CertificateService(harness.Db.Factory, harness.Service, TimeProvider.System, NullLogger<CertificateService>.Instance);
        var operations = new CertificateOperations(certificates, vault, vault, authorizer ?? new Allow(), NullLogger<CertificateOperations>.Instance);
        return (operations, certificates, vault);
    }

    private static MemoryStream Upload(string text = "upload") => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task SelfSignedIsRegisteredPendingThenActivatedAfterTheVaultMadeIt()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, certificates, vault) = Create(harness);
        await harness.RunAsync(new UpsertHostCommand(Objects.RespondHost(HostId.New(), "seed.example.com")));
        vault.OnCreate = async id =>
        {
            // While the vault works, the row exists and is pending: visible, never served.
            var during = await certificates.GetAsync(id, Ct);
            Assert.Equal(CertificateStatus.Pending, during!.Summary.Status);
        };

        var result = await operations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["App.Example.com", "192.0.2.1"], KeySpec.Rsa2048, Label: "mine"), ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateOperationStatus.Completed, result.Status);
        Assert.True(result.Publish!.Succeeded);
        var summary = result.Certificate!;
        Assert.Equal((CertificateSource.SelfSigned, CertificateStatus.Active, "mine"), (summary.Source, summary.Status, summary.Label));
        Assert.Equal(["192.0.2.1", "app.example.com"], summary.Names);
        Assert.Equal([summary.Id], vault.Keys);
        Assert.Equal(["create:SelfSigned"], vault.Calls);
        Assert.Equal(KeySpec.Rsa2048, vault.LastSpec);
        Assert.Equal(CertificateOperations.DefaultSelfSignedValidity, vault.LastValidity);
    }

    [Fact]
    public async Task InvalidNamesAreRefusedBeforeTheVaultIsTouched()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, certificates, vault) = Create(harness);

        var result = await operations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["not a name"]), ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateOperationStatus.Invalid, result.Status);
        Assert.Equal(CertificateErrorCodes.NameInvalid, Assert.Single(result.Errors).Code);
        Assert.Empty(vault.Calls);
        Assert.Empty(await certificates.ListAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task AVaultRefusalRemovesThePendingRow()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, certificates, vault) = Create(harness);
        vault.CreateFailure = new ArgumentException("bad validity");

        var result = await operations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["a.example.com"]), ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateOperationStatus.Invalid, result.Status);
        Assert.Equal(CertificateErrorCodes.IssuanceRefused, Assert.Single(result.Errors).Code);
        Assert.Empty(await certificates.ListAsync(cancellationToken: Ct));
        Assert.Empty(vault.Keys);
    }

    [Fact]
    public async Task AVaultFailureRemovesThePendingRowAndPropagates()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, certificates, vault) = Create(harness);
        vault.CreateFailure = new IOException("disk gone");

        await Assert.ThrowsAsync<IOException>(
            async () => await operations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["a.example.com"]), ConfigServiceHarness.Admin, Ct));

        Assert.Empty(await certificates.ListAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task IfTheInventoryRefusesWhatTheVaultMadeTheKeyAndTheRowAreTakenBack()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, certificates, vault) = Create(harness);
        vault.InfoOverride = info => info with { NotAfter = info.NotBefore };

        var result = await operations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["a.example.com"]), ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateOperationStatus.Invalid, result.Status);
        Assert.Equal(CertificateErrorCodes.ValidityInvalid, Assert.Single(result.Errors).Code);
        Assert.Empty(vault.Keys);
        Assert.Empty(await certificates.ListAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task LeavesNeedTheRootWhichIsCreatedOnceAndHasNoNames()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, certificates, vault) = Create(harness);

        var early = await operations.IssueFromLocalCaAsync(new LocalCaLeafRequest(["nas.lan"]), ConfigServiceHarness.Admin, Ct);
        Assert.Equal((CertificateOperationStatus.Invalid, CertificateErrorCodes.LocalCaMissing), (early.Status, Assert.Single(early.Errors).Code));
        Assert.Empty(vault.Calls);

        var root = await operations.EnsureLocalCaAsync(new LocalCaRequest(), ConfigServiceHarness.Admin, Ct);
        var again = await operations.EnsureLocalCaAsync(new LocalCaRequest(), ConfigServiceHarness.Admin, Ct);
        var leaf = await operations.IssueFromLocalCaAsync(new LocalCaLeafRequest(["nas.lan"], Validity: TimeSpan.FromDays(30)), ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateOperationStatus.Completed, root.Status);
        Assert.Equal((CertificateSource.LocalCaRoot, CertificateStatus.Active), (root.Certificate!.Source, root.Certificate.Status));
        Assert.Empty(root.Certificate.Names);
        Assert.Equal((CertificateOperationStatus.Unchanged, root.Certificate.Id), (again.Status, again.Certificate!.Id));
        Assert.Equal(CertificateSource.LocalCa, leaf.Certificate!.Source);
        Assert.Equal(["create:LocalCaRoot", "create:LocalCa"], vault.Calls);
        Assert.Equal(TimeSpan.FromDays(30), vault.LastValidity);
        Assert.Equal(2, (await certificates.ListAsync(cancellationToken: Ct)).Count);
    }

    [Fact]
    public async Task TheRootCannotBeDeletedWhileLeavesExist()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, _, vault) = Create(harness);
        var root = (await operations.EnsureLocalCaAsync(new LocalCaRequest(), ConfigServiceHarness.Admin, Ct)).Certificate!;
        var leaf = (await operations.IssueFromLocalCaAsync(new LocalCaLeafRequest(["nas.lan"]), ConfigServiceHarness.Admin, Ct)).Certificate!;

        var refused = await operations.DeleteAsync(root.Id, ConfigServiceHarness.Admin, Ct);
        Assert.Equal((CertificateOperationStatus.Invalid, CertificateErrorCodes.LocalCaInUse), (refused.Status, Assert.Single(refused.Errors).Code));
        Assert.Contains(root.Id, vault.Keys);

        Assert.Equal(CertificateOperationStatus.Completed, (await operations.DeleteAsync(leaf.Id, ConfigServiceHarness.Admin, Ct)).Status);
        Assert.Equal(CertificateOperationStatus.Completed, (await operations.DeleteAsync(root.Id, ConfigServiceHarness.Admin, Ct)).Status);
        Assert.Empty(vault.Keys);
    }

    [Fact]
    public async Task UploadRegistersTheVaultsFactsAndAReplacementKeepsTheId()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, certificates, vault) = Create(harness);
        vault.ImportNames = ["site.example.com"];

        var first = await operations.UploadAsync(Upload(), "pw".AsMemory(), "from upload", null, ConfigServiceHarness.Admin, Ct);
        Assert.Equal(CertificateOperationStatus.Completed, first.Status);
        var id = first.Certificate!.Id;
        Assert.Equal(CertificateSource.Uploaded, first.Certificate.Source);
        Assert.Equal("pw", vault.LastPassword);

        vault.ImportNames = ["site.example.com", "www.example.com"];
        var second = await operations.UploadAsync(Upload("v2"), default, null, id, ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateOperationStatus.Completed, second.Status);
        Assert.Equal(id, second.Certificate!.Id);
        Assert.Equal(["site.example.com", "www.example.com"], second.Certificate.Names);
        Assert.Equal("from upload", second.Certificate.Label);
        Assert.Single(await certificates.ListAsync(cancellationToken: Ct));
    }

    [Theory]
    [InlineData(CertificateImportFailure.TooLarge, CertificateErrorCodes.ImportTooLarge)]
    [InlineData(CertificateImportFailure.WrongPassword, CertificateErrorCodes.ImportWrongPassword)]
    [InlineData(CertificateImportFailure.NoMatchingKey, CertificateErrorCodes.ImportNoMatchingKey)]
    [InlineData(CertificateImportFailure.UnsupportedKeyFormat, CertificateErrorCodes.ImportUnsupportedKeyFormat)]
    [InlineData(CertificateImportFailure.NoUsableName, CertificateErrorCodes.ImportNoUsableName)]
    [InlineData(CertificateImportFailure.Unreadable, CertificateErrorCodes.ImportUnreadable)]
    [InlineData(CertificateImportFailure.WeakKey, CertificateErrorCodes.ImportWeakKey)]
    [InlineData(CertificateImportFailure.CaCertificate, CertificateErrorCodes.ImportCaCertificate)]
    public async Task ARejectedUploadIsInvalidWithTheReasonAndLeavesNothing(CertificateImportFailure reason, string code)
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, certificates, vault) = Create(harness);
        vault.ImportFailure = new CertificateImportException(reason, "nope");

        var result = await operations.UploadAsync(Upload(), default, null, null, ConfigServiceHarness.Admin, Ct);

        Assert.Equal((CertificateOperationStatus.Invalid, code), (result.Status, Assert.Single(result.Errors).Code));
        Assert.Empty(await certificates.ListAsync(cancellationToken: Ct));
        Assert.Empty(vault.Keys);
    }

    [Fact]
    public async Task AnUploadOverAnotherSourceOrAMissingIdIsRefusedBeforeTheVaultIsTouched()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, _, vault) = Create(harness);
        var selfSigned = (await operations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["a.example.com"]), ConfigServiceHarness.Admin, Ct)).Certificate!;
        vault.Calls.Clear();

        var wrongSource = await operations.UploadAsync(Upload(), default, null, selfSigned.Id, ConfigServiceHarness.Admin, Ct);
        var missing = await operations.UploadAsync(Upload(), default, null, CertificateId.New(), ConfigServiceHarness.Admin, Ct);

        Assert.Equal((CertificateOperationStatus.Invalid, CertificateErrorCodes.NotReplaceable), (wrongSource.Status, Assert.Single(wrongSource.Errors).Code));
        Assert.Equal(CertificateOperationStatus.NotFound, missing.Status);
        Assert.Empty(vault.Calls);
    }

    [Fact]
    public async Task IfRecordingAnUploadFailsTheNewKeyIsTakenBack()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, certificates, vault) = Create(harness);
        vault.ImportNames = [];

        var result = await operations.UploadAsync(Upload(), default, null, null, ConfigServiceHarness.Admin, Ct);

        // The vault accepted a certificate whose names the inventory cannot use: nothing stays behind.
        Assert.Equal(CertificateOperationStatus.Invalid, result.Status);
        Assert.Empty(vault.Keys);
        Assert.Empty(await certificates.ListAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task ReissueKeepsTheIdAndTheNamesAndSwapsTheCertificate()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, _, vault) = Create(harness);
        var first = (await operations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["a.example.com", "*.a.example.com"], KeySpec.EcdsaP384), ConfigServiceHarness.Admin, Ct)).Certificate!;

        var result = await operations.ReissueAsync(first.Id, null, ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateOperationStatus.Completed, result.Status);
        Assert.Equal(first.Id, result.Certificate!.Id);
        Assert.NotEqual(first.Sha256Fingerprint, result.Certificate.Sha256Fingerprint);
        Assert.Equal(first.Names, result.Certificate.Names);
        Assert.Equal(KeySpec.EcdsaP384, vault.LastSpec);
        Assert.Equal(["create:SelfSigned", "create:SelfSigned"], vault.Calls);
    }

    [Fact]
    public async Task ReissueIsOnlyForSelfSignedAndLocalCaCertificates()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, _, vault) = Create(harness);
        vault.ImportNames = ["u.example.com"];
        var uploaded = (await operations.UploadAsync(Upload(), default, null, null, ConfigServiceHarness.Admin, Ct)).Certificate!;
        vault.Calls.Clear();

        var refused = await operations.ReissueAsync(uploaded.Id, null, ConfigServiceHarness.Admin, Ct);
        var missing = await operations.ReissueAsync(CertificateId.New(), null, ConfigServiceHarness.Admin, Ct);

        Assert.Equal((CertificateOperationStatus.Invalid, CertificateErrorCodes.NotReissuable), (refused.Status, Assert.Single(refused.Errors).Code));
        Assert.Equal(CertificateOperationStatus.NotFound, missing.Status);
        Assert.Empty(vault.Calls);
    }

    [Fact]
    public async Task DeleteIsRefusedWhileASpecificBindingUsesItBeforeTheVaultKeyIsDeleted()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, certificates, vault) = Create(harness);
        var cert = (await operations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["a.example.com"]), ConfigServiceHarness.Admin, Ct)).Certificate!;
        var bound = Objects.RespondHost(HostId.New(), "a.example.com") with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = cert.Id } };
        await harness.RunAsync(new UpsertHostCommand(bound));

        var refused = await operations.DeleteAsync(cert.Id, ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateOperationStatus.Invalid, refused.Status);
        Assert.Equal(CertificateErrorCodes.InUse, Assert.Single(refused.Errors).Code);
        Assert.Contains(cert.Id, vault.Keys);
        Assert.DoesNotContain("delete", vault.Calls);
        Assert.NotNull(await certificates.GetAsync(cert.Id, Ct));
    }

    [Fact]
    public async Task DeleteRemovesTheRowAndThenTheKey()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, certificates, vault) = Create(harness);
        var cert = (await operations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["a.example.com"]), ConfigServiceHarness.Admin, Ct)).Certificate!;
        vault.OnDelete = async id => Assert.Null(await certificates.GetAsync(id, Ct));

        var result = await operations.DeleteAsync(cert.Id, ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateOperationStatus.Completed, result.Status);
        Assert.Empty(vault.Keys);
        Assert.Null(await certificates.GetAsync(cert.Id, Ct));
    }

    [Fact]
    public async Task AFailedKeyDeletionIsAWarningNotAnError()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, certificates, vault) = Create(harness);
        var cert = (await operations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["a.example.com"]), ConfigServiceHarness.Admin, Ct)).Certificate!;
        vault.DeleteFailure = new IOException("locked");

        var result = await operations.DeleteAsync(cert.Id, ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateOperationStatus.Completed, result.Status);
        Assert.Equal(CertificateErrorCodes.KeyNotDeleted, Assert.Single(result.Warnings).Code);
        Assert.Null(await certificates.GetAsync(cert.Id, Ct));
        Assert.Contains(cert.Id, vault.Keys);
    }

    [Fact]
    public async Task ADeleteWithoutARowStillRemovesAnOrphanedKey()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, _, vault) = Create(harness);
        var orphan = CertificateId.New();
        vault.Keys.Add(orphan);

        Assert.Equal(CertificateOperationStatus.Completed, (await operations.DeleteAsync(orphan, ConfigServiceHarness.Admin, Ct)).Status);
        Assert.Equal(CertificateOperationStatus.NotFound, (await operations.DeleteAsync(orphan, ConfigServiceHarness.Admin, Ct)).Status);
        Assert.Empty(vault.Keys);
    }

    [Fact]
    public async Task AnUnauthorizedOriginChangesNothing()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, certificates, vault) = Create(harness, new Deny());

        Assert.Equal(CertificateOperationStatus.Forbidden, (await operations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["a.example.com"]), ConfigServiceHarness.Admin, Ct)).Status);
        Assert.Equal(CertificateOperationStatus.Forbidden, (await operations.UploadAsync(Upload(), default, null, null, ConfigServiceHarness.Admin, Ct)).Status);
        Assert.Equal(CertificateOperationStatus.Forbidden, (await operations.EnsureLocalCaAsync(new LocalCaRequest(), ConfigServiceHarness.Admin, Ct)).Status);
        Assert.Equal(CertificateOperationStatus.Forbidden, (await operations.DeleteAsync(CertificateId.New(), ConfigServiceHarness.Admin, Ct)).Status);
        Assert.Empty(vault.Calls);
        Assert.Empty(await certificates.ListAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task AnUploadWithATooLongLabelIsRefusedBeforeTheVaultChangesAnything()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, _, vault) = Create(harness);
        vault.ImportNames = ["site.example.com"];
        var first = (await operations.UploadAsync(Upload(), default, null, null, ConfigServiceHarness.Admin, Ct)).Certificate!;
        vault.Calls.Clear();

        var replace = await operations.UploadAsync(Upload("v2"), default, new string('x', CertificateService.MaxLabelLength + 1), first.Id, ConfigServiceHarness.Admin, Ct);
        var fresh = await operations.UploadAsync(Upload("v3"), default, new string('x', CertificateService.MaxLabelLength + 1), null, ConfigServiceHarness.Admin, Ct);

        Assert.Equal((CertificateOperationStatus.Invalid, CertificateErrorCodes.TextTooLong), (replace.Status, Assert.Single(replace.Errors).Code));
        Assert.Equal(CertificateOperationStatus.Invalid, fresh.Status);
        Assert.Empty(vault.Calls);
    }

    [Fact]
    public async Task ARootRowACrashLeftPendingIsCompletedUnderItsOwnId()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, certificates, vault) = Create(harness);
        var id = CertificateId.New();
        await certificates.RegisterPendingAsync(id, CertificateSource.LocalCaRoot, [], null, ConfigServiceHarness.Admin, Ct);

        var result = await operations.EnsureLocalCaAsync(new LocalCaRequest(), ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateOperationStatus.Completed, result.Status);
        Assert.Equal(id, result.Certificate!.Id);
        Assert.Equal(CertificateStatus.Active, result.Certificate.Status);
        Assert.Contains(id, vault.Keys);
        Assert.Single(await certificates.ListAsync(cancellationToken: Ct));

        // And now it is the one local CA.
        Assert.Equal(CertificateOperationStatus.Unchanged, (await operations.EnsureLocalCaAsync(new LocalCaRequest(), ConfigServiceHarness.Admin, Ct)).Status);
    }

    [Fact]
    public async Task ARootTheVaultHoldsUnderAnotherIdIsRecordedUnderThatId()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, certificates, vault) = Create(harness);
        var real = CertificateId.New();
        vault.Keys.Add(real);
        vault.ExistingRoot = (real, vault.RootInfo());

        var result = await operations.EnsureLocalCaAsync(new LocalCaRequest(), ConfigServiceHarness.Admin, Ct);

        Assert.Equal(CertificateOperationStatus.Completed, result.Status);
        Assert.Equal(real, result.Certificate!.Id);
        Assert.Equal(CertificateSource.LocalCaRoot, result.Certificate.Source);
        Assert.Equal([real], (await certificates.ListAsync(cancellationToken: Ct)).Select(c => c.Id));
        Assert.Equal([real], vault.Keys);
    }

    [Fact]
    public async Task AVaultCallThatFailsAfterStoringTheKeyDoesNotLeaveIt()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, certificates, vault) = Create(harness);
        vault.StoreThenFail = new IOException("died after the write");

        await Assert.ThrowsAsync<IOException>(
            async () => await operations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["a.example.com"]), ConfigServiceHarness.Admin, Ct));

        Assert.Empty(vault.Keys);
        Assert.Empty(await certificates.ListAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task TheKeyOfADeletedCertificateIsDeletedEvenIfTheRequestIsCancelled()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        var (operations, _, vault) = Create(harness);
        var cert = (await operations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["a.example.com"]), ConfigServiceHarness.Admin, Ct)).Certificate!;

        await operations.DeleteAsync(cert.Id, ConfigServiceHarness.Admin, Ct);

        Assert.False(vault.LastDeleteToken.CanBeCanceled);
    }

    private sealed class Allow : ICertificateOperationAuthorizer
    {
        public ValueTask<bool> IsAllowedAsync(CertificateOperation operation, CommandOrigin origin, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class Deny : ICertificateOperationAuthorizer
    {
        public ValueTask<bool> IsAllowedAsync(CertificateOperation operation, CommandOrigin origin, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }

    /// <summary>A vault that remembers which IDs hold a key and can be made to fail; it makes CertificateInfo with a fresh fingerprint each time.</summary>
    private sealed class FakeVault : ICertificateIssuance, ICertificateImport
    {
        private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow.AddMinutes(-5);

        private int _serial;

        public HashSet<CertificateId> Keys { get; } = [];

        public List<string> Calls { get; } = [];

        public Exception? CreateFailure { get; set; }

        public Exception? DeleteFailure { get; set; }

        public CertificateImportException? ImportFailure { get; set; }

        public IReadOnlyList<string> ImportNames { get; set; } = ["upload.example.com"];

        public Func<CertificateInfo, CertificateInfo>? InfoOverride { get; set; }

        public Func<CertificateId, Task>? OnCreate { get; set; }

        public (CertificateId Id, CertificateInfo Info)? ExistingRoot { get; set; }

        public Exception? StoreThenFail { get; set; }

        public CancellationToken LastDeleteToken { get; private set; }

        public CertificateInfo RootInfo() => InfoFor([], null);

        public Func<CertificateId, Task>? OnDelete { get; set; }

        public KeySpec LastSpec { get; private set; }

        public TimeSpan LastValidity { get; private set; }

        public string? LastPassword { get; private set; }

        public ValueTask<CsrResult> CreateKeyAndCsrAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<CertificateInfo> AttachChainAsync(CertificateId id, StagedKeyId stagedKey, ReadOnlyMemory<byte> pemChain, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<CertificateInfo> CreateSelfSignedAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, TimeSpan validity, CancellationToken cancellationToken) =>
            Create("SelfSigned", id, spec, names, validity);

        public ValueTask<CertificateInfo> EnsureLocalCaAsync(CertificateId id, KeySpec spec, TimeSpan validity, CancellationToken cancellationToken) =>
            Create("LocalCaRoot", id, spec, [], validity);

        public ValueTask<CertificateInfo> IssueFromLocalCaAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, TimeSpan validity, CancellationToken cancellationToken) =>
            Create("LocalCa", id, spec, names, validity);

        public async ValueTask<bool> DeleteAsync(CertificateId id, CancellationToken cancellationToken)
        {
            Calls.Add("delete");
            LastDeleteToken = cancellationToken;
            if (OnDelete is not null)
            {
                await OnDelete(id);
            }

            if (DeleteFailure is not null)
            {
                throw DeleteFailure;
            }

            return Keys.Remove(id);
        }

        public ValueTask<CertificateInfo> ImportAsync(CertificateId id, Stream pemOrPfx, ReadOnlyMemory<char> password, CancellationToken cancellationToken)
        {
            Calls.Add("import");
            LastPassword = password.ToString();
            if (ImportFailure is not null)
            {
                throw ImportFailure;
            }

            Keys.Add(id);
            return ValueTask.FromResult(InfoFor(ImportNames));
        }

        private async ValueTask<CertificateInfo> Create(string kind, CertificateId id, KeySpec spec, IReadOnlyList<string> names, TimeSpan validity)
        {
            Calls.Add("create:" + kind);
            if (kind == "LocalCaRoot" && ExistingRoot is { } existing && existing.Id != id)
            {
                throw new LocalCaExistsException(existing.Id, existing.Info);
            }

            LastSpec = spec;
            LastValidity = validity;
            if (OnCreate is not null)
            {
                await OnCreate(id);
            }

            if (CreateFailure is not null)
            {
                throw CreateFailure;
            }

            Keys.Add(id);
            if (StoreThenFail is not null)
            {
                throw StoreThenFail;
            }

            return InfoFor(names, validity, spec);
        }

        private CertificateInfo InfoFor(IReadOnlyList<string> names, TimeSpan? validity = null, KeySpec spec = KeySpec.EcdsaP256)
        {
            var serial = Interlocked.Increment(ref _serial);
            CertificateInfo info = new(
                names.Count > 0 ? $"CN={names[0]}" : "CN=Fake Local CA",
                names.Select(n => n.ToLowerInvariant()).ToList(),
                Start,
                Start + (validity ?? TimeSpan.FromDays(90)),
                "CN=Fake Issuer",
                serial.ToString("X2", System.Globalization.CultureInfo.InvariantCulture),
                null,
                $"fp-{serial}",
                spec.ToName() switch { "ecdsa-p384" => "ECDSA P-384", "rsa-2048" => "RSA 2048", _ => "ECDSA P-256" },
                ["CN=Fake Issuer"],
                "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n");
            return InfoOverride?.Invoke(info) ?? info;
        }
    }
}
