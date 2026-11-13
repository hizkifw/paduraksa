using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Paduraksa.Acme;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Acme.Fakes;
using static Paduraksa.Tests.Acme.Fakes.AcmeHarness;

namespace Paduraksa.Tests.Acme;

public sealed class AcmeTransportTests
{
    private static readonly AcmeNewOrderRequest OneName = new([AcmeIdentifier.Dns("example.com")]);

    [Fact]
    public async Task NoncesAreReusedFromResponsesSoNewNonceIsFetchedOnce()
    {
        using var h = new AcmeHarness();
        var account = await h.RegisterAsync();

        await h.Client.GetAccountAsync(account.Ref, Token);
        await h.Client.CreateOrderAsync(account.Ref, OneName, Token);
        await h.Client.GetAccountAsync(account.Ref, Token);

        Assert.Equal(1, h.Server.NonceRequests);
        Assert.Equal(4, h.Signer.SignCount);
    }

    [Fact]
    public async Task BadNonceIsRetriedWithAFreshNonceAndANewSignature()
    {
        using var h = new AcmeHarness();
        var account = await h.RegisterAsync();
        var signedBefore = h.Signer.SignCount;
        h.Server.ForgetNonces();

        var read = await h.Client.GetAccountAsync(account.Ref, Token);

        Assert.Equal(AcmeStatus.Valid, read.Status);
        Assert.Equal(signedBefore + 2, h.Signer.SignCount);
        var attempts = h.Server.To("acct/1").ToList();
        Assert.Equal(2, attempts.Count);
        Assert.NotEqual(attempts[0].HeaderString("nonce"), attempts[1].HeaderString("nonce"));
    }

    [Fact]
    public async Task BadNonceBeyondTheRetryLimitIsATransientProblem()
    {
        using var h = new AcmeHarness(new AcmeClientOptions { MaxBadNonceRetries = 2 });
        var account = await h.RegisterAsync();
        for (var i = 0; i < 3; i++)
        {
            h.Server.Inject("acct/1", _ => FakeAcmeServer.Problem(HttpStatusCode.BadRequest, AcmeProblemTypes.BadNonce, "no"));
        }

        var ex = await Assert.ThrowsAsync<AcmeProblemException>(() => h.Client.GetAccountAsync(account.Ref, Token));

        Assert.Equal(AcmeProblemTypes.BadNonce, ex.Type);
        Assert.Equal(AcmeFailureKind.Transient, ex.Kind);
        Assert.Equal(3, h.Server.To("acct/1").Count());
    }

    [Fact]
    public async Task RateLimitedProblemCarriesRetryAfterAndItsDocument()
    {
        using var h = new AcmeHarness();
        var account = await h.RegisterAsync();
        h.Server.Inject("new-order", _ => FakeAcmeServer.Problem(
            HttpStatusCode.TooManyRequests, AcmeProblemTypes.RateLimited, "too many certificates (5) already issued", retryAfter: "7200"));

        var ex = await Assert.ThrowsAsync<AcmeProblemException>(() => h.Client.CreateOrderAsync(account.Ref, OneName, Token));

        Assert.Equal(AcmeFailureKind.RateLimited, ex.Kind);
        Assert.True(ex.IsTransient);
        Assert.Equal(TimeSpan.FromHours(2), ex.RetryAfter);
        Assert.Equal("too many certificates (5) already issued", ex.Detail);
        Assert.Equal(429, ex.Status);
    }

