using Paduraksa.Acme;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Acme.Fakes;
using static Paduraksa.Tests.Acme.Fakes.AcmeHarness;

namespace Paduraksa.Tests.Acme;

public sealed class AcmeAccountTests
{
    [Fact]
    public async Task DirectoryExposesTheUrlsAndMeta()
    {
        using var h = new AcmeHarness();

        var directory = await h.Client.GetDirectoryAsync(Token);

        Assert.Equal(FakeAcmeServer.Url("new-order"), directory.NewOrder);
        Assert.Equal(FakeAcmeServer.Url("renewal-info"), directory.RenewalInfo);
        Assert.Equal(FakeAcmeServer.Url("terms"), directory.Meta!.TermsOfService);
        Assert.False(directory.Meta.ExternalAccountRequired);
        Assert.Equal("Six days", directory.Meta.Profiles!["shortlived"]);
    }

    [Fact]
    public async Task DirectoryIsFetchedOnceWhileFresh()
    {
        using var h = new AcmeHarness();

        await h.Client.GetDirectoryAsync(Token);
        await h.Client.GetDirectoryAsync(Token);

        Assert.Single(h.Server.To("directory"));
    }

    [Fact]
    public async Task NewAccountIsSignedWithTheJwkAndLaterRequestsWithTheKid()
    {
        using var h = new AcmeHarness();

        var account = await h.RegisterAsync();
        await h.Client.GetAccountAsync(account.Ref, Token);

        Assert.True(account.Created);
        Assert.Equal(AcmeStatus.Valid, account.Status);
        Assert.Equal(["mailto:ops@example.com"], account.Contacts);
        var create = Assert.Single(h.Server.To("new-account"));
        Assert.True(create.HasHeader("jwk"));
        Assert.False(create.HasHeader("kid"));
        Assert.True(create.PayloadJson!["termsOfServiceAgreed"]!.GetValue<bool>());
        Assert.False(create.PayloadJson.AsObject().ContainsKey("externalAccountBinding"));
        var read = Assert.Single(h.Server.To("acct/1"));
        Assert.Equal(account.Url.AbsoluteUri, read.HeaderString("kid"));
        Assert.False(read.HasHeader("jwk"));
        Assert.Equal(string.Empty, read.Payload);
    }

    [Fact]
    public async Task RegisteringAnExistingKeyReturnsTheAccountWithoutCreatedFlag()
    {
        using var h = new AcmeHarness();
        var id = AcmeAccountId.New();
        var first = await h.RegisterAsync(id);

        var second = await h.RegisterAsync(id);

        Assert.False(second.Created);
        Assert.Equal(first.Url, second.Url);
    }

    [Fact]
    public async Task FindAccountReturnsNullForAnUnknownKey()
    {
        using var h = new AcmeHarness();
        var id = AcmeAccountId.New();
        await h.Signer.CreateAccountKeyAsync(id, Paduraksa.Core.Vault.JwsAlgorithm.ES256, Token);

        Assert.Null(await h.Client.FindAccountAsync(id, Token));

        var registered = await h.Client.RegisterAccountAsync(id, new AcmeNewAccountRequest([], TermsOfServiceAgreed: true), Token);
        var found = await h.Client.FindAccountAsync(id, Token);

        Assert.Equal(registered.Url, found!.Url);
        Assert.True(h.Server.To("new-account").Last().PayloadJson!["onlyReturnExisting"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ExternalAccountBindingIsMacedByTheVaultKeyAndAcceptedByTheCa()
    {
        var server = new FakeAcmeServer { ExternalAccountRequired = true, EabKeyId = "zerossl-kid" };
        using var h = new AcmeHarness(server: server);

        var account = await h.RegisterAsync(request: new AcmeNewAccountRequest(
            ["mailto:ops@example.com"], true, new AcmeExternalAccountBinding("zerossl-kid", h.EabSecretId)));

        Assert.True(account.Created);
        var binding = h.Server.To("new-account").Single().PayloadJson!["externalAccountBinding"]!;
        Assert.NotNull(binding["signature"]);
    }

    [Fact]
    public async Task ExternalAccountBindingWithTheWrongKeyIsRefusedByTheCa()
    {
        var server = new FakeAcmeServer { ExternalAccountRequired = true, EabKeyId = "right-kid" };
        using var h = new AcmeHarness(server: server);

        var ex = await Assert.ThrowsAsync<AcmeProblemException>(() => h.RegisterAsync(request: new AcmeNewAccountRequest(
            [], true, new AcmeExternalAccountBinding("wrong-kid", h.EabSecretId))));

        Assert.Equal(AcmeProblemTypes.ExternalAccountRequired, ex.Type);
        Assert.Equal(AcmeFailureKind.Permanent, ex.Kind);
    }

    [Fact]
    public async Task ExternalAccountBindingNeedsAMacKeyThatIsInTheVault()
    {
        using var h = new AcmeHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.RegisterAsync(request: new AcmeNewAccountRequest(
            [], true, new AcmeExternalAccountBinding("kid", SecretId.New()))));
        Assert.Empty(h.Server.To("new-account"));
    }

    [Fact]
    public async Task UpdateContactsAndDeactivateUseTheKid()
    {
        using var h = new AcmeHarness();
        var account = await h.RegisterAsync();

        var updated = await h.Client.UpdateContactsAsync(account.Ref, ["mailto:new@example.com"], Token);
        var deactivated = await h.Client.DeactivateAccountAsync(account.Ref, Token);

        Assert.Equal(["mailto:new@example.com"], updated.Contacts);
        Assert.Equal(AcmeStatus.Deactivated, deactivated.Status);
        var requests = h.Server.To("acct/1").ToList();
        Assert.All(requests, r => Assert.Equal(account.Url.AbsoluteUri, r.HeaderString("kid")));
        Assert.Equal("deactivated", requests[1].PayloadJson!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task AccountWithoutAVaultKeyIsACallerError()
    {
        using var h = new AcmeHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Client.RegisterAccountAsync(AcmeAccountId.New(), new AcmeNewAccountRequest([], true), Token));
    }

    [Fact]
    public async Task EverySignatureVerifiesAgainstTheAccountKey()
    {
        // The fake CA rejects a bad signature, a wrong url header or a stale nonce with a problem, so a full flow that
        // completes means every JWS the client built was valid. This pins that it did sign.
        using var h = new AcmeHarness();

        var account = await h.RegisterAsync();
        await h.Client.GetAccountAsync(account.Ref, Token);

        Assert.Equal(2, h.Signer.SignCount);
        Assert.All(h.Server.Requests.Where(r => r.Method == "POST"), r => Assert.Equal("ES256", r.HeaderString("alg")));
    }
}
