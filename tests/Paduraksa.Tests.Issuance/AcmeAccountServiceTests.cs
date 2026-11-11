using System.Net;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Acme;
using Paduraksa.Application.Issuance;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Acme.Fakes;

using static Paduraksa.Tests.Data.Config.TestConfigDatabase;
using static Paduraksa.Tests.Issuance.IssuanceHarness;

namespace Paduraksa.Tests.Issuance;

public sealed class AcmeAccountServiceTests
{
    private static CreateAcmeAccountRequest Request(string directory = "https://acme.test/directory") =>
        new(directory, ["mailto:ops@example.com"], TermsOfServiceAgreed: true);

    [Fact]
    public async Task CreatingAnAccountMakesTheVaultKeyRegistersItAndRecordsIt()
    {
        await using var h = await IssuanceHarness.CreateAsync();

        var result = await h.Accounts.CreateAsync(Request() with { Label = "  Let's Encrypt  " }, Admin, Ct);

        Assert.Equal(IssuanceChangeStatus.Committed, result.Status);
        var account = result.Account!;
        Assert.Equal(("https://acme.test/directory", AcmeAccountStatus.Active, JwsAlgorithm.ES256, "Let's Encrypt"), (account.DirectoryUrl, account.Status, account.KeyAlgorithm, account.Label));
        Assert.StartsWith("https://acme.test/acct/", account.AccountUrl, StringComparison.Ordinal);
        Assert.Equal(["mailto:ops@example.com"], account.Contacts);
        Assert.True(account.TermsOfServiceAgreed);
        // The key exists in the vault under the account's ID, and the CA saw terms agreed.
        Assert.Equal(account.Id, Assert.Single(h.Signer.Created));
        Assert.NotNull(await h.Signer.GetAccountKeyAsync(account.Id, Ct));
        Assert.True(h.Server.To("new-account").Single().PayloadJson!["termsOfServiceAgreed"]!.GetValue<bool>());
        var audit = Assert.Single(await h.AuditAsync("acme.account.create"));
        Assert.Equal((AuditOutcome.Success, account.Id.ToString()), (audit.Outcome, audit.Target));
        Assert.Equal(account.Id, Assert.Single(await h.Accounts.ListAsync(Ct)).Id);
        Assert.Equal(account.AccountUrl, (await h.Accounts.GetAsync(account.Id, Ct))!.AccountUrl);
        Assert.Null(await h.Accounts.GetAsync(AcmeAccountId.New(), Ct));
        await h.Config.AssertChainIntactAsync();
    }

    [Fact]
    public async Task ARefusedRegistrationDeletesTheKeyAndRecordsNothing()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        h.Server.Inject("new-account", _ => FakeAcmeServer.Problem(HttpStatusCode.Forbidden, AcmeProblemTypes.UserActionRequired, "agree to the new terms"));

        var result = await h.Accounts.CreateAsync(Request(), Admin, Ct);