    [Fact]
    public async Task RetryAfterAsAnHttpDateIsRelativeToTheClock()
    {
        using var h = new AcmeHarness();
        var account = await h.RegisterAsync();
        var when = h.Time.GetUtcNow().AddMinutes(10).UtcDateTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        h.Server.Inject("new-order", _ => FakeAcmeServer.Problem(HttpStatusCode.ServiceUnavailable, AcmeProblemTypes.ServerInternal, "later", retryAfter: when));

        var ex = await Assert.ThrowsAsync<AcmeProblemException>(() => h.Client.CreateOrderAsync(account.Ref, OneName, Token));

        Assert.InRange(ex.RetryAfter!.Value, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task CompoundProblemExposesItsSubproblems()
    {
        using var h = new AcmeHarness();
        var account = await h.RegisterAsync();
        h.Server.Inject("new-order", _ => FakeAcmeServer.Problem(
            HttpStatusCode.BadRequest,
            AcmeProblemTypes.Compound,
            "Error creating new order",
            subproblems: [new JsonObject
            {
                ["type"] = "urn:ietf:params:acme:error:rejectedIdentifier",
                ["detail"] = "Policy forbids issuing for this name",
                ["identifier"] = new JsonObject { ["type"] = "dns", ["value"] = "bad.example" },
            }]));

        var ex = await Assert.ThrowsAsync<AcmeProblemException>(() => h.Client.CreateOrderAsync(account.Ref, OneName, Token));

        Assert.Equal(AcmeFailureKind.Permanent, ex.Kind);
        var sub = Assert.Single(ex.Subproblems);
        Assert.Equal("urn:ietf:params:acme:error:rejectedIdentifier", sub.Type);
        Assert.Equal(new AcmeIdentifier("dns", "bad.example"), sub.Identifier);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, AcmeFailureKind.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, AcmeFailureKind.Transient)]
    [InlineData(HttpStatusCode.TooManyRequests, AcmeFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.Forbidden, AcmeFailureKind.Permanent)]
    [InlineData(HttpStatusCode.NotFound, AcmeFailureKind.Permanent)]
    public async Task AnAnswerThatIsNotAProblemDocumentIsClassifiedByItsStatus(HttpStatusCode status, AcmeFailureKind expected)
    {
        using var h = new AcmeHarness();
        var account = await h.RegisterAsync();
        h.Server.Inject("new-order", _ => new HttpResponseMessage(status)
        {
            Content = new StringContent("<html>gateway trouble</html>", Encoding.UTF8, "text/html"),
        });

        var ex = await Assert.ThrowsAsync<AcmeProblemException>(() => h.Client.CreateOrderAsync(account.Ref, OneName, Token));

        Assert.Equal("about:blank", ex.Type);
        Assert.Equal(expected, ex.Kind);
        Assert.Equal((int)status, ex.Status);
    }

    [Theory]
    [InlineData("urn:ietf:params:acme:error:unauthorized", AcmeFailureKind.Permanent)]
    [InlineData("urn:ietf:params:acme:error:badCSR", AcmeFailureKind.Permanent)]
    [InlineData("urn:ietf:params:acme:error:caa", AcmeFailureKind.Permanent)]
    [InlineData(AcmeProblemTypes.ServerInternal, AcmeFailureKind.Transient)]
    [InlineData(AcmeProblemTypes.RateLimited, AcmeFailureKind.RateLimited)]
    public void ProblemTypesClassify(string type, AcmeFailureKind expected) =>
        Assert.Equal(expected, AcmeProblemTypes.Classify(type, 400));

    [Fact]
    public async Task ARedirectIsRefusedNotFollowed()
    {
        using var h = new AcmeHarness();
        var account = await h.RegisterAsync();
        h.Server.Inject("new-order", _ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            redirect.Headers.Location = new Uri("https://elsewhere.test/new-order");
            redirect.Headers.Add("Replay-Nonce", "attackerNonce");
            return redirect;
        });

        var ex = await Assert.ThrowsAsync<AcmeProtocolException>(() => h.Client.CreateOrderAsync(account.Ref, OneName, Token));
        await h.Client.GetAccountAsync(account.Ref, Token);

        Assert.Equal(AcmeFailureKind.ProtocolViolation, ex.Kind);
        Assert.False(ex.IsTransient);
        Assert.True(ex.IsRetryable);
        Assert.Single(h.Server.To("new-order"));
        Assert.NotEqual("attackerNonce", h.Server.To("acct/1").Single().HeaderString("nonce"));
    }

    [Fact]
    public async Task AFollowedRedirectIsDetectedFromTheResponsesRequestUrl()
    {
        using var h = new AcmeHarness(wrap: inner => new RewritingHandler(new HttpMessageInvoker(inner)));

        await Assert.ThrowsAsync<AcmeProtocolException>(() => h.Client.GetDirectoryAsync(Token));
    }

    [Fact]
    public async Task UrlsThatAreNotHttpsAreRefused()
    {
        using var h = new AcmeHarness();
        var account = await h.RegisterAsync();

        await Assert.ThrowsAsync<AcmeProtocolException>(() =>
            h.Client.GetOrderAsync(account.Ref, new Uri("http://acme.test/order/1"), Token));
    }

