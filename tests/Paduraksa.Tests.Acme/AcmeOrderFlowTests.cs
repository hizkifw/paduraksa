using System.Buffers.Text;
using System.Net;
using Paduraksa.Acme;
using Paduraksa.Tests.Acme.Fakes;
using static Paduraksa.Tests.Acme.Fakes.AcmeHarness;

namespace Paduraksa.Tests.Acme;

/// <summary>Orders, challenges, polling, finalization, download and revocation end to end against the fake CA.</summary>
public sealed class AcmeOrderFlowTests
{
    private static readonly AcmeIdentifier[] Identifiers =
        [AcmeIdentifier.Dns("example.com"), AcmeIdentifier.Dns("*.example.com"), AcmeIdentifier.Ip(IPAddress.Parse("192.0.2.7"))];

    [Fact]
    public async Task FullIssuanceFlow()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;

        var order = await h.Client.CreateOrderAsync(account, new AcmeNewOrderRequest(Identifiers, Profile: "shortlived"), Token);

        Assert.Equal(AcmeStatus.Pending, order.Status);
        Assert.Equal(3, order.Authorizations.Count);
        Assert.Equal("shortlived", order.Profile);
        var sent = h.Server.To("new-order").Single().PayloadJson!;
        Assert.Equal("*.example.com", sent["identifiers"]![1]!["value"]!.GetValue<string>());
        Assert.Equal("ip", sent["identifiers"]![2]!["type"]!.GetValue<string>());
        Assert.Equal("shortlived", sent["profile"]!.GetValue<string>());
        Assert.Null(sent["replaces"]);

        var expectedThumbprint = (await h.Signer.GetAccountKeyAsync(account.Id, Token))!.Thumbprint;
        var authorizations = new List<AcmeAuthorization>();
        foreach (var authzUrl in order.Authorizations)
        {
            var authz = await h.Client.GetAuthorizationAsync(account, authzUrl, Token);
            var challenge = authz.SelectChallenge([AcmeChallenges.Dns01, AcmeChallenges.Http01])!;
            Assert.Equal(AcmeChallenges.Dns01, challenge.Type);
            authorizations.Add(authz);

            var keyAuthorization = await h.Client.GetKeyAuthorizationAsync(account.Id, challenge, Token);
            Assert.Equal($"{challenge.Token}.{expectedThumbprint}", keyAuthorization);

            await h.Client.RespondToChallengeAsync(account, challenge, Token);
            var done = await h.Client.WaitForAuthorizationAsync(account, authzUrl, Token);
            Assert.Equal(AcmeStatus.Valid, done.Status);
        }

        Assert.Equal(["example.com", "example.com", "192.0.2.7"], authorizations.Select(a => a.Identifier.Value));
        Assert.Equal([false, true, false], authorizations.Select(a => a.Wildcard));

        var ready = await h.Client.WaitForOrderReadyAsync(account, order.Url, Token);
        Assert.Equal(AcmeStatus.Ready, ready.Status);

        byte[] csr = [0x30, 0x03, 0x02, 0x01, 0x00];
        var finalizing = await h.Client.FinalizeOrderAsync(account, ready, csr, Token);
        Assert.Equal(AcmeStatus.Processing, finalizing.Status);
        Assert.Equal(csr, h.Server.CsrOf(0));

        var issued = await h.Client.WaitForOrderIssuedAsync(account, order.Url, Token);
        Assert.Equal(AcmeStatus.Valid, issued.Status);
        var download = await h.Client.DownloadCertificateAsync(account, issued.Certificate!, null, Token);

        Assert.Equal(AcmePem.CertificateDers(FakeAcmeServer.DefaultChainPem), AcmePem.CertificateDers(download.PemChain));
        Assert.Equal(["E5", "ISRG Root X2", "ISRG Root X1"], download.IssuerCommonNames);
        Assert.Equal([FakeAcmeServer.Url("cert/" + order.Url.Segments[^1] + "-alt")], download.Alternates);