        Assert.Equal(IssuanceChangeStatus.Failed, result.Status);
        Assert.Contains("userActionRequired", result.Message, StringComparison.Ordinal);
        Assert.Contains("agree to the new terms", result.Message, StringComparison.Ordinal);
        Assert.Equal(h.Signer.Created, h.Signer.Deleted);
        Assert.Null(await h.Signer.GetAccountKeyAsync(h.Signer.Created[0], Ct));
        Assert.Empty(await h.Accounts.ListAsync(Ct));
        Assert.Empty(await h.AuditAsync("acme.account.create"));
    }

    [Fact]
    public async Task ExternalAccountBindingGoesThroughTheVaultsMacKey()
    {
        var server = new FakeAcmeServer { ExternalAccountRequired = true };
        await using var h = await IssuanceHarness.CreateAsync(server);
        var secret = SecretId.New();
        var macKeys = new FakeMacKeySource(secret, new FakeMacKey(server.EabSecret));
        var accounts = new AcmeAccountService(
            h.Config.Db.Factory,
            h.Config.Service,
            new AcmeClientProvider(h.Signer, macKeys, new AcmeClientOptions { RequestTimeout = TimeSpan.FromDays(1) }, () => h.Hook, h.Time),
            h.Signer,
            macKeys,
            h.Time,
            NullLogger<AcmeAccountService>.Instance,
            h.Permissions);

        var bound = await accounts.CreateAsync(Request() with { EabKid = "kid-1", EabSecretId = secret }, Admin, Ct);

        Assert.Equal(IssuanceChangeStatus.Committed, bound.Status);
        Assert.Equal("kid-1", bound.Account!.EabKid);
        Assert.Equal(secret, await h.EabSecretOfAsync(bound.Account.Id));
        Assert.NotNull(h.Server.To("new-account").Last().PayloadJson!["externalAccountBinding"]);

        // The CA that needs a binding refuses an account without one, and the key made for the try is deleted.
        var unbound = await accounts.CreateAsync(Request(), Admin, Ct);
        Assert.Equal(IssuanceChangeStatus.Failed, unbound.Status);
        Assert.Equal(2, h.Signer.Created.Count);
        Assert.Equal(h.Signer.Created[1], Assert.Single(h.Signer.Deleted));
    }

    private static AcmeAccountService WithSecrets(IssuanceHarness h, IMacKeySource macKeys) => new(
        h.Config.Db.Factory,
        h.Config.Service,
        new AcmeClientProvider(h.Signer, macKeys, new AcmeClientOptions { RequestTimeout = TimeSpan.FromDays(1) }, () => h.Hook, h.Time),
        h.Signer,
        macKeys,
        h.Time,
        NullLogger<AcmeAccountService>.Instance,
        h.Permissions,
        secrets: h.Secrets);

    private sealed class AnyEabKeys(IMacKey key) : IMacKeySource
    {
        public ValueTask<IMacKey?> OpenAsync(SecretId requested, SecretKind expected, CancellationToken cancellationToken) =>
            ValueTask.FromResult(expected == SecretKind.EabHmacKey ? key : null);
    }

    [Fact]
    public async Task CreateWithEabStoresNothingForARequestThatCannotWork()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var accounts = WithSecrets(h, new AnyEabKeys(new FakeMacKey(new byte[32])));
        byte[] key = [1, 2, 3, 4];

        (CreateAcmeAccountRequest Request, byte[] Key, string Code)[] cases =
        [
            (Request("http://acme.test/directory") with { EabKid = "kid" }, key, IssuanceErrorCodes.DirectoryInvalid), // a valid binding does not excuse a bad directory
            (Request() with { EabKid = "kid" }, [], IssuanceErrorCodes.EabIncomplete),
            (Request(), key, IssuanceErrorCodes.EabIncomplete),
            (Request() with { EabKid = "kid" }, new byte[AcmeAccountService.MaxEabKeyLength + 1], IssuanceErrorCodes.TextTooLong),
        ];
        foreach (var (request, bytes, code) in cases)
        {
            var result = await accounts.CreateWithEabAsync(request, bytes, Admin, Ct);

            Assert.Equal(IssuanceChangeStatus.Invalid, result.Status);
            Assert.Equal(code, Assert.Single(result.Errors).Code);
        }

        Assert.Equal(0, h.Secrets.Puts);
        Assert.Empty(h.Signer.Created);
    }

    [Fact]
    public async Task CreateWithEabKeepsTheKeyInTheVaultWhenTheAccountIsCreatedAndDeletesItWhenTheCaRefuses()
    {
        var server = new FakeAcmeServer { ExternalAccountRequired = true };
        await using var h = await IssuanceHarness.CreateAsync(server);

        var accepted = await WithSecrets(h, new AnyEabKeys(new FakeMacKey(server.EabSecret))).CreateWithEabAsync(Request() with { EabKid = "kid-1" }, new byte[] { 9, 9, 9 }, Admin, Ct);
        Assert.Equal(IssuanceChangeStatus.Committed, accepted.Status);
        var stored = (await h.Secrets.DescribeAsync((await h.EabSecretOfAsync(accepted.Account!.Id))!.Value, Ct))!;
        Assert.Equal(SecretKind.EabHmacKey, stored.Kind);
        Assert.Equal(MacAlgorithm.HmacSha256, stored.MacAlgorithm);

        // A binding the CA rejects: the account isn't created and the secret stored for it is gone again.
        var refused = await WithSecrets(h, new AnyEabKeys(new FakeMacKey(new byte[32]))).CreateWithEabAsync(Request() with { EabKid = "kid-2" }, new byte[] { 1, 2, 3 }, Admin, Ct);
        Assert.Equal(IssuanceChangeStatus.Failed, refused.Status);
        Assert.Equal(2, h.Secrets.Puts);
        Assert.Equal(1, h.Secrets.Count);
    }

    [Fact]
    public async Task RequestsThatCannotWorkAreRefusedBeforeAnyKeyIsMade()
    {
        await using var h = await IssuanceHarness.CreateAsync();

        (CreateAcmeAccountRequest Request, string Code)[] cases =
        [
            (Request("http://acme.test/directory"), IssuanceErrorCodes.DirectoryInvalid),
            (Request("not a url"), IssuanceErrorCodes.DirectoryInvalid),
            (Request() with { Contacts = ["ops@example.com"] }, IssuanceErrorCodes.ContactInvalid),
            (Request() with { Contacts = [.. Enumerable.Range(0, 6).Select(i => $"mailto:a{i}@example.com")] }, IssuanceErrorCodes.ContactInvalid),
            (Request() with { Label = new string('x', 257) }, IssuanceErrorCodes.TextTooLong),
            (Request() with { Algorithm = (JwsAlgorithm)99 }, IssuanceErrorCodes.AlgorithmUndefined),
            (Request() with { EabKid = "kid" }, IssuanceErrorCodes.EabIncomplete),
            (Request() with { EabKid = "kid", EabSecretId = SecretId.New() }, IssuanceErrorCodes.EabSecretMissing),
        ];
        foreach (var (request, code) in cases)
        {
            var result = await h.Accounts.CreateAsync(request, Admin, Ct);

            Assert.Equal(IssuanceChangeStatus.Invalid, result.Status);
            Assert.Equal(code, Assert.Single(result.Errors).Code);
        }

        Assert.Empty(h.Signer.Created);
        Assert.Empty(await h.Accounts.ListAsync(Ct));
    }

    [Fact]
    public async Task DeactivatingIsFinalDeletesTheKeyAndIsIdempotent()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();

        var result = await h.Accounts.DeactivateAsync(account.Id, Admin, Ct);

        Assert.Equal(IssuanceChangeStatus.Committed, result.Status);
        Assert.Equal(AcmeAccountStatus.Deactivated, result.Account!.Status);
        Assert.Equal(account.Id, Assert.Single(h.Signer.Deleted));
        Assert.Null(await h.Signer.GetAccountKeyAsync(account.Id, Ct));
        Assert.Single(await h.AuditAsync("acme.account.deactivate"));

        var again = await h.Accounts.DeactivateAsync(account.Id, Admin, Ct);
        Assert.Equal((IssuanceChangeStatus.Committed, AcmeAccountStatus.Deactivated), (again.Status, again.Account!.Status));
        Assert.Single(await h.AuditAsync("acme.account.deactivate"));
        Assert.Equal(IssuanceChangeStatus.NotFound, (await h.Accounts.DeactivateAsync(AcmeAccountId.New(), Admin, Ct)).Status);
    }

    [Fact]
    public async Task DeactivatingAnAccountTheCaAlreadyDeactivatedStillSucceeds()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        // A deactivation that worked at the CA but was not recorded here: the CA now refuses the account's signatures.
        h.Server.Inject(new Uri(account.AccountUrl).AbsolutePath.TrimStart('/'), _ => FakeAcmeServer.Problem(HttpStatusCode.Unauthorized, AcmeProblemTypes.Prefix + "unauthorized", "the account is deactivated"));

        var result = await h.Accounts.DeactivateAsync(account.Id, Admin, Ct);

        Assert.Equal((IssuanceChangeStatus.Committed, AcmeAccountStatus.Deactivated), (result.Status, result.Account!.Status));
    }

    [Fact]
    public async Task ARowHoldsNoKeyMaterialOnlyReferences()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        _ = await h.CreateAccountAsync();

        var columns = (await h.Config.Db.QueryAsync("SELECT name FROM pragma_table_info('AcmeAccounts')")).Select(r => (string)r[0]!).ToList();

        Assert.Contains("EabSecretId", columns);
        Assert.DoesNotContain(columns, c => c.Contains("Private", StringComparison.OrdinalIgnoreCase) || c.Contains("Jwk", StringComparison.OrdinalIgnoreCase));
        await using var context = h.Config.Db.NewContext();
        Assert.Equal(1, await context.AcmeAccounts.CountAsync(Ct));
    }
}
