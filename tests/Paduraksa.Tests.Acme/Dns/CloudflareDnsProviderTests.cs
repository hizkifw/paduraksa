using System.Net;
using Paduraksa.Acme.Dns;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Acme.Fakes;
using static Paduraksa.Tests.Acme.Fakes.AcmeHarness;

namespace Paduraksa.Tests.Acme.Dns;

/// <summary>The Cloudflare provider against an in-memory Cloudflare, over a fake of the vault's authenticating handler.</summary>
public sealed class CloudflareDnsProviderTests : IDisposable
{
    private const string Challenge = "_acme-challenge.example.com";

    private readonly FakeCloudflare _cloudflare = new();
    private readonly FakeSecretHandlers _handlers;
    private readonly CapturingLogs _logs = new();
    private readonly VirtualTimeProvider _time = new();

    public CloudflareDnsProviderTests()
    {
        _cloudflare.AddZone("example.com");
        _handlers = new FakeSecretHandlers(_cloudflare.Handle);
    }

    public void Dispose() => _logs.Dispose();

    private IDnsProvider Provider(DnsProviderOptions? options = null) =>
        new DnsProviderFactory(_handlers, options, _time, _logs).Create(DnsProviderKind.Cloudflare, SecretId.New());

    [Fact]
    public async Task TheCredentialGoesOnlyThroughABearerInjectionForTheApiHost()
    {
        await Provider().CreateTxtAsync(Challenge, "v1", Token);

        SecretInjection injection = Assert.Single(_handlers.Injections);
        Assert.Equal(SecretInjectionKind.Authorization, injection.Kind);
        Assert.Equal("Bearer", injection.Scheme);
        Assert.Equal(["api.cloudflare.com"], injection.AllowedHosts);
        Assert.All(_handlers.Calls, c => Assert.Equal("api.cloudflare.com", c.Url.Host));
    }

    [Fact]
    public async Task TheZoneIsFoundByWalkingUpTheLabels()
    {
        _cloudflare.AddZone("b.example.com");

        await Provider().CreateTxtAsync("_acme-challenge.a.b.c.example.com", "v1", Token);

        List<string> lookups = [.. _handlers.Calls.Where(c => c.Path.EndsWith("/zones", StringComparison.Ordinal)).Select(c => c.Params["name"]!)];
        Assert.Equal(["_acme-challenge.a.b.c.example.com", "a.b.c.example.com", "b.c.example.com", "c.example.com", "example.com"], lookups);
        Assert.Equal([("_acme-challenge.a.b.c.example.com", "v1")], _cloudflare.Records("example.com"));
    }

    [Fact]
    public async Task TheMostSpecificZoneWins()
    {
        _cloudflare.AddZone("sub.example.com");

        await Provider().CreateTxtAsync("_acme-challenge.sub.example.com", "v1", Token);

        Assert.Equal([("_acme-challenge.sub.example.com", "v1")], _cloudflare.Records("sub.example.com"));
        Assert.Empty(_cloudflare.Records("example.com"));
    }