    [Fact]
    public async Task ADirectoryWithAnInsecureUrlIsRefused()
    {
        var server = new FakeAcmeServer();
        server.Inject("directory", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"newNonce":"http://acme.test/n","newAccount":"https://acme.test/a","newOrder":"https://acme.test/o","revokeCert":"https://acme.test/r"}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var h = new AcmeHarness(server: server);

        await Assert.ThrowsAsync<AcmeProtocolException>(() => h.Client.GetDirectoryAsync(Token));
    }

    [Fact]
    public async Task ADirectoryWithoutMandatoryUrlsIsAProtocolError()
    {
        var server = new FakeAcmeServer();
        server.Inject("directory", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        using var h = new AcmeHarness(server: server);

        await Assert.ThrowsAsync<AcmeProtocolException>(() => h.Client.GetDirectoryAsync(Token));
    }

    [Fact]
    public async Task AnOversizedResponseIsRefused()
    {
        var server = new FakeAcmeServer();
        server.Inject("directory", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 5000), Encoding.UTF8, "application/json") });
        using var h = new AcmeHarness(new AcmeClientOptions { MaxResponseBytes = 1000 }, server);

        await Assert.ThrowsAsync<AcmeProtocolException>(() => h.Client.GetDirectoryAsync(Token));
    }

    [Fact]
    public async Task AConnectionFailureIsATransientTransportError()
    {
        using var h = new AcmeHarness();
        using var client = new AcmeClient(FakeAcmeServer.Url("directory"), h.Signer, handler: new ThrowingHandler(new HttpRequestException("connection refused")));

        var ex = await Assert.ThrowsAsync<AcmeTransportException>(() => client.GetDirectoryAsync(Token));

        Assert.Equal(AcmeFailureKind.Transient, ex.Kind);
    }

    [Theory]
    [InlineData("http://127.0.0.1:14000/dir", false, false)]
    [InlineData("http://ca.example.com/dir", true, false)]
    [InlineData("http://192.0.2.10/dir", true, false)]
    [InlineData("http://127.0.0.1:14000/dir", true, true)]
    [InlineData("http://localhost:14000/dir", true, true)]
    [InlineData("http://[::1]:14000/dir", true, true)]
    public async Task PlainHttpIsAcceptedOnlyForALoopbackHostAndOnlyWhenAllowed(string directory, bool allowed, bool reachesTheNetwork)
    {
        using var h = new AcmeHarness();
        var options = new AcmeClientOptions { AllowInsecureHttp = allowed };

        // Refused up front (the directory URL is checked when the client is made), or let through to the transport, which here fails like a closed port.
        if (!reachesTheNetwork)
        {
            Assert.Throws<AcmeProtocolException>(() => new AcmeClient(new Uri(directory), h.Signer, options: options, handler: new ThrowingHandler(new HttpRequestException("connection refused"))));
            return;
        }

        using var client = new AcmeClient(new Uri(directory), h.Signer, options: options, handler: new ThrowingHandler(new HttpRequestException("connection refused")));
        await Assert.ThrowsAsync<AcmeTransportException>(() => client.GetDirectoryAsync(Token));
    }

    [Fact]
    public async Task ARequestThatTakesTooLongTimesOutAsATransientError()
    {
        using var h = new AcmeHarness();
        using var client = new AcmeClient(
            FakeAcmeServer.Url("directory"), h.Signer, options: new AcmeClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(50) }, handler: new HangingHandler());

        var ex = await Assert.ThrowsAsync<AcmeTransportException>(() => client.GetDirectoryAsync(Token));

        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task CallerCancellationIsNotAnAcmeError()
    {
        using var h = new AcmeHarness();
        using var client = new AcmeClient(FakeAcmeServer.Url("directory"), h.Signer, handler: new HangingHandler());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetDirectoryAsync(cts.Token));
    }

    [Fact]
    public async Task RenewalInfoParsesTheWindowAndExplanation()
    {
        using var h = new AcmeHarness();
        h.Server.RenewalInfoRetryAfterSeconds = 21600;
        h.Server.RenewalInfoBodies["aYhba4dGQEHhs3uEe6CuLN4ByNQ.AIdlQyE"] =
            """{"suggestedWindow":{"start":"2026-12-01T00:00:00Z","end":"2026-12-03T00:00:00Z"},"explanationURL":"https://ca.test/why"}""";

        var info = await h.Client.GetRenewalInfoAsync("aYhba4dGQEHhs3uEe6CuLN4ByNQ.AIdlQyE", Token);

        Assert.NotNull(info);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), info.WindowStart);
        Assert.Equal(new DateTimeOffset(2026, 12, 3, 0, 0, 0, TimeSpan.Zero), info.WindowEnd);
        Assert.Equal(new Uri("https://ca.test/why"), info.ExplanationUrl);
        Assert.Equal(TimeSpan.FromHours(6), info.RetryAfter);
        Assert.Equal("renewal-info/aYhba4dGQEHhs3uEe6CuLN4ByNQ.AIdlQyE", h.Server.Requests.Last().Path);
        Assert.Equal(0, h.Signer.SignCount);
    }

    [Fact]
    public async Task RenewalInfoOfAnUnknownCertificateIsNull()
    {
        using var h = new AcmeHarness();

        Assert.Null(await h.Client.GetRenewalInfoAsync("AAAA.BBBB", Token));
    }

    [Fact]
    public async Task RenewalInfoIsNullWhenTheCaHasNoAri()
    {
        using var h = new AcmeHarness(server: new FakeAcmeServer { AdvertiseRenewalInfo = false });

        Assert.Null(await h.Client.GetRenewalInfoAsync("AAAA.BBBB", Token));
        Assert.DoesNotContain(h.Server.Requests, r => r.Path.StartsWith("renewal-info", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("nodot")]
    [InlineData("a.b/c")]
    public async Task RenewalInfoRefusesAMalformedCertificateId(string id)
    {
        using var h = new AcmeHarness();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Client.GetRenewalInfoAsync(id, Token));
    }

    [Fact]
    public async Task RenewalInfoWithAnInvertedWindowIsAProtocolError()
    {
        using var h = new AcmeHarness();
        h.Server.RenewalInfoBodies["AAAA.BBBB"] = """{"suggestedWindow":{"start":"2026-12-03T00:00:00Z","end":"2026-12-01T00:00:00Z"}}""";

        await Assert.ThrowsAsync<AcmeProtocolException>(() => h.Client.GetRenewalInfoAsync("AAAA.BBBB", Token));
    }

    [Fact]
    public async Task NoLogLineCarriesABodyOrASecret()
    {
        var server = new FakeAcmeServer { ExternalAccountRequired = true, EabKeyId = "eab-kid-123" };
        using var h = new AcmeHarness(server: server);
        var account = await h.RegisterAsync(request: new AcmeNewAccountRequest(
            ["mailto:secret-contact@example.com"], true, new AcmeExternalAccountBinding("eab-kid-123", h.EabSecretId)));
        var order = await h.Client.CreateOrderAsync(account.Ref, OneName, Token);
        await Assert.ThrowsAsync<AcmeProblemException>(() => h.Client.FinalizeOrderAsync(account.Ref, order, new byte[] { 9, 9, 9, 9 }, Token));

        var logs = string.Join('\n', h.Logs.Messages);
        Assert.Contains("ACME POST https://acme.test/new-order", logs, StringComparison.Ordinal);
        Assert.Contains(AcmeProblemTypes.OrderNotReady, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-contact", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("eab-kid-123", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("CQkJCQ", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("signature", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", logs.Replace("acme.test", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAccountKeyIsOnlyEverUsedThroughTheSigner()
    {
        using var h = new AcmeHarness();
        var id = AcmeAccountId.New();
        await h.Signer.CreateAccountKeyAsync(id, Paduraksa.Core.Vault.JwsAlgorithm.ES256, Token);

        await h.Client.RegisterAccountAsync(id, new AcmeNewAccountRequest([], true), Token);

        Assert.Equal(1, h.Signer.SignCount);
        Assert.True(h.Server.To("new-account").Single().HasHeader("jwk"));
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // A hung peer: only the client's timeout or cancellation ends the request.
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>Answers as if the request had been redirected and followed: the response's request is another URL.</summary>
    private sealed class RewritingHandler(HttpMessageInvoker inner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await inner.SendAsync(new HttpRequestMessage(request.Method, request.RequestUri), cancellationToken);
            response.RequestMessage = new HttpRequestMessage(request.Method, new Uri("https://elsewhere.test/directory"));
            return response;
        }
    }
}
