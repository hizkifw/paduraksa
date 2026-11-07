using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Paduraksa.Acme;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Acme.Fakes;
using static Paduraksa.Tests.Acme.Fakes.AcmeHarness;

namespace Paduraksa.Tests.Acme;

/// <summary>What a hostile or sloppy CA must not be able to do, the retry information a scheduler needs, and parallel use.</summary>
public sealed class AcmeHardeningTests
{
    private static readonly AcmeNewOrderRequest OneName = new([AcmeIdentifier.Dns("example.com")]);

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // ---- URLs from the CA ----

    [Theory]
    [InlineData("""{"status":"pending","identifiers":[{"type":"dns","value":"example.com"}],"authorizations":["/authz/9"],"finalize":"https://acme.test/finalize/2"}""")]
    [InlineData("""{"status":"pending","identifiers":[{"type":"dns","value":"example.com"}],"authorizations":[],"finalize":"finalize/2"}""")]
    [InlineData("""{"status":"valid","identifiers":[],"authorizations":[],"finalize":"https://acme.test/finalize/2","certificate":"/cert/2"}""")]
    public async Task ARelativeUrlInAnOrderIsAProtocolErrorNotACrash(string body)
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        h.Server.Inject("new-order", _ => JsonResponse(body, HttpStatusCode.Created).WithLocation(FakeAcmeServer.Url("order/2")));