        var request = h.Server.To("cert/" + order.Url.Segments[^1]).Single();
        Assert.Equal("application/pem-certificate-chain", request.Accept);
        Assert.StartsWith("Paduraksa-Acme/", request.UserAgent, StringComparison.Ordinal);
        Assert.Equal("en", request.AcceptLanguage);
        Assert.Equal(string.Empty, request.Payload);
    }

    [Fact]
    public async Task Http01ValueIsServedFromTheKeyAuthorization()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        var order = await h.Client.CreateOrderAsync(account, new AcmeNewOrderRequest([AcmeIdentifier.Dns("example.com")]), Token);

        var authz = await h.Client.GetAuthorizationAsync(account, order.Authorizations[0], Token);
        var challenge = authz.FindChallenge(AcmeChallenges.Http01)!;

        Assert.Equal(h.Server.TokensOf(0)[0], challenge.Token);
        Assert.Equal($"/.well-known/acme-challenge/{challenge.Token}", AcmeChallenges.Http01Path(challenge.Token!));
        Assert.Equal(
            Base64Url.EncodeToString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.ASCII.GetBytes(await h.Client.GetKeyAuthorizationAsync(account.Id, challenge, Token)))),
            AcmeChallenges.Dns01TxtValue(await h.Client.GetKeyAuthorizationAsync(account.Id, challenge, Token)));
    }

    [Fact]
    public async Task ChallengeResponsePayloadIsAnEmptyObject()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        var order = await h.Client.CreateOrderAsync(account, new AcmeNewOrderRequest([AcmeIdentifier.Dns("example.com")]), Token);
        var challenge = (await h.Client.GetAuthorizationAsync(account, order.Authorizations[0], Token)).FindChallenge(AcmeChallenges.Http01)!;

        var answered = await h.Client.RespondToChallengeAsync(account, challenge, Token);

        Assert.Equal(AcmeStatus.Processing, answered.Status);
        Assert.Equal("{}", h.Server.Requests.Last(r => r.Path.StartsWith("chall/", StringComparison.Ordinal)).Payload);
    }

    [Fact]
    public async Task PollingHonoursRetryAfterWithinTheBounds()
    {
        var server = new FakeAcmeServer { RetryAfterSecondsWhilePending = 5, AuthorizationPollsBeforePass = 3 };
        using var h = new AcmeHarness(server: server);
        var account = (await h.RegisterAsync()).Ref;
        var order = await h.Client.CreateOrderAsync(account, new AcmeNewOrderRequest([AcmeIdentifier.Dns("example.com")]), Token);
        var challenge = (await h.Client.GetAuthorizationAsync(account, order.Authorizations[0], Token)).FindChallenge(AcmeChallenges.Dns01)!;
        await h.Client.RespondToChallengeAsync(account, challenge, Token);

        await h.Client.WaitForAuthorizationAsync(account, order.Authorizations[0], Token);

        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], h.Time.Delays);
    }

    [Fact]
    public async Task PollingStopsWhenRetryAfterIsLongerThanTheBoundAndCarriesTheLastState()
    {
        var server = new FakeAcmeServer { RetryAfterSecondsWhilePending = 3600, AuthorizationPollsBeforePass = 1 };
        using var h = new AcmeHarness(server: server);
        var account = (await h.RegisterAsync()).Ref;
        var order = await h.Client.CreateOrderAsync(account, new AcmeNewOrderRequest([AcmeIdentifier.Dns("example.com")]), Token);
        var challenge = (await h.Client.GetAuthorizationAsync(account, order.Authorizations[0], Token)).FindChallenge(AcmeChallenges.Dns01)!;
        await h.Client.RespondToChallengeAsync(account, challenge, Token);

        var ex = await Assert.ThrowsAsync<AcmePollException>(() => h.Client.WaitForAuthorizationAsync(account, order.Authorizations[0], Token));

        Assert.Equal(AcmePollFailure.RetryAfterTooLong, ex.Reason);
        Assert.Equal(TimeSpan.FromHours(1), ex.RetryAfter);
        Assert.Equal(AcmeStatus.Pending, ex.Status);
        Assert.Equal(AcmeStatus.Pending, ex.Authorization!.Status);
        Assert.Equal(order.Authorizations[0], ex.Url);
        Assert.True(ex.IsTransient);
        Assert.Empty(h.Time.Delays);
    }

    [Fact]
    public async Task PollingRaisesAZeroRetryAfterToTheMinimum()
    {
        var server = new FakeAcmeServer { RetryAfterSecondsWhilePending = 0, AuthorizationPollsBeforePass = 1 };
        using var h = new AcmeHarness(server: server);
        var account = (await h.RegisterAsync()).Ref;
        var order = await h.Client.CreateOrderAsync(account, new AcmeNewOrderRequest([AcmeIdentifier.Dns("example.com")]), Token);
        var challenge = (await h.Client.GetAuthorizationAsync(account, order.Authorizations[0], Token)).FindChallenge(AcmeChallenges.Dns01)!;
        await h.Client.RespondToChallengeAsync(account, challenge, Token);

        await h.Client.WaitForAuthorizationAsync(account, order.Authorizations[0], Token);

        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(h.Time.Delays));
    }

    [Fact]
    public async Task PollingUsesTheDefaultIntervalWithoutRetryAfter()
    {
        var server = new FakeAcmeServer { AuthorizationPollsBeforePass = 1 };
        using var h = new AcmeHarness(server: server);
        var account = (await h.RegisterAsync()).Ref;
        var order = await h.Client.CreateOrderAsync(account, new AcmeNewOrderRequest([AcmeIdentifier.Dns("example.com")]), Token);
        var challenge = (await h.Client.GetAuthorizationAsync(account, order.Authorizations[0], Token)).FindChallenge(AcmeChallenges.Dns01)!;
        await h.Client.RespondToChallengeAsync(account, challenge, Token);

        await h.Client.WaitForAuthorizationAsync(account, order.Authorizations[0], Token);

        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(h.Time.Delays));
    }

    [Fact]
    public async Task PollingGivesUpWithAPollErrorThatSaysWhereTheResourceStands()
    {
        // The challenge is never answered, so the authorization stays pending for good.
        using var h = new AcmeHarness(new AcmeClientOptions { PollInterval = TimeSpan.FromSeconds(10), MaxPollDelay = TimeSpan.FromSeconds(10), PollTimeout = TimeSpan.FromSeconds(35) });
        var account = (await h.RegisterAsync()).Ref;
        var order = await h.Client.CreateOrderAsync(account, new AcmeNewOrderRequest([AcmeIdentifier.Dns("example.com")]), Token);

        var ex = await Assert.ThrowsAsync<AcmePollException>(() => h.Client.WaitForAuthorizationAsync(account, order.Authorizations[0], Token));

        Assert.True(ex.IsTransient);
        Assert.Equal(AcmePollFailure.Timeout, ex.Reason);
        Assert.Equal(AcmeStatus.Pending, ex.Authorization!.Status);
        Assert.Equal(3, h.Time.Delays.Count);
    }

    [Fact]
    public async Task FinalizingAnOrderThatIsNotReadyIsAPermanentProblem()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        var order = await h.Client.CreateOrderAsync(account, new AcmeNewOrderRequest([AcmeIdentifier.Dns("example.com")]), Token);

        var ex = await Assert.ThrowsAsync<AcmeProblemException>(() => h.Client.FinalizeOrderAsync(account, order, new byte[] { 1, 2, 3 }, Token));

        Assert.Equal(AcmeProblemTypes.OrderNotReady, ex.Type);
        Assert.Equal(AcmeFailureKind.Permanent, ex.Kind);
    }

    [Fact]
    public async Task PreferredChainMatchesOnlyTheTopCertificatesIssuer()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        var url = await IssueAsync(h, account);

        // Default [leaf, E5, X2 cross-signed by X1] has "ISRG Root X2" as E5's issuer but ends under "ISRG Root X1";
        // the alternate [leaf, E5] ends under "ISRG Root X2".
        var x2 = await h.Client.DownloadCertificateAsync(account, url, "ISRG Root X2", Token);
        var x1 = await h.Client.DownloadCertificateAsync(account, url, "isrg root x1", Token);
        var unmatched = await h.Client.DownloadCertificateAsync(account, url, "Nobody", Token);
        var intermediateOnly = await h.Client.DownloadCertificateAsync(account, url, "E5", Token);

        Assert.Equal(AcmePem.CertificateDers(FakeAcmeServer.AlternateChainPem), AcmePem.CertificateDers(x2.PemChain));
        Assert.Equal(["E5", "ISRG Root X2"], x2.IssuerCommonNames);
        Assert.Equal(AcmePem.CertificateDers(FakeAcmeServer.DefaultChainPem), AcmePem.CertificateDers(x1.PemChain));
        Assert.Equal(AcmePem.CertificateDers(FakeAcmeServer.DefaultChainPem), AcmePem.CertificateDers(unmatched.PemChain));
        Assert.Equal(AcmePem.CertificateDers(FakeAcmeServer.DefaultChainPem), AcmePem.CertificateDers(intermediateOnly.PemChain));
    }

    [Fact]
    public async Task RevocationIsSignedWithTheKidAndNamesTheReason()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        var leaf = AcmePem.LeafDer(FakeAcmeServer.DefaultChainPem);

        await h.Client.RevokeCertificateAsync(account, leaf, AcmeRevocationReason.KeyCompromise, Token);

        var request = h.Server.To("revoke-cert").Single();
        Assert.Equal(account.Url.AbsoluteUri, request.HeaderString("kid"));
        Assert.False(request.HasHeader("jwk"));
        Assert.Equal(Base64Url.EncodeToString(leaf), request.PayloadJson!["certificate"]!.GetValue<string>());
        Assert.Equal(1, request.PayloadJson["reason"]!.GetValue<int>());
    }

    [Fact]
    public async Task OrderCanReplaceACertificateByItsAriId()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;

        var order = await h.Client.CreateOrderAsync(
            account, new AcmeNewOrderRequest([AcmeIdentifier.Dns("example.com")], Replaces: "aYhba4dGQEHhs3uEe6CuLN4ByNQ.AIdlQyE"), Token);

        Assert.Equal("aYhba4dGQEHhs3uEe6CuLN4ByNQ.AIdlQyE", order.Replaces);
        Assert.Equal("aYhba4dGQEHhs3uEe6CuLN4ByNQ.AIdlQyE", h.Server.ReplacesOf(0));
    }

    [Fact]
    public async Task AnOrderNeedsIdentifiers()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;

        await Assert.ThrowsAsync<ArgumentException>(() => h.Client.CreateOrderAsync(account, new AcmeNewOrderRequest([]), Token));
    }

    [Fact]
    public async Task ATokenThatIsNotBase64UrlFromTheCaIsAProtocolError()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        var order = await h.Client.CreateOrderAsync(account, new AcmeNewOrderRequest([AcmeIdentifier.Dns("example.com")]), Token);
        h.Server.Inject("authz/3", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"identifier":{"type":"dns","value":"example.com"},"status":"pending","challenges":[{"type":"http-01","url":"https://acme.test/chall/2-http","status":"pending","token":"../../x"}]}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        });

        await Assert.ThrowsAsync<AcmeProtocolException>(() => h.Client.GetAuthorizationAsync(account, order.Authorizations[0], Token));
    }

    private static async Task<Uri> IssueAsync(AcmeHarness h, AcmeAccountRef account)
    {
        var order = await h.Client.CreateOrderAsync(account, new AcmeNewOrderRequest([AcmeIdentifier.Dns("example.com")]), Token);
        var challenge = (await h.Client.GetAuthorizationAsync(account, order.Authorizations[0], Token)).FindChallenge(AcmeChallenges.Dns01)!;
        await h.Client.RespondToChallengeAsync(account, challenge, Token);
        await h.Client.WaitForAuthorizationAsync(account, order.Authorizations[0], Token);
        var ready = await h.Client.WaitForOrderReadyAsync(account, order.Url, Token);
        await h.Client.FinalizeOrderAsync(account, ready, new byte[] { 1 }, Token);
        return (await h.Client.WaitForOrderIssuedAsync(account, order.Url, Token)).Certificate!;
    }
}
