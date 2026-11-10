using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Paduraksa.Core.Model;
using Paduraksa.Core.Text;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>
/// The token, challenge, solution and page primitives of the proof-of-work gate, without a request pipeline: the token's
/// layout and scope, every rejection reason, the challenge's tag and age, leading-zero-bit counting, return sanitising and
/// the page's CSP hashes.
/// </summary>
public sealed class ProofOfWorkTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly IpKey V4 = IpKey.Parse("192.0.2.1");
    private static readonly IpKey V4Neighbour = IpKey.Parse("192.0.2.2");
    private static readonly IpKey V6 = IpKey.Parse("2001:db8:1:2::1");
    private static readonly IpKey V6SameSlash64 = IpKey.Parse("2001:db8:1:2:ffff:1:2:3");
    private static readonly IpKey V6OtherSlash64 = IpKey.Parse("2001:db8:1:3::1");
    private static readonly byte[] Scope = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];
    private static readonly byte[] OtherScope = [.. Enumerable.Range(0, 16).Select(i => (byte)(i + 1))];

    private static TestMacKey Key => new();

    [Fact]
    public void ATokenRoundTripsWithItsExpiryAndDifficulty()
    {
        var expiresAt = (uint)(Now.ToUnixTimeSeconds() + 3600);
        var token = Encode(expiresAt, 18);

        Assert.Equal(ProofOfWorkToken.EncodedLength, token.Length);
        Assert.True(ProofOfWorkToken.TryValidate(token, Key, Scope, V4, Now.ToUnixTimeSeconds(), 16, out var expiry, out var difficulty, out var problem));
        Assert.Equal(ProofOfWorkTokenProblem.None, problem);
        Assert.Equal(expiresAt, expiry);
        Assert.Equal(18, difficulty);
    }

    [Fact]
    public void EveryTokenByteIsCoveredByTheTag()
    {
        var token = Encode((uint)(Now.ToUnixTimeSeconds() + 3600), 16);
        var bytes = Decode(token);

        for (var i = 0; i < bytes.Length; i++)
        {
            var tampered = (byte[])bytes.Clone();
            tampered[i] ^= 0x01;
            var value = Base64Url.EncodeToString(tampered);
            Assert.False(
                ProofOfWorkToken.TryValidate(value, Key, Scope, V4, Now.ToUnixTimeSeconds(), 16, out _, out _, out _),
                $"a token with byte {i} changed was accepted");
        }
    }

    [Fact]
    public void ExpiryIsCheckedBeforeTheDifficulty()
    {
        var now = Now.ToUnixTimeSeconds();
        Assert.False(ProofOfWorkToken.TryValidate(Encode((uint)now), Key, Scope, V4, now, 16, out _, out _, out var exact));
        Assert.Equal(ProofOfWorkTokenProblem.Expired, exact);
        Assert.False(ProofOfWorkToken.TryValidate(Encode((uint)now - 1), Key, Scope, V4, now, 16, out _, out _, out var past));
        Assert.Equal(ProofOfWorkTokenProblem.Expired, past);
        Assert.True(ProofOfWorkToken.TryValidate(Encode((uint)now + 1), Key, Scope, V4, now, 16, out _, out _, out _));
    }

    [Fact]
    public void AnIpv4TokenIsBoundToTheWholeAddress()
    {
        var token = Encode((uint)(Now.ToUnixTimeSeconds() + 60), 16);
        Assert.True(ProofOfWorkToken.TryValidate(token, Key, Scope, V4, Now.ToUnixTimeSeconds(), 16, out _, out _, out _));
        Assert.False(ProofOfWorkToken.TryValidate(token, Key, Scope, V4Neighbour, Now.ToUnixTimeSeconds(), 16, out _, out _, out var problem));
        Assert.Equal(ProofOfWorkTokenProblem.BadTag, problem);
    }

    [Fact]
    public void AnIpv6TokenIsBoundToItsSlash64()
    {
        var token = Encode((uint)(Now.ToUnixTimeSeconds() + 60), 16, client: V6);
        Assert.True(ProofOfWorkToken.TryValidate(token, Key, Scope, V6SameSlash64, Now.ToUnixTimeSeconds(), 16, out _, out _, out _));
        Assert.False(ProofOfWorkToken.TryValidate(token, Key, Scope, V6OtherSlash64, Now.ToUnixTimeSeconds(), 16, out _, out _, out var problem));
        Assert.Equal(ProofOfWorkTokenProblem.BadTag, problem);
    }

    [Fact]
    public void AHostScopeIsNotTransferable()
    {
        var token = Encode((uint)(Now.ToUnixTimeSeconds() + 60), 16);
        Assert.False(ProofOfWorkToken.TryValidate(token, Key, OtherScope, V4, Now.ToUnixTimeSeconds(), 16, out _, out _, out var problem));
        Assert.Equal(ProofOfWorkTokenProblem.BadTag, problem);
    }

    [Fact]
    public void RaisingTheDifficultyInvalidatesAnEasierTokenAndLoweringItDoesNot()
    {
        var easier = Encode((uint)(Now.ToUnixTimeSeconds() + 60), 12);
        Assert.False(ProofOfWorkToken.TryValidate(easier, Key, Scope, V4, Now.ToUnixTimeSeconds(), 16, out _, out _, out var problem));
        Assert.Equal(ProofOfWorkTokenProblem.TooEasy, problem);

        var harder = Encode((uint)(Now.ToUnixTimeSeconds() + 60), 20);
        Assert.True(ProofOfWorkToken.TryValidate(harder, Key, Scope, V4, Now.ToUnixTimeSeconds(), 16, out _, out var difficulty, out _));
        Assert.Equal(20, difficulty);
    }

    [Theory]
    [InlineData("a=1; paduraksa_pow={0}; b=2", true)]
    [InlineData("paduraksa_pow = {0}", true)]
    [InlineData("paduraksa_pow={0}\nfirst=1", true)]
    [InlineData("a=1", false)]
    [InlineData("pafuraksa_pow={0}", false)]
    public void TheCookieIsReadStraightFromTheHeader(string header, bool found)
    {
        var token = Encode((uint)(Now.ToUnixTimeSeconds() + 60), 16);
        var request = Request(string.Format(System.Globalization.CultureInfo.InvariantCulture, header, token));
        Span<char> value = stackalloc char[ProofOfWorkToken.MaxCookieLength + 1];
        var read = ProofOfWorkToken.TryReadCookie(request, value, out var length, out var problem);

        Assert.Equal(found, read);
        if (found)
        {
            Assert.Equal(ProofOfWorkTokenProblem.None, problem);
            Assert.Equal(token, value[..length].ToString());
        }
        else
        {
            Assert.Equal(ProofOfWorkTokenProblem.Missing, problem);
        }
    }

    [Fact]
    public void DuplicateAndUnreadableCookiesAreNotTrusted()
    {
        var token = Encode((uint)(Now.ToUnixTimeSeconds() + 60), 16);
        Span<char> value = stackalloc char[ProofOfWorkToken.MaxCookieLength + 1];
        var duplicate = Request($"paduraksa_pow={token}; paduraksa_pow={token}");
        Assert.False(ProofOfWorkToken.TryReadCookie(duplicate, value, out _, out var problem));
        Assert.Equal(ProofOfWorkTokenProblem.Duplicate, problem);

        var malformed = Request("paduraksa_pow=!!!!");
        Assert.True(ProofOfWorkToken.TryReadCookie(malformed, value, out var length, out _));
        Assert.False(ProofOfWorkToken.TryValidate(value[..length], Key, Scope, V4, Now.ToUnixTimeSeconds(), 16, out _, out _, out problem));
        Assert.Equal(ProofOfWorkTokenProblem.Malformed, problem);

        var longValue = Request($"paduraksa_pow={new string('a', 200)}");
        Assert.False(ProofOfWorkToken.TryReadCookie(longValue, value, out _, out problem));
        Assert.Equal(ProofOfWorkTokenProblem.Malformed, problem);
    }

    [Fact]
    public void AFullLengthForgeryIsMalformed_NeverAnException()
    {
        // Base64Url.TryDecodeFromChars throws on a character outside the alphabet and on nonzero spare bits in the last
        // character; a forged cookie or challenge with either was a 500 (the verify endpoint's, in production) instead of
        // a refusal. Every last character, and one that isn't base64url at all, is tried at the full length.
        var token = Encode((uint)(Now.ToUnixTimeSeconds() + 60), 16);
        var challenge = Challenge((uint)Now.ToUnixTimeSeconds(), 16);
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_!=.+/";
        foreach (var last in Alphabet)
        {
            var forgedToken = token[..^1] + last;
            var tokenValid = ProofOfWorkToken.TryValidate(forgedToken, Key, Scope, V4, Now.ToUnixTimeSeconds(), 16, out _, out _, out var tokenProblem);
            Assert.True(tokenValid == (forgedToken == token), $"token ending in '{last}'");
            Assert.True(tokenValid == (tokenProblem == ProofOfWorkTokenProblem.None), $"token ending in '{last}': {tokenProblem}");

            var forgedChallenge = challenge[..^1] + last;
            var challengeValid = ProofOfWorkChallenge.TryValidate(forgedChallenge, Key, Scope, V4, Now.ToUnixTimeSeconds(), out _, out _, out var challengeProblem);
            Assert.True(challengeValid == (forgedChallenge == challenge), $"challenge ending in '{last}'");
            Assert.True(challengeValid == (challengeProblem == ProofOfWorkChallengeProblem.None), $"challenge ending in '{last}': {challengeProblem}");
        }

        Assert.False(ProofOfWorkToken.TryValidate(token[..10] + "!" + token[11..], Key, Scope, V4, Now.ToUnixTimeSeconds(), 16, out _, out _, out var middle));
        Assert.Equal(ProofOfWorkTokenProblem.Malformed, middle);
        Assert.False(ProofOfWorkChallenge.TryValidate(challenge[..10] + "!" + challenge[11..], Key, Scope, V4, Now.ToUnixTimeSeconds(), out _, out _, out var challengeMiddle));
        Assert.Equal(ProofOfWorkChallengeProblem.Malformed, challengeMiddle);
    }

    [Fact]
    public void AChallengeRoundTripsAndItsTagAgeAndScopeAreChecked()
    {
        var issuedAt = (uint)Now.ToUnixTimeSeconds();
        var challenge = Challenge(issuedAt, 16);

        Assert.Equal(ProofOfWorkChallenge.EncodedLength, challenge.Length);
        Assert.True(ProofOfWorkChallenge.TryValidate(challenge, Key, Scope, V4, Now.ToUnixTimeSeconds(), out var issued, out var difficulty, out var problem));
        Assert.Equal((issuedAt, 16, ProofOfWorkChallengeProblem.None), (issued, difficulty, problem));

        // The tag covers every byte.
        var bytes = Decode(challenge);
        bytes[9] ^= 0x01;
        Assert.False(ProofOfWorkChallenge.TryValidate(Base64Url.EncodeToString(bytes), Key, Scope, V4, Now.ToUnixTimeSeconds(), out _, out _, out var tampered));
        Assert.Equal(ProofOfWorkChallengeProblem.BadTag, tampered);

        Assert.False(ProofOfWorkChallenge.TryValidate(challenge, Key, OtherScope, V4, Now.ToUnixTimeSeconds(), out _, out _, out var scope));
        Assert.Equal(ProofOfWorkChallengeProblem.BadTag, scope);
        Assert.False(ProofOfWorkChallenge.TryValidate(challenge, Key, Scope, V4Neighbour, Now.ToUnixTimeSeconds(), out _, out _, out var client));
        Assert.Equal(ProofOfWorkChallengeProblem.BadTag, client);

        // Valid for ten minutes, then not; a timestamp in the future is not either.
        Assert.True(ProofOfWorkChallenge.TryValidate(challenge, Key, Scope, V4, Now.ToUnixTimeSeconds() + ProofOfWorkChallenge.LifetimeSeconds, out _, out _, out _));
        Assert.False(ProofOfWorkChallenge.TryValidate(challenge, Key, Scope, V4, Now.ToUnixTimeSeconds() + ProofOfWorkChallenge.LifetimeSeconds + 1, out _, out _, out var old));
        Assert.Equal(ProofOfWorkChallengeProblem.Expired, old);
        Assert.False(ProofOfWorkChallenge.TryValidate(challenge, Key, Scope, V4, Now.ToUnixTimeSeconds() - 1, out _, out _, out var future));
        Assert.Equal(ProofOfWorkChallengeProblem.Expired, future);
    }

    [Fact]
    public void TheSolutionIsSearchedByTheSameRuleThePageUses()
    {
        var challenge = Challenge((uint)Now.ToUnixTimeSeconds(), 10);
        var solution = Solve(challenge, 10);

        Assert.True(ProofOfWorkChallenge.TryVerifySolution(challenge, solution, 10));
        Assert.False(ProofOfWorkChallenge.TryVerifySolution(challenge, "x", 10));
        Assert.False(ProofOfWorkChallenge.TryVerifySolution(challenge, string.Empty, 10));

        // The counter is hashed exactly as posted: a leading zero is another input, and the answer says what that input gives.
        Assert.Equal(
            LeadingZeroBits(challenge, "0" + solution) >= 10,
            ProofOfWorkChallenge.TryVerifySolution(challenge, "0" + solution, 10));
    }

    [Fact]
    public void LeadingZeroBitsAreCountedAcrossTheDigest()
    {
        Assert.Equal(0, ProofOfWorkChallenge.CountLeadingZeroBits([0x80]));
        Assert.Equal(4, ProofOfWorkChallenge.CountLeadingZeroBits([0x0F]));
        Assert.Equal(8, ProofOfWorkChallenge.CountLeadingZeroBits([0x00, 0x80]));
        Assert.Equal(12, ProofOfWorkChallenge.CountLeadingZeroBits([0x00, 0x0F]));
        Assert.Equal(256, ProofOfWorkChallenge.CountLeadingZeroBits(new byte[32]));
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/a/b?c=1", "/a/b?c=1")]
    [InlineData("", "/")]
    [InlineData("relative", "/")]
    [InlineData("//evil.test/x", "/")]
    [InlineData("/\\evil.test/x", "/")]
    [InlineData("/a b", "/")]
    [InlineData("/a\nb", "/")]
    [InlineData("/a\u00e9", "/")]
    public void TheReturnTargetMustBeALocalVisiblePath(string value, string expected) =>
        Assert.Equal(expected, ProofOfWorkEndpoint.SanitizeReturn(value));

    [Theory]
    [InlineData(true, "paduraksa_pow=abc; Path=/; HttpOnly; SameSite=Lax; Max-Age=3600; Secure")]
    [InlineData(false, "paduraksa_pow=abc; Path=/; HttpOnly; SameSite=Lax; Max-Age=3600")]
    public void TheCookieIsSecureExactlyWhenTheTrustedSchemeIsHttps(bool secure, string expected) =>
        Assert.Equal(expected, ProofOfWorkEndpoint.Cookie("abc", 3600, secure));

    [Fact]
    public void ThePageIsAsciiAndItsCspHashesMatchItsInlineScriptAndStyle()
    {
        Assert.All(ProofOfWorkPage.ScriptText, c => Assert.InRange(c, '\0', '\x7F'));
        Assert.All(ProofOfWorkPage.StyleText, c => Assert.InRange(c, '\0', '\x7F'));
        Assert.DoesNotContain("eval(", ProofOfWorkPage.ScriptText, StringComparison.Ordinal);
        Assert.DoesNotContain("crypto.subtle", ProofOfWorkPage.ScriptText, StringComparison.Ordinal);

        var csp = ProofOfWorkPage.ContentSecurityPolicy;
        Assert.Contains($"script-src '{Hash(ProofOfWorkPage.ScriptText)}'", csp, StringComparison.Ordinal);
        Assert.Contains($"style-src '{Hash(ProofOfWorkPage.StyleText)}'", csp, StringComparison.Ordinal);
        Assert.Contains("worker-src blob:", csp, StringComparison.Ordinal);
        Assert.Contains("form-action 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("base-uri 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);

        var response = new ProofOfWorkPage(10).Build(Challenge((uint)Now.ToUnixTimeSeconds(), 10), "/a?b=1&c=2");
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.ContentType.ToString());
        Assert.Equal("no-store", response.CacheControl.ToString());
        Assert.Contains("name=\"challenge\"", Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
        Assert.Contains("value=\"/a?b=1&amp;c=2\"", Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
        Assert.All(response.Headers, header => Assert.False(string.IsNullOrEmpty(header.Name)));
        Assert.All(response.Body, b => Assert.InRange(b, 0, 0x7F));
    }

    [Fact]
    public void AProtectedRouteChallengesABrowserAndAnswersOtherRequestsForbidden()
    {
        var runtime = Runtime();
        var policy = new ProofOfWorkPolicy(Spec(10), Scope, routeOverride: false, runtime);

        var browser = View("GET", "text/html");
        var decision = policy.Evaluate(browser);
        Assert.Equal(PolicyAction.Respond, decision.Action);
        Assert.Equal("text/html; charset=utf-8", decision.Response!.ContentType.ToString());
        Assert.Equal(1, runtime.Stats.ChallengesServed);

        var api = View("POST", "application/json");
        decision = policy.Evaluate(api);
        Assert.Equal(PolicyAction.Respond, decision.Action);
        Assert.Equal(403, decision.Response!.StatusCode);
        Assert.Equal(1, runtime.Stats.BlockedNonBrowser);

        // A GET that doesn't accept HTML is not a browser either.
        decision = policy.Evaluate(View("GET", "application/json"));
        Assert.Equal(403, decision.Response!.StatusCode);
    }

    [Fact]
    public void RobotsAndTheVerifyEndpointAreExempt()
    {
        var runtime = Runtime();
        var policy = new ProofOfWorkPolicy(Spec(10), Scope, routeOverride: false, runtime);

        Assert.Equal(PolicyAction.Continue, policy.Evaluate(View("GET", "text/html", ProofOfWorkTargets.RobotsPath)).Action);
        Assert.Equal(PolicyAction.Continue, policy.Evaluate(View("HEAD", "*/*", ProofOfWorkTargets.RobotsPath)).Action);
        Assert.Equal(PolicyAction.Continue, policy.Evaluate(View("POST", "text/plain", ProofOfWorkTargets.VerifyPath)).Action);

        var request = View("GET", "text/html", ProofOfWorkTargets.RobotsPath);
        Assert.Contains("robots.txt", Explain(policy, request, PolicyDecision.Continue), StringComparison.Ordinal);
    }

    [Fact]
    public void AValidTokenLetsTheRequestThroughAtZeroAllocation()
    {
        var runtime = Runtime();
        var token = Encode((uint)(Now.ToUnixTimeSeconds() + 3600), 16);
        var policy = new ProofOfWorkPolicy(Spec(10), Scope, routeOverride: false, runtime);
        var request = View("GET", "text/html", cookie: token);

        Assert.Equal(PolicyAction.Continue, policy.Evaluate(request).Action);
        PolicyState state = new(policy, request.Http, request.State);
        AllocAssert.Zero(state, static s => EvaluateOnce(s), "proof-of-work token validation");
        Assert.Contains("valid token", Explain(policy, request, PolicyDecision.Continue), StringComparison.Ordinal);
    }

    [Fact]
    public void AKeylessRouteFailsClosed()
    {
        var runtime = new ProofOfWorkRuntime();
        var policy = new ProofOfWorkPolicy(Spec(10), Scope, routeOverride: false, runtime);

        var decision = policy.Evaluate(View("GET", "text/html"));

        Assert.Equal(PolicyAction.Respond, decision.Action);
        Assert.Equal(503, decision.Response!.StatusCode);
        Assert.Contains("503", Explain(policy, View("GET", "text/html"), decision), StringComparison.Ordinal);
    }

    [Fact]
    public void ARejectedTokenSaysWhy()
    {
        var runtime = Runtime();
        var policy = new ProofOfWorkPolicy(Spec(16), Scope, routeOverride: true, runtime);

        var expired = View("GET", "text/html", cookie: Encode((uint)Now.ToUnixTimeSeconds() - 1, 16));
        var decision = policy.Evaluate(expired);
        Assert.Equal(200, decision.Response!.StatusCode);
        Assert.Contains("expired", Explain(policy, expired, decision), StringComparison.Ordinal);
        Assert.Equal(1, runtime.Stats.Expired);

        var tooEasy = View("GET", "text/html", cookie: Encode((uint)(Now.ToUnixTimeSeconds() + 60), 10));
        decision = policy.Evaluate(tooEasy);
        Assert.Contains("difficulty", Explain(policy, tooEasy, decision), StringComparison.Ordinal);
        Assert.Equal(1, runtime.Stats.TooEasy);

        var otherHost = View("GET", "text/html", cookie: Encode((uint)(Now.ToUnixTimeSeconds() + 60), 16, scope: OtherScope));
        decision = policy.Evaluate(otherHost);
        Assert.Contains("other host or IP", Explain(policy, otherHost, decision), StringComparison.Ordinal);
        Assert.Equal(1, runtime.Stats.BadTag);

        // The route's own override is named in the trace.
        Assert.Contains("route's own override", Explain(policy, otherHost, decision), StringComparison.Ordinal);
    }

    [Fact]
    public void TheGatesValidTokenPathAllocatesNothing()
    {
        var runtime = Runtime();
        HostId hostId = HostId.New();
        ProxyConfigModel model = new()
        {
            Upstreams = [new UpstreamSpec { Id = UpstreamId.New(), Destinations = [new DestinationSpec(DestinationId.New(), "http://127.0.0.1:1")] }],
            Hosts =
            [
                new HostSpec
                {
                    Id = hostId,
                    Names = ["pow.test"],
                    Https = false,
                    ProofOfWork = Spec(10),
                    Routes =
                    [
                        new RouteSpec
                        {
                            Id = RouteId.New(),
                            Upstream = UpstreamId.New(),
                            ProofOfWork = Spec(10),
                        },
                    ],
                },
            ],
        };
        // Point the route at the upstream the model carries.
        model = model with { Hosts = [model.Hosts[0] with { Routes = [model.Hosts[0].Routes[0] with { Upstream = model.Upstreams[0].Id }] }] };
        var snapshot = TestSnapshots.Bound(model, options: new SnapshotCompilerOptions { ProofOfWork = runtime });
        using SnapshotStore store = new();
        store.Publish(snapshot);
        GateMiddleware gate = new(store, new TrustedProxyClientAddressSource(), challenges: null, runtime);

        var client = IpKey.Parse("10.0.0.5");
        var expiry = (uint)(Now.ToUnixTimeSeconds() + 3600);
        Span<char> token = stackalloc char[ProofOfWorkToken.EncodedLength];
        Assert.True(ProofOfWorkToken.TryEncode(runtime.Key!, snapshot.HostsByIndex[0].ProofOfWorkHostScope, client, expiry, 10, token, out var written));

        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
        context.Request.Method = "GET";
        context.Request.Path = "/";
        context.Request.Host = new HostString("pow.test");
        context.Request.Headers.Accept = "text/html";
        context.Request.Headers.Cookie = $"{ProofOfWorkToken.CookieName}={token[..written]}";
        context.Features.Set<IPersistentStateFeature>(new PersistentState());

        Assert.Equal(GateAction.Forward, gate.Decide(context).Action);
        AllocAssert.Zero((gate, context), static state => state.gate.Decide(state.context), "GateMiddleware.Decide with a valid proof-of-work token");
    }

    private sealed class PersistentState : IPersistentStateFeature
    {
        public IDictionary<object, object?> State { get; } = new Dictionary<object, object?>();
    }

    private static ProofOfWorkRuntime Runtime()
    {
        var runtime = new ProofOfWorkRuntime(new FixedTime(Now));
        runtime.UseKey(new TestMacKey());
        return runtime;
    }

    private static HostProofOfWorkSpec Spec(int difficulty) =>
        new() { Enabled = true, Difficulty = difficulty, TokenLifetimeHours = 168 };

    private static string Encode(uint expiresAt, int difficulty = 16, byte[]? scope = null, IpKey? client = null)
    {
        Span<char> token = stackalloc char[ProofOfWorkToken.EncodedLength];
        Assert.True(ProofOfWorkToken.TryEncode(Key, scope ?? Scope, client ?? V4, expiresAt, difficulty, token, out var written));
        return token[..written].ToString();
    }

    private static string Explain(ProofOfWorkPolicy policy, in RequestView request, PolicyDecision decision) =>
        ((IRequestPolicy)policy).Explain(request, decision);

    private static string Challenge(uint issuedAt, int difficulty, byte[]? scope = null, IpKey? client = null)
    {
        Span<byte> nonce = stackalloc byte[ProofOfWorkChallenge.NonceLength];
        for (var i = 0; i < nonce.Length; i++)
        {
            nonce[i] = (byte)(i + 1);
        }

        Span<char> challenge = stackalloc char[ProofOfWorkChallenge.EncodedLength];
        Assert.True(ProofOfWorkChallenge.TryCreate(Key, scope ?? Scope, client ?? V4, issuedAt, difficulty, nonce, challenge, out var written));
        return challenge[..written].ToString();
    }

    private static string Solve(string challenge, int difficulty)
    {
        for (var n = 0; ; n++)
        {
            var text = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (LeadingZeroBits(challenge, text) >= difficulty)
            {
                return text;
            }
        }
    }

    private static int LeadingZeroBits(string challenge, string solution)
    {
        var bytes = Encoding.ASCII.GetBytes(string.Concat(challenge, ":", solution));
        Span<byte> hash = stackalloc byte[32];
        _ = SHA256.HashData(bytes, hash);
        return ProofOfWorkChallenge.CountLeadingZeroBits(hash);
    }

    private static byte[] Decode(string value)
    {
        var bytes = new byte[value.Length * 3 / 4];
        Assert.True(Base64Url.TryDecodeFromChars(value, bytes, out var written));
        return bytes[..written];
    }

    [Fact]
    public async Task TheChallengePageIsWrittenFromTheTemplateForABrowser()
    {
        var challenge = Challenge((uint)Now.ToUnixTimeSeconds(), 10);
        var response = new ProofOfWorkPage(10).Build(challenge, "/a?b=1&c=2");
        var context = TestContexts.Create("app.test");
        context.Request.Headers.Accept = "text/html,application/xhtml+xml";
        var body = new MemoryStream();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>(
            new Microsoft.AspNetCore.Http.StreamResponseBodyFeature(body));

        await ResponseWriter.WriteAsync(context, response);
        await context.Response.BodyWriter.FlushAsync(TestContext.Current.CancellationToken);

        var page = Encoding.UTF8.GetString(body.ToArray());
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", context.Response.ContentType);
        Assert.Contains("name=\"challenge\"", page, StringComparison.Ordinal);
        Assert.Contains($"value=\"{challenge}\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"return\"", page, StringComparison.Ordinal);
        Assert.Contains("value=\"/a?b=1&amp;c=2\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"difficulty\" type=\"hidden\" value=\"10\"", page, StringComparison.Ordinal);
        Assert.Contains($"script-src '{Hash(ProofOfWorkPage.ScriptText)}'", context.Response.Headers["Content-Security-Policy"].ToString(), StringComparison.Ordinal);
    }

    private static string Hash(string content) => "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static HttpRequest Request(string cookie)
    {
        DefaultHttpContext context = new();
        if (cookie.Length > 0)
        {
            context.Request.Headers.Cookie = new Microsoft.Extensions.Primitives.StringValues(cookie.Split('\n'));
        }

        return context.Request;
    }

    private static void EvaluateOnce(PolicyState state) => state.Policy.Evaluate(new RequestView(state.Context, state.State));

    private sealed record PolicyState(ProofOfWorkPolicy Policy, HttpContext Context, GateRequestState State);

    private static RequestView View(string method, string accept, string path = "/", string? cookie = null)
    {
        DefaultHttpContext context = new();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers.Accept = accept;
        if (cookie is not null)
        {
            context.Request.Headers.Cookie = ProofOfWorkToken.CookieName + "=" + cookie;
        }

        GateRequestState state = new()
        {
            ClientIp = new ClientIpResolution(V4, ClientIpSource.Socket, ClientIpOutcome.PeerNotTrusted),
        };
        return new RequestView(context, state);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