        await Assert.ThrowsAsync<AcmeProtocolException>(() => h.Client.CreateOrderAsync(account, OneName, Token));
    }

    [Fact]
    public async Task ARelativeChallengeUrlIsAProtocolError()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        var order = await h.Client.CreateOrderAsync(account, OneName, Token);
        h.Server.Inject("authz/3", _ => JsonResponse(
            """{"identifier":{"type":"dns","value":"example.com"},"status":"pending","challenges":[{"type":"http-01","url":"chall/3-http","status":"pending","token":"abc"}]}"""));

        await Assert.ThrowsAsync<AcmeProtocolException>(() => h.Client.GetAuthorizationAsync(account, order.Authorizations[0], Token));
    }

    [Fact]
    public async Task ARelativeUrlPassedByTheCallerIsAProtocolErrorWithoutAnyRequest()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;

        var ex = await Assert.ThrowsAsync<AcmeProtocolException>(() => h.Client.GetOrderAsync(account, new Uri("/order/2", UriKind.Relative), Token));

        Assert.Contains("/order/2", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.Server.To("order/2"));
    }

    [Fact]
    public async Task AnIdentifierWithoutAValueIsAProtocolError()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        h.Server.Inject("new-order", _ => JsonResponse(
            """{"status":"pending","identifiers":[{"type":"dns"}],"authorizations":[],"finalize":"https://acme.test/finalize/2"}""",
            HttpStatusCode.Created).WithLocation(FakeAcmeServer.Url("order/2")));

        await Assert.ThrowsAsync<AcmeProtocolException>(() => h.Client.CreateOrderAsync(account, OneName, Token));
    }

    [Fact]
    public async Task DirectoryLinksShownToAnOperatorAreKeptOnlyIfAbsoluteHttp()
    {
        var server = new FakeAcmeServer();
        server.Inject("directory", _ => JsonResponse(
            """{"newNonce":"https://acme.test/n","newAccount":"https://acme.test/a","newOrder":"https://acme.test/o","revokeCert":"https://acme.test/r","meta":{"termsOfService":"javascript:alert(1)","website":"https://ca.test/"}}"""));
        using var h = new AcmeHarness(server: server);

        var directory = await h.Client.GetDirectoryAsync(Token);

        Assert.Null(directory.Meta!.TermsOfService);
        Assert.Equal(new Uri("https://ca.test/"), directory.Meta.Website);
    }

    [Theory]
    [InlineData("/relative", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("https://ca.test/why", "https://ca.test/why")]
    public async Task TheRenewalExplanationLinkIsKeptOnlyIfAbsoluteHttp(string explanation, string? expected)
    {
        using var h = new AcmeHarness();
        h.Server.RenewalInfoBodies["AAAA.BBBB"] =
            $$"""{"suggestedWindow":{"start":"2026-12-01T00:00:00Z","end":"2026-12-02T00:00:00Z"},"explanationURL":"{{explanation}}"}""";

        var info = await h.Client.GetRenewalInfoAsync("AAAA.BBBB", Token);

        Assert.Equal(expected is null ? null : new Uri(expected), info!.ExplanationUrl);
    }

    // ---- Origins ----

    [Fact]
    public async Task ALocationOnAnotherOriginIsRefused()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        h.Server.Inject("new-order", _ => JsonResponse(
            """{"status":"pending","identifiers":[],"authorizations":[],"finalize":"https://acme.test/finalize/2"}""", HttpStatusCode.Created)
            .WithLocation(new Uri("https://evil.test/order/2")));

        var ex = await Assert.ThrowsAsync<AcmeProtocolException>(() => h.Client.CreateOrderAsync(account, OneName, Token));

        Assert.Contains("origin", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAlternateChainOnAnotherOriginIsRefused()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        h.Server.Inject("cert/9", _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FakeAcmeServer.DefaultChainPem, Encoding.ASCII, "application/pem-certificate-chain") };
            response.Headers.TryAddWithoutValidation("Link", "<https://evil.test/cert/1>;rel=\"alternate\"");
            return response;
        });

        await Assert.ThrowsAsync<AcmeProtocolException>(() =>
            h.Client.DownloadCertificateAsync(account, FakeAcmeServer.Url("cert/9"), null, Token));
    }

    [Fact]
    public async Task ASignedRequestToAnotherOriginIsRefusedBeforeItIsSigned()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        var signed = h.Signer.SignCount;

        await Assert.ThrowsAsync<AcmeProtocolException>(() => h.Client.GetOrderAsync(account, new Uri("https://evil.test/order/2"), Token));

        Assert.Equal(signed, h.Signer.SignCount);
    }

    [Fact]
    public async Task AnAdditionalOriginIsAllowedWhenTheOptionsNameIt()
    {
        using var plain = new AcmeHarness();
        var account = (await plain.RegisterAsync()).Ref;
        var order = await plain.Client.CreateOrderAsync(account, OneName, Token);
        var onCdn = new Uri("https://cdn.acme.test/order/" + order.Url.Segments[^1]);
        await Assert.ThrowsAsync<AcmeProtocolException>(() => plain.Client.GetOrderAsync(account, onCdn, Token));

        // Same fake CA, now told that cdn.acme.test is one of its hosts.
        using var h = new AcmeHarness(DefaultOptions with { AdditionalOrigins = [new Uri("https://cdn.acme.test")] }, plain.Server);
        var again = (await h.RegisterAsync()).Ref;
        var fresh = await h.Client.CreateOrderAsync(again, OneName, Token);
        var read = await h.Client.GetOrderAsync(again, new Uri("https://cdn.acme.test/order/" + fresh.Url.Segments[^1]), Token);

        Assert.Equal(AcmeStatus.Pending, read.Status);
    }

    [Fact]
    public async Task ADirectoryUrlOnAnotherOriginIsRefused()
    {
        var server = new FakeAcmeServer();
        server.Inject("directory", _ => JsonResponse(
            """{"newNonce":"https://acme.test/n","newAccount":"https://acme.test/a","newOrder":"https://evil.test/o","revokeCert":"https://acme.test/r"}"""));
        using var h = new AcmeHarness(server: server);

        await Assert.ThrowsAsync<AcmeProtocolException>(() => h.Client.GetDirectoryAsync(Token));
    }

    [Fact]
    public async Task ANewNonceAnswerWithoutAUsableNonceIsAProtocolViolation()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;

        // An answer with a malformed nonce leaves the pool empty, and newNonce then answers with a malformed one too.
        h.Server.Inject("acct/1", _ => BadNonceResponse(JsonResponse("""{"status":"valid"}""")));
        await h.Client.GetAccountAsync(account, Token);
        h.Server.Inject("new-nonce", _ => BadNonceResponse(new HttpResponseMessage(HttpStatusCode.OK)));

        var ex = await Assert.ThrowsAsync<AcmeProtocolException>(() => h.Client.GetAccountAsync(account, Token));

        Assert.Equal(AcmeFailureKind.ProtocolViolation, ex.Kind);
    }

    private static HttpResponseMessage BadNonceResponse(HttpResponseMessage response)
    {
        response.Headers.TryAddWithoutValidation("Replay-Nonce", "not a nonce!");
        return response;
    }

    // ---- Bodies ----

    [Fact]
    public async Task AnIoErrorWhileReadingTheBodyIsATransientTransportError()
    {
        var server = new FakeAcmeServer();
        server.Inject("directory", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent() });
        using var h = new AcmeHarness(server: server);

        var ex = await Assert.ThrowsAsync<AcmeTransportException>(() => h.Client.GetDirectoryAsync(Token));

        Assert.True(ex.IsTransient);
        Assert.IsAssignableFrom<IOException>(ex.InnerException);
    }

    [Fact]
    public async Task ADownloadedChainIsParsedAndRebuiltFromItsCertificateBlocksOnly()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        h.Server.Inject("cert/9", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "preamble\n-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----\n" + FakeAcmeServer.DefaultChainPem,
                Encoding.ASCII,
                "application/pem-certificate-chain"),
        });

        var chain = await h.Client.DownloadCertificateAsync(account, FakeAcmeServer.Url("cert/9"), null, Token);

        Assert.DoesNotContain("PRIVATE", chain.PemChain, StringComparison.Ordinal);
        Assert.DoesNotContain("preamble", chain.PemChain, StringComparison.Ordinal);
        Assert.Equal(AcmePem.CertificateDers(FakeAcmeServer.DefaultChainPem), AcmePem.CertificateDers(chain.PemChain));
    }

    [Fact]
    public async Task ACertificateBlockThatIsNotACertificateIsAProtocolError()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        h.Server.Inject("cert/9", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n", Encoding.ASCII, "application/pem-certificate-chain"),
        });

        await Assert.ThrowsAsync<AcmeProtocolException>(() =>
            h.Client.DownloadCertificateAsync(account, FakeAcmeServer.Url("cert/9"), null, Token));
    }

    // ---- Problems ----

    [Fact]
    public async Task ProblemDetailInTheMessageIsCutAndStrippedButKeptRawOnTheProblem()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        var raw = "bad\u0007 \u001b[31mred\n" + new string('x', 2000);
        h.Server.Inject("new-order", _ => FakeAcmeServer.Problem(HttpStatusCode.BadRequest, "urn:ietf:params:acme:error:malformed", raw));

        var ex = await Assert.ThrowsAsync<AcmeProblemException>(() => h.Client.CreateOrderAsync(account, OneName, Token));

        Assert.True(ex.Message.Length < 800);
        Assert.DoesNotContain(ex.Message, char.IsControl);
        Assert.StartsWith("bad", ex.Message[(ex.Message.IndexOf('(', StringComparison.Ordinal) + 1)..], StringComparison.Ordinal);
        Assert.Equal(raw, ex.Detail);
    }

    [Fact]
    public async Task UserActionRequiredKeepsTheInstanceAndTheTermsLink()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        h.Server.Inject("new-order", _ =>
        {
            var body = new JsonObject
            {
                ["type"] = AcmeProblemTypes.UserActionRequired,
                ["detail"] = "Terms changed",
                ["instance"] = "https://ca.test/agree/42",
            };
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/problem+json"),
            };
            response.Headers.TryAddWithoutValidation("Link", "<javascript:alert(1)>;rel=\"terms-of-service\", <https://ca.test/terms-v2>;rel=\"terms-of-service\"");
            return response;
        });

        var ex = await Assert.ThrowsAsync<AcmeProblemException>(() => h.Client.CreateOrderAsync(account, OneName, Token));

        Assert.Equal("https://ca.test/agree/42", ex.Problem.Instance);
        Assert.Equal(new Uri("https://ca.test/terms-v2"), ex.TermsOfServiceUrl);
        Assert.Equal(AcmeFailureKind.Permanent, ex.Kind);
    }

    // ---- Retry-After for the scheduler ----

    [Fact]
    public async Task SingleShotCallsReportTheCasRetryAfterOnTheModel()
    {
        var server = new FakeAcmeServer { RetryAfterSecondsWhilePending = 7, AuthorizationPollsBeforePass = 5 };
        using var h = new AcmeHarness(server: server);
        var account = (await h.RegisterAsync()).Ref;
        var order = await h.Client.CreateOrderAsync(account, OneName, Token);
        var challenge = (await h.Client.GetAuthorizationAsync(account, order.Authorizations[0], Token)).FindChallenge(AcmeChallenges.Dns01)!;
        Assert.Null((await h.Client.GetAuthorizationAsync(account, order.Authorizations[0], Token)).RetryAfter);

        await h.Client.RespondToChallengeAsync(account, challenge, Token);
        var authz = await h.Client.GetAuthorizationAsync(account, order.Authorizations[0], Token);

        Assert.Equal(TimeSpan.FromSeconds(7), authz.RetryAfter);
    }

    [Fact]
    public async Task FinalizeAndOrderReadsReportRetryAfter()
    {
        var server = new FakeAcmeServer { RetryAfterSecondsWhilePending = 7, AuthorizationPollsBeforePass = 0, OrderPollsWhileProcessing = 2 };
        using var h = new AcmeHarness(server: server);
        var account = (await h.RegisterAsync()).Ref;
        var order = await h.Client.CreateOrderAsync(account, OneName, Token);
        var challenge = (await h.Client.GetAuthorizationAsync(account, order.Authorizations[0], Token)).FindChallenge(AcmeChallenges.Dns01)!;
        await h.Client.RespondToChallengeAsync(account, challenge, Token);
        await h.Client.GetAuthorizationAsync(account, order.Authorizations[0], Token);
        var ready = await h.Client.WaitForOrderReadyAsync(account, order.Url, Token);

        var finalizing = await h.Client.FinalizeOrderAsync(account, ready, new byte[] { 1 }, Token);
        var processing = await h.Client.GetOrderAsync(account, order.Url, Token);

        Assert.Equal(TimeSpan.FromSeconds(7), finalizing.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(7), processing.RetryAfter);
    }

    // ---- Invalid outcomes ----

    [Fact]
    public async Task AFailedValidationReturnsInvalidWithTheProblemOnTheChallengeAndTheOrder()
    {
        var server = new FakeAcmeServer { FailValidation = true, AuthorizationPollsBeforePass = 1 };
        using var h = new AcmeHarness(server: server);
        var account = (await h.RegisterAsync()).Ref;
        var order = await h.Client.CreateOrderAsync(account, OneName, Token);
        var challenge = (await h.Client.GetAuthorizationAsync(account, order.Authorizations[0], Token)).FindChallenge(AcmeChallenges.Http01)!;
        await h.Client.RespondToChallengeAsync(account, challenge, Token);

        var authz = await h.Client.WaitForAuthorizationAsync(account, order.Authorizations[0], Token);
        var failedOrder = await h.Client.WaitForOrderReadyAsync(account, order.Url, Token);

        Assert.Equal(AcmeStatus.Invalid, authz.Status);
        var error = authz.FindChallenge(AcmeChallenges.Http01)!.Error!;
        Assert.Equal("urn:ietf:params:acme:error:unauthorized", error.Type);
        Assert.Equal(403, error.Status);
        Assert.Equal(AcmeStatus.Invalid, failedOrder.Status);
        Assert.Equal("urn:ietf:params:acme:error:unauthorized", failedOrder.Error!.Type);
    }

    [Fact]
    public async Task WaitingForIssuanceOfAnOrderThatWasNeverFinalizedFailsAtOnce()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        var order = await h.Client.CreateOrderAsync(account, OneName, Token);
        var challenge = (await h.Client.GetAuthorizationAsync(account, order.Authorizations[0], Token)).FindChallenge(AcmeChallenges.Dns01)!;
        await h.Client.RespondToChallengeAsync(account, challenge, Token);
        await h.Client.WaitForAuthorizationAsync(account, order.Authorizations[0], Token);
        var ready = await h.Client.WaitForOrderReadyAsync(account, order.Url, Token);
        var delays = h.Time.Delays.Count;

        var ex = await Assert.ThrowsAsync<AcmeProtocolException>(() => h.Client.WaitForOrderIssuedAsync(account, order.Url, Token));

        Assert.Equal(AcmeStatus.Ready, ready.Status);
        Assert.Equal(delays, h.Time.Delays.Count);
        Assert.Contains("Ready", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAuthorizationCanBeDeactivated()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        var order = await h.Client.CreateOrderAsync(account, OneName, Token);

        var authz = await h.Client.DeactivateAuthorizationAsync(account, order.Authorizations[0], Token);

        Assert.Equal(AcmeStatus.Deactivated, authz.Status);
        Assert.Equal("deactivated", h.Server.Requests.Last().PayloadJson!["status"]!.GetValue<string>());
    }

    // ---- Caller errors ----

    [Fact]
    public async Task ReplacesMustLookLikeAnAriId()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Client.CreateOrderAsync(account, new AcmeNewOrderRequest(OneName.Identifiers, Replaces: "../x"), Token));
    }

    [Fact]
    public async Task AnUnsetAccountReferenceIsACallerError()
    {
        using var h = new AcmeHarness();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Client.CreateOrderAsync(default, OneName, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Client.GetAccountAsync(default, Token));
    }

    // ---- Logging ----

    [Fact]
    public async Task OneBadNonceRetryIsLoggedAtDebugAndExhaustionOnceAtWarning()
    {
        using var h = new AcmeHarness(DefaultOptions with { MaxBadNonceRetries = 1 });
        var account = await h.RegisterAsync();
        h.Server.ForgetNonces();
        await h.Client.GetAccountAsync(account.Ref, Token);

        Assert.Empty(h.Logs.At(LogLevel.Warning));
        Assert.NotEmpty(h.Logs.At(LogLevel.Debug));

        for (var i = 0; i < 2; i++)
        {
            h.Server.Inject("acct/1", _ => FakeAcmeServer.Problem(HttpStatusCode.BadRequest, AcmeProblemTypes.BadNonce, "no"));
        }

        await Assert.ThrowsAsync<AcmeProblemException>(() => h.Client.GetAccountAsync(account.Ref, Token));

        Assert.Single(h.Logs.At(LogLevel.Warning));
        Assert.Contains("nonce", h.Logs.At(LogLevel.Warning)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControlCharactersInAProblemTypeNeverReachTheLog()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;
        h.Server.Inject("new-order", _ => FakeAcmeServer.Problem(HttpStatusCode.BadRequest, "urn:x\r\nFORGED LOG LINE", "d"));

        await Assert.ThrowsAsync<AcmeProblemException>(() => h.Client.CreateOrderAsync(account, OneName, Token));

        Assert.All(h.Logs.Messages, m => Assert.DoesNotContain(m, char.IsControl));
    }

    // ---- Key types and parallel use ----

    [Theory]
    [InlineData(JwsAlgorithm.ES256)]
    [InlineData(JwsAlgorithm.ES384)]
    [InlineData(JwsAlgorithm.RS256)]
    public async Task EveryAccountKeyAlgorithmSignsRequestsTheCaVerifies(JwsAlgorithm algorithm)
    {
        using var h = new AcmeHarness();

        var account = await h.RegisterAsync(algorithm: algorithm);
        var read = await h.Client.GetAccountAsync(account.Ref, Token);
        var order = await h.Client.CreateOrderAsync(account.Ref, OneName, Token);

        Assert.True(account.Created);
        Assert.Equal(AcmeStatus.Valid, read.Status);
        Assert.Equal(AcmeStatus.Pending, order.Status);
        Assert.All(h.Server.Requests.Where(r => r.Method == "POST"), r => Assert.Equal(algorithm.ToName(), r.HeaderString("alg")));
    }

    [Fact]
    public async Task ParallelOrdersOnOneAccountEachGetTheirOwnNonce()
    {
        using var h = new AcmeHarness();
        var account = (await h.RegisterAsync()).Ref;

        var orders = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => h.Client.CreateOrderAsync(account, OneName, Token)));

        Assert.Equal(24, orders.Select(o => o.Url).Distinct().Count());
        Assert.Equal(24, h.Server.OrderCount);
        var nonces = h.Server.To("new-order").Select(r => r.HeaderString("nonce")).ToList();
        Assert.Equal(24, nonces.Distinct().Count());
        Assert.Equal(25, h.Signer.SignCount);
    }

    /// <summary>Content whose body stream breaks mid-read with an <see cref="IOException"/>, as a reset connection does.</summary>
    private sealed class BrokenContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new BrokenStream());

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("connection reset while reading"));

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset while reading");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal static class ResponseExtensions
{
    public static HttpResponseMessage WithLocation(this HttpResponseMessage response, Uri location)
    {
        response.Headers.Location = location;
        return response;
    }
}
