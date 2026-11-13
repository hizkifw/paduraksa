using System.Net;
using Paduraksa.Acme.Dns;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Acme.Fakes;
using static Paduraksa.Tests.Acme.Fakes.AcmeHarness;

namespace Paduraksa.Tests.Acme.Dns;

public sealed class DesecDnsProviderTests : IDisposable
{
    private const string Challenge = "_acme-challenge.example.com";

    private readonly FakeDesec _desec = new();
    private readonly FakeSecretHandlers _handlers;
    private readonly CapturingLogs _logs = new();
    private readonly VirtualTimeProvider _time = new();

    public DesecDnsProviderTests()
    {
        _desec.AddDomain("example.com");
        _handlers = new FakeSecretHandlers(_desec.Handle);
    }

    public void Dispose() => _logs.Dispose();

    private IDnsProvider Provider(DnsProviderOptions? options = null) =>
        new DnsProviderFactory(_handlers, options, _time, _logs).Create(DnsProviderKind.DeSec, SecretId.New());

    [Fact]
    public async Task TheCredentialGoesOnlyThroughATokenInjectionForTheApiHost()
    {
        await Provider().CreateTxtAsync(Challenge, "v1", Token);

        var injection = Assert.Single(_handlers.Injections);
        Assert.Equal("Token", injection.Scheme);
        Assert.Equal(["desec.io"], injection.AllowedHosts);
        Assert.All(_handlers.Calls, c => Assert.Equal("desec.io", c.Url.Host));
    }

    [Fact]
    public async Task AMissingRrsetIsCreatedWithTheMinimumTtlAndAQuotedValue()
    {
        await Provider().CreateTxtAsync(Challenge, "v1", Token);

        var (ttl, records) = _desec.Rrset("example.com", "_acme-challenge")!.Value;
        Assert.Equal(3600, ttl);
        Assert.Equal(["\"v1\""], records);
        Assert.Contains(_handlers.Calls, c => c.Method == HttpMethod.Post && (string?)c.Json![0]!["subname"] == "_acme-challenge" && (string?)c.Json[0]!["type"] == "TXT");
    }