    [Fact]
    public async Task ANameWithNoZoneInTheAccountIsPermanent()
    {
        DnsProviderException ex = await Assert.ThrowsAsync<DnsProviderException>(async () =>
            await Provider().CreateTxtAsync("_acme-challenge.other.org", "v1", Token));

        Assert.False(ex.IsTransient);
        Assert.Contains("no zone", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoValuesAtOneNameCoexistAndDeleteRemovesOnlyTheNamedOne()
    {
        IDnsProvider provider = Provider();

        await provider.CreateTxtAsync(Challenge, "wildcard-value", Token);
        await provider.CreateTxtAsync(Challenge, "apex-value", Token);
        Assert.Equal([(Challenge, "wildcard-value"), (Challenge, "apex-value")], _cloudflare.Records("example.com"));

        await provider.DeleteTxtAsync(Challenge, "wildcard-value", Token);
        Assert.Equal([(Challenge, "apex-value")], _cloudflare.Records("example.com"));
    }

    [Fact]
    public async Task CreateAndDeleteAreIdempotent()
    {
        IDnsProvider provider = Provider();
        await provider.CreateTxtAsync(Challenge, "v1", Token);
        await provider.CreateTxtAsync(Challenge, "v1", Token);

        Assert.Single(_cloudflare.Records("example.com"));
        Assert.Single(_handlers.Calls, c => c.Method == HttpMethod.Post);

        await provider.DeleteTxtAsync(Challenge, "v1", Token);
        int deletes = _handlers.Calls.Count(c => c.Method == HttpMethod.Delete);
        await provider.DeleteTxtAsync(Challenge, "v1", Token);

        Assert.Empty(_cloudflare.Records("example.com"));
        Assert.Equal(deletes, _handlers.Calls.Count(c => c.Method == HttpMethod.Delete));
    }

    [Fact]
    public async Task AnIdenticalRecordRefusedByCloudflareCountsAsCreated()
    {
        _cloudflare.Intercept = call => call.Method == HttpMethod.Post
            ? FakeCloudflare.Error(HttpStatusCode.BadRequest, 81058, "An identical record already exists.")
            : null;

        await Provider().CreateTxtAsync(Challenge, "v1", Token);
    }

    [Fact]
    public async Task QuotedContentAndPagedListsAreMatched()
    {
        _cloudflare.QuoteContent = true;
        _cloudflare.PageSize = 2;
        _cloudflare.Seed("example.com", Challenge, "a");
        _cloudflare.Seed("example.com", Challenge, "b");
        _cloudflare.Seed("example.com", Challenge, "c");
        _cloudflare.Seed("example.com", Challenge, "d");
        IDnsProvider provider = Provider();

        await provider.CreateTxtAsync(Challenge, "d", Token);
        Assert.DoesNotContain(_handlers.Calls, c => c.Method == HttpMethod.Post);

        await provider.DeleteTxtAsync(Challenge, "d", Token);
        Assert.Equal(["a", "b", "c"], _cloudflare.Records("example.com").Select(r => r.Content));
    }

    [Fact]
    public async Task ARecordAtAnotherNameIsLeftAlone()
    {
        _cloudflare.Seed("example.com", "other.example.com", "v1");

        await Provider().DeleteTxtAsync(Challenge, "v1", Token);

        Assert.Single(_cloudflare.Records("example.com"));
    }

    [Fact]
    public async Task ARateLimitWithALongRetryAfterIsTransientWithTheHint()
    {
        _cloudflare.Intercept = _ => FakeCloudflare.Error(HttpStatusCode.TooManyRequests, 971, "Rate limited", ("Retry-After", "120"));

        DnsProviderException ex = await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().CreateTxtAsync(Challenge, "v1", Token));

        Assert.True(ex.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(120), ex.RetryAfter);
        Assert.Empty(_time.Delays);
    }

    [Fact]
    public async Task AShortRetryAfterIsWaitedOutOnceInsideTheCall()
    {
        bool limited = false;
        _cloudflare.Intercept = call =>
        {
            if (limited || call.Path.EndsWith("/zones", StringComparison.Ordinal) is false)
            {
                return null;
            }

            limited = true;
            return FakeCloudflare.Error(HttpStatusCode.TooManyRequests, 971, "Rate limited", ("Retry-After", "2"));
        };

        await Provider().CreateTxtAsync(Challenge, "v1", Token);

        // Retry-After is the floor; the jitter adds up to a quarter more.
        Assert.Contains(_time.Delays, delay => delay >= TimeSpan.FromSeconds(2) && delay < TimeSpan.FromSeconds(2.5));
        Assert.Single(_cloudflare.Records("example.com"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task StatusesAreClassifiedAsTransientOrPermanent(HttpStatusCode status, bool transient)
    {
        _cloudflare.Intercept = _ => FakeCloudflare.Error(status, 9109, "Unauthorized to access requested resource");

        DnsProviderException ex = await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().CreateTxtAsync(Challenge, "v1", Token));

        Assert.Equal(transient, ex.IsTransient);
        Assert.Contains($"{(int)status}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANetworkFailureIsTransient()
    {
        _handlers.TransportFailure = new HttpRequestException(HttpRequestError.ConnectionError, "connection refused");

        DnsProviderException ex = await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().CreateTxtAsync(Challenge, "v1", Token));

        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task AnAnswerThatIsNotCloudflaresJsonIsAProtocolFailure()
    {
        _cloudflare.Intercept = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>captive portal</html>") };

        DnsProviderException ex = await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().CreateTxtAsync(Challenge, "v1", Token));

        Assert.False(ex.IsTransient);
        Assert.DoesNotContain("captive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOversizedAnswerIsRefused()
    {
        _cloudflare.Intercept = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 5000)) };

        DnsProviderException ex = await Assert.ThrowsAsync<DnsProviderException>(async () =>
            await Provider(new DnsProviderOptions { MaxResponseBytes = 1000 }).CreateTxtAsync(Challenge, "v1", Token));

        Assert.Contains("1000 bytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusalByTheVaultHandlerIsPermanent()
    {
        _handlers.TransportFailure = new SecretTransportRefusedException("the destination 'api.cloudflare.com' is not one the secret allows");

        DnsProviderException ex = await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().CreateTxtAsync(Challenge, "v1", Token));

        Assert.False(ex.IsTransient);
        Assert.Contains("vault refused", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeitherErrorsNorLogsCarryTheToken()
    {
        _cloudflare.Intercept = call => call.Method == HttpMethod.Post ? FakeCloudflare.Error(HttpStatusCode.Forbidden, 10000, "Authentication error") : null;
        IDnsProvider provider = Provider();
        await provider.DeleteTxtAsync(Challenge, "v1", Token);
        DnsProviderException ex = await Assert.ThrowsAsync<DnsProviderException>(async () => await provider.CreateTxtAsync(Challenge, "v1", Token));
        DnsCredentialCheck check = await provider.ValidateCredentialsAsync(Token);

        Assert.DoesNotContain(FakeSecretHandlers.Token, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecretHandlers.Token, check.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.All(_logs.Entries, e => Assert.DoesNotContain(FakeSecretHandlers.Token, e, StringComparison.Ordinal));
        Assert.Contains("Authentication error (10000)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValidTokenWithZoneAccessChecksOut()
    {
        DnsCredentialCheck check = await Provider().ValidateCredentialsAsync(Token);

        Assert.True(check.Valid);
        Assert.Null(check.Message);
        Assert.Contains(_handlers.Calls, c => c.Path.EndsWith("/user/tokens/verify", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATokenThatIsNotActiveIsInvalid()
    {
        _cloudflare.TokenStatus = "expired";

        DnsCredentialCheck check = await Provider().ValidateCredentialsAsync(Token);

        Assert.False(check.Valid);
        Assert.Contains("expired", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAccountOwnedTokenIsJudgedByTheZoneRead()
    {
        _cloudflare.UserTokenVerifySupported = false;

        DnsCredentialCheck check = await Provider().ValidateCredentialsAsync(Token);

        Assert.True(check.Valid);
    }

    [Fact]
    public async Task ATokenThatReadsNoZoneIsInvalid()
    {
        FakeCloudflare empty = new();
        FakeSecretHandlers handlers = new(empty.Handle);

        DnsCredentialCheck check = await new DnsProviderFactory(handlers, null, _time, _logs)
            .Create(DnsProviderKind.Cloudflare, SecretId.New()).ValidateCredentialsAsync(Token);

        Assert.False(check.Valid);
        Assert.Contains("no zone", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenCloudflareRefusesIsAnInvalidResultNotAnException()
    {
        _cloudflare.Intercept = _ => FakeCloudflare.Error(HttpStatusCode.Forbidden, 9109, "Unauthorized to access requested resource");

        DnsCredentialCheck check = await Provider().ValidateCredentialsAsync(Token);

        Assert.False(check.Valid);
        Assert.Contains("9109", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABadRecordNameOrValueIsRejectedBeforeAnythingIsSent()
    {
        IDnsProvider provider = Provider();

        await Assert.ThrowsAsync<ArgumentException>(async () => await provider.CreateTxtAsync("not a name", "v", Token));
        await Assert.ThrowsAsync<ArgumentException>(async () => await provider.CreateTxtAsync(Challenge, string.Empty, Token));
        await Assert.ThrowsAsync<ArgumentException>(async () => await provider.CreateTxtAsync(Challenge, new string('a', 256), Token));

        Assert.Empty(_handlers.Calls);
    }

    [Fact]
    public async Task ATrailingDotAndCapitalsInTheNameAreNormalised()
    {
        await Provider().CreateTxtAsync("_ACME-Challenge.Example.COM.", "v1", Token);

        Assert.Equal([(Challenge, "v1")], _cloudflare.Records("example.com"));
    }

    [Fact]
    public async Task ACnameAtTheNameIsRefusedBeforeAnythingIsWritten()
    {
        _cloudflare.SeedCname("example.com", Challenge, "validation.other.org");

        DnsProviderException ex = await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().CreateTxtAsync(Challenge, "v1", Token));

        Assert.False(ex.IsTransient);
        Assert.Contains("CNAME to validation.other.org", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not supported yet", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_handlers.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task AListLongerThanThePageLimitIsRefusedNotCutOff()
    {
        _cloudflare.PageSize = 1;
        for (int i = 0; i < 25; i++)
        {
            _cloudflare.Seed("example.com", Challenge, "value" + i);
        }

        DnsProviderException ex = await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().DeleteTxtAsync(Challenge, "value24", Token));

        Assert.False(ex.IsTransient);
        Assert.DoesNotContain(_handlers.Calls, c => c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task ARecordAlreadyGoneWhenDeletedIsLoggedAsSuchNotAsDeleted()
    {
        _cloudflare.Seed("example.com", Challenge, "v1");
        _cloudflare.Intercept = call => call.Method == HttpMethod.Delete ? FakeCloudflare.Error(HttpStatusCode.NotFound, 81044, "Record does not exist.") : null;

        await Provider().DeleteTxtAsync(Challenge, "v1", Token);

        Assert.Contains(_logs.Entries, e => e.Contains("already gone", StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Entries, e => e.Contains("deleted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AThrottleIsMarkedRateLimited()
    {
        _cloudflare.Intercept = _ => FakeCloudflare.Error(HttpStatusCode.TooManyRequests, 971, "Rate limited", ("Retry-After", "600"));

        Assert.True((await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().CreateTxtAsync(Challenge, "v1", Token))).IsRateLimited);
    }

    [Fact]
    public async Task TheDefaultBatchPublishesAndRemovesRecordOneByOne()
    {
        IDnsProvider provider = Provider();
        DnsChallengeRecord[] records = [new(Challenge, "a"), new(Challenge, "b")];

        await provider.CreateTxtBatchAsync(records, Token);
        Assert.Equal(2, _cloudflare.Records("example.com").Count);

        _cloudflare.Intercept = call => call.Method == HttpMethod.Delete && call.Path.EndsWith("rec-0", StringComparison.Ordinal)
            ? FakeCloudflare.Error(HttpStatusCode.InternalServerError, 1, "boom")
            : null;
        await Assert.ThrowsAsync<DnsProviderException>(async () => await provider.DeleteTxtBatchAsync(records, Token));

        // The failing delete did not stop the other.
        Assert.Single(_cloudflare.Records("example.com"));
    }
}