    [Fact]
    public async Task AnExistingRrsetIsMergedSoTheWildcardAndTheBaseNameCoexist()
    {
        _desec.Seed("example.com", "_acme-challenge", 7200, "\"apex-value\"");

        await Provider().CreateTxtAsync(Challenge, "wildcard-value", Token);

        var (ttl, records) = _desec.Rrset("example.com", "_acme-challenge")!.Value;
        Assert.Equal(7200, ttl);
        Assert.Equal(["\"apex-value\"", "\"wildcard-value\""], records);
        Assert.Contains(_handlers.Calls, c => c.Method == HttpMethod.Patch);
        Assert.DoesNotContain(_handlers.Calls, c => c.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task ANonDefaultMinimumTtlOfTheDomainIsRespected()
    {
        _desec.AddDomain("fast.example", minimumTtl: 60);
        _desec.AddDomain("slow.example", minimumTtl: 86400);

        await Provider().CreateTxtAsync("_acme-challenge.fast.example", "v1", Token);
        await Provider().CreateTxtAsync("_acme-challenge.slow.example", "v1", Token);

        Assert.Equal(60, _desec.Rrset("fast.example", "_acme-challenge")!.Value.Ttl);
        Assert.Equal(86400, _desec.Rrset("slow.example", "_acme-challenge")!.Value.Ttl);
    }

    [Fact]
    public async Task CreateIsIdempotent()
    {
        var provider = Provider();
        await provider.CreateTxtAsync(Challenge, "v1", Token);
        var writes = _handlers.Calls.Count(c => c.Method != HttpMethod.Get);

        await provider.CreateTxtAsync(Challenge, "v1", Token);

        Assert.Equal(writes, _handlers.Calls.Count(c => c.Method != HttpMethod.Get));
        Assert.Single(_desec.Rrset("example.com", "_acme-challenge")!.Value.Records);
    }

    [Fact]
    public async Task DeleteRemovesOnlyTheNamedValueAndDeletesTheSetWhenItIsEmpty()
    {
        _desec.Seed("example.com", "_acme-challenge", 3600, "\"a\"", "\"b\"");
        var provider = Provider();

        await provider.DeleteTxtAsync(Challenge, "a", Token);
        Assert.Equal(["\"b\""], _desec.Rrset("example.com", "_acme-challenge")!.Value.Records);

        await provider.DeleteTxtAsync(Challenge, "b", Token);
        Assert.Null(_desec.Rrset("example.com", "_acme-challenge"));
        Assert.Contains(_handlers.Calls, c => c.Method == HttpMethod.Patch && c.Json![0]!["records"]!.AsArray().Count == 0);
    }

    [Fact]
    public async Task DeleteOfAValueOrASetThatIsNotThereWritesNothing()
    {
        _desec.Seed("example.com", "_acme-challenge", 3600, "\"a\"");
        var provider = Provider();

        await provider.DeleteTxtAsync(Challenge, "other", Token);
        await provider.DeleteTxtAsync("_acme-challenge.sub.example.com", "a", Token);

        Assert.All(_handlers.Calls, c => Assert.Equal(HttpMethod.Get, c.Method));
        Assert.Single(_desec.Rrset("example.com", "_acme-challenge")!.Value.Records);
    }

    [Fact]
    public async Task ValuesWithQuotesAndBackslashesAreEscapedAndMatchedUnescaped()
    {
        var provider = Provider();

        await provider.CreateTxtAsync(Challenge, "a\"b\\c", Token);
        Assert.Equal(["\"a\\\"b\\\\c\""], _desec.Rrset("example.com", "_acme-challenge")!.Value.Records);

        await provider.DeleteTxtAsync(Challenge, "a\"b\\c", Token);
        Assert.Null(_desec.Rrset("example.com", "_acme-challenge"));
    }

    [Fact]
    public async Task TheSubnameIsTheNameBelowTheDomain()
    {
        _desec.AddDomain("sub.example.com");

        await Provider().CreateTxtAsync("_acme-challenge.a.sub.example.com", "v1", Token);
        await Provider().CreateTxtAsync("_acme-challenge.x.example.com", "v2", Token);

        Assert.NotNull(_desec.Rrset("sub.example.com", "_acme-challenge.a"));
        Assert.NotNull(_desec.Rrset("example.com", "_acme-challenge.x"));
    }

    [Fact]
    public async Task ANameOutsideTheAccountsDomainsIsPermanent()
    {
        var ex = await Assert.ThrowsAsync<DnsProviderException>(async () =>
            await Provider().CreateTxtAsync("_acme-challenge.other.org", "v1", Token));

        Assert.False(ex.IsTransient);
        Assert.Contains("no domain", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThrottleWithALongRetryAfterIsTransientWithTheHint()
    {
        _desec.Intercept = _ => FakeDesec.Detail(HttpStatusCode.TooManyRequests, "Request was throttled. Expected available in 300 seconds.", ("Retry-After", "300"));

        var ex = await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().CreateTxtAsync(Challenge, "v1", Token));

        Assert.True(ex.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(300), ex.RetryAfter);
        Assert.Empty(_time.Delays);
    }

    [Fact]
    public async Task AThrottleWithAShortRetryAfterIsWaitedOutOnce()
    {
        var throttled = false;
        _desec.Intercept = call =>
        {
            if (throttled || call.Method != HttpMethod.Patch && call.Method != HttpMethod.Post)
            {
                return null;
            }

            throttled = true;
            return FakeDesec.Detail(HttpStatusCode.TooManyRequests, "Request was throttled. Expected available in 1 second.", ("Retry-After", "1"));
        };

        await Provider().CreateTxtAsync(Challenge, "v1", Token);

        // Retry-After is the floor; the jitter adds up to a quarter more.
        Assert.Contains(_time.Delays, delay => delay >= TimeSpan.FromSeconds(1) && delay < TimeSpan.FromSeconds(1.25));
        Assert.NotNull(_desec.Rrset("example.com", "_acme-challenge"));
    }

    [Fact]
    public async Task AThrottleThatPersistsAfterTheShortWaitIsTransient()
    {
        _desec.Intercept = _ => FakeDesec.Detail(HttpStatusCode.TooManyRequests, "throttled", ("Retry-After", "1"));

        var ex = await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().CreateTxtAsync(Challenge, "v1", Token));

        Assert.True(ex.IsTransient);
        Assert.Single(_time.Delays);
    }

    [Fact]
    public async Task AValidationErrorIsPermanentAndSaysWhichField()
    {
        _desec.Intercept = call => call.Method == HttpMethod.Post
            ? FakeSecretHandlers.Json(HttpStatusCode.BadRequest, """{"records":["Ensure this field has no more than 500 items."]}""")
            : null;

        var ex = await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().CreateTxtAsync(Challenge, "v1", Token));

        Assert.False(ex.IsTransient);
        Assert.Contains("records: Ensure this field", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerErrorIsTransient()
    {
        _desec.Intercept = _ => FakeSecretHandlers.Empty(HttpStatusCode.ServiceUnavailable);

        var ex = await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().CreateTxtAsync(Challenge, "v1", Token));

        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task ANetworkFailureIsTransientAndARefusalByTheVaultIsNot()
    {
        _handlers.TransportFailure = new HttpRequestException(HttpRequestError.NameResolutionError, "boom");
        Assert.True((await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().CreateTxtAsync(Challenge, "v1", Token))).IsTransient);

        _handlers.TransportFailure = new SecretTransportRefusedException("the secret does not exist or is not a DNS API token");
        Assert.False((await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().CreateTxtAsync(Challenge, "v1", Token))).IsTransient);
    }

    [Fact]
    public async Task ABadTokenIsAnInvalidResultAndTheTokenIsNeverInMessagesOrLogs()
    {
        _desec.Intercept = _ => FakeDesec.Detail(HttpStatusCode.Unauthorized, "Invalid token.");
        var provider = Provider();

        var check = await provider.ValidateCredentialsAsync(Token);
        var ex = await Assert.ThrowsAsync<DnsProviderException>(async () => await provider.CreateTxtAsync(Challenge, "v1", Token));

        Assert.False(check.Valid);
        Assert.Contains("Invalid token.", check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecretHandlers.Token, check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecretHandlers.Token, ex.ToString(), StringComparison.Ordinal);
        Assert.All(_logs.Entries, e => Assert.DoesNotContain(FakeSecretHandlers.Token, e, StringComparison.Ordinal));
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task AGoodTokenValidates()
    {
        var check = await Provider().ValidateCredentialsAsync(Token);

        Assert.True(check.Valid);
        Assert.Contains(_handlers.Calls, c => c.Path.EndsWith("/domains/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWholeOrdersRecordsAreWrittenInOneBulkCallPerDomain()
    {
        _desec.AddDomain("example.org");
        _desec.Seed("example.com", "_acme-challenge.old", 3600, "\"x\"");
        var provider = Provider();

        await provider.CreateTxtBatchAsync(
            [
                new DnsChallengeRecord(Challenge, "wildcard-value"),
                new DnsChallengeRecord(Challenge, "apex-value"),
                new DnsChallengeRecord("_acme-challenge.a.example.com", "a-value"),
                new DnsChallengeRecord("_acme-challenge.old.example.com", "old-value"),
                new DnsChallengeRecord("_acme-challenge.example.org", "org-value"),
            ],
            Token);

        Assert.Equal(["\"wildcard-value\"", "\"apex-value\""], _desec.Rrset("example.com", "_acme-challenge")!.Value.Records);
        Assert.Equal(["\"x\"", "\"old-value\""], _desec.Rrset("example.com", "_acme-challenge.old")!.Value.Records);
        Assert.NotNull(_desec.Rrset("example.org", "_acme-challenge"));
        // example.com: one POST for the two new sets and one PATCH for the old one; example.org: one POST. Three writes for five records.
        Assert.Equal(3, _desec.Writes);

        _desec.AddDomain("example.net");
        await provider.DeleteTxtBatchAsync(
            [new DnsChallengeRecord(Challenge, "wildcard-value"), new DnsChallengeRecord(Challenge, "apex-value"), new DnsChallengeRecord("_acme-challenge.a.example.com", "a-value")],
            Token);

        Assert.Null(_desec.Rrset("example.com", "_acme-challenge"));
        Assert.Null(_desec.Rrset("example.com", "_acme-challenge.a"));
        Assert.Equal(4, _desec.Writes);
    }

    [Fact]
    public async Task APutWithoutSubnameAndTypeIsRefusedByDesecSoTheProviderNeverSendsOne()
    {
        _desec.Seed("example.com", "_acme-challenge", 3600, "\"a\"");
        var strict = _desec;
        ApiCall bare = new(HttpMethod.Put, new Uri("https://desec.io/api/v1/domains/example.com/rrsets/_acme-challenge/TXT/"), """{"ttl":3600,"records":["\"b\""]}""", "Token " + FakeSecretHandlers.Token);
        Assert.Equal(HttpStatusCode.BadRequest, strict.Handle(bare).StatusCode);

        await Provider().CreateTxtAsync(Challenge, "b", Token);

        Assert.DoesNotContain(_handlers.Calls, c => c.Method == HttpMethod.Put);
        Assert.Equal(2, _desec.Rrset("example.com", "_acme-challenge")!.Value.Records.Count);
    }

    [Fact]
    public async Task ACreateThatMeetsASetMadeInTheMeantimeMergesIntoItOnce()
    {
        var raced = false;
        _desec.Intercept = call =>
        {
            if (raced || call.Method != HttpMethod.Post)
            {
                return null;
            }

            raced = true;
            _desec.Seed("example.com", "_acme-challenge", 3600, "\"other\"");
            return FakeSecretHandlers.Json(HttpStatusCode.BadRequest, """[{"non_field_errors":["Another RRset with the same subdomain and type exists for this domain."]}]""");
        };

        await Provider().CreateTxtAsync(Challenge, "v1", Token);

        Assert.Equal(["\"other\"", "\"v1\""], _desec.Rrset("example.com", "_acme-challenge")!.Value.Records);
    }

    [Fact]
    public async Task ABadRequestForASetThatStillDoesNotExistIsPermanent()
    {
        _desec.Intercept = call => call.Method == HttpMethod.Post ? FakeSecretHandlers.Json(HttpStatusCode.BadRequest, """[{"records":["Invalid."]}]""") : null;

        var ex = await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().CreateTxtAsync(Challenge, "v1", Token));

        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task ANameThatIsACnameIsRefusedBeforeAnythingIsWritten()
    {
        _desec.SeedCname("example.com", "_acme-challenge", "validation.other.org.");

        var ex = await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().CreateTxtAsync(Challenge, "v1", Token));

        Assert.False(ex.IsTransient);
        Assert.Contains("CNAME", ex.Message, StringComparison.Ordinal);
        Assert.Contains("publishes the record at the CNAME's target", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, _desec.Writes);
    }

    [Fact]
    public async Task AThrottleIsMarkedRateLimitedButAServerErrorIsNot()
    {
        _desec.Intercept = _ => FakeDesec.Detail(HttpStatusCode.TooManyRequests, "throttled", ("Retry-After", "3600"));
        Assert.True((await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().CreateTxtAsync(Challenge, "v1", Token))).IsRateLimited);

        _desec.Intercept = _ => FakeSecretHandlers.Empty(HttpStatusCode.ServiceUnavailable);
        Assert.False((await Assert.ThrowsAsync<DnsProviderException>(async () => await Provider().CreateTxtAsync(Challenge, "v1", Token))).IsRateLimited);
    }

    [Fact]
    public async Task AThrottleOfAMinuteIsWaitedOutInsideTheCallByDefault()
    {
        var throttled = false;
        _desec.Intercept = call =>
        {
            if (throttled || call.Method != HttpMethod.Post)
            {
                return null;
            }

            throttled = true;
            return FakeDesec.Detail(HttpStatusCode.TooManyRequests, "Request was throttled. Expected available in 60 seconds.", ("Retry-After", "60"));
        };

        await Provider().CreateTxtAsync(Challenge, "v1", Token);

        // Retry-After is the floor; the jitter adds up to a quarter more.
        Assert.Contains(_time.Delays, delay => delay >= TimeSpan.FromSeconds(60) && delay < TimeSpan.FromSeconds(75));
        Assert.NotNull(_desec.Rrset("example.com", "_acme-challenge"));
    }

    [Fact]
    public async Task ValidationReadsOneBoundedAnswer()
    {
        await Provider().ValidateCredentialsAsync(Token);

        var call = Assert.Single(_handlers.Calls);
        Assert.Equal("owns_qname", call.Query.TrimStart('?').Split('=')[0]);
    }
}
