using System.Buffers.Text;

using Paduraksa.Proxy.Gate;
using Paduraksa.ProxyProtocol;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>
/// The trap's proof of work over the browser check's code: the same key signs both, so what keeps a token solved in the trap out of
/// the real site (and the other way round) is the domain each flow signs under, and the name of the cookie it reads.
/// </summary>
public sealed class HoneypotProofOfWorkTests
{
    private static readonly long Now = 1_800_000_000;
    private static readonly IpKey Client = IpKey.Parse("192.0.2.1");
    private static readonly byte[] Scope = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

    private static TestMacKey Key => new();

    [Fact]
    public void ATrapTokenIsNotAGateTokenAndTheOtherWayRound()
    {
        var trap = EncodeToken(ProofOfWorkDomain.Honeypot);
        var gate = EncodeToken(ProofOfWorkDomain.Gate);

        Assert.True(ProofOfWorkToken.TryValidate(ProofOfWorkDomain.Honeypot, trap, Key, Scope, Client, Now, 16, out _, out _, out _));
        Assert.True(ProofOfWorkToken.TryValidate(ProofOfWorkDomain.Gate, gate, Key, Scope, Client, Now, 16, out _, out _, out _));

        Assert.False(ProofOfWorkToken.TryValidate(ProofOfWorkDomain.Gate, trap, Key, Scope, Client, Now, 16, out _, out _, out var gateProblem));
        Assert.Equal(ProofOfWorkTokenProblem.BadTag, gateProblem);
        Assert.False(ProofOfWorkToken.TryValidate(ProofOfWorkDomain.Honeypot, gate, Key, Scope, Client, Now, 16, out _, out _, out var trapProblem));
        Assert.Equal(ProofOfWorkTokenProblem.BadTag, trapProblem);

        // The two overloads that predate the domain are the browser check's.
        Assert.True(ProofOfWorkToken.TryValidate(gate, Key, Scope, Client, Now, 16, out _, out _, out _));
        Assert.False(ProofOfWorkToken.TryValidate(trap, Key, Scope, Client, Now, 16, out _, out _, out _));
    }

    [Fact]
    public void ATrapChallengeIsNotAGateChallengeAndTheOtherWayRound()
    {
        var trap = Challenge(ProofOfWorkDomain.Honeypot);
        var gate = Challenge(ProofOfWorkDomain.Gate);

        Assert.True(ProofOfWorkChallenge.TryValidate(ProofOfWorkDomain.Honeypot, trap, Key, Scope, Client, Now, out _, out _, out _));
        Assert.True(ProofOfWorkChallenge.TryValidate(ProofOfWorkDomain.Gate, gate, Key, Scope, Client, Now, out _, out _, out _));
        Assert.False(ProofOfWorkChallenge.TryValidate(ProofOfWorkDomain.Gate, trap, Key, Scope, Client, Now, out _, out _, out var gateProblem));
        Assert.Equal(ProofOfWorkChallengeProblem.BadTag, gateProblem);
        Assert.False(ProofOfWorkChallenge.TryValidate(ProofOfWorkDomain.Honeypot, gate, Key, Scope, Client, Now, out _, out _, out var trapProblem));
        Assert.Equal(ProofOfWorkChallengeProblem.BadTag, trapProblem);
    }

    [Fact]
    public void ATokenIsNotAChallengeInEitherFlow()
    {
        foreach (var domain in new[] { ProofOfWorkDomain.Gate, ProofOfWorkDomain.Honeypot })
        {
            Assert.False(ProofOfWorkChallenge.TryValidate(domain, EncodeToken(domain), Key, Scope, Client, Now, out _, out _, out _));
            Assert.False(ProofOfWorkToken.TryValidate(domain, Challenge(domain), Key, Scope, Client, Now, 16, out _, out _, out _));
        }
    }

    [Fact]
    public void EachFlowReadsOnlyItsOwnCookie()
    {
        var request = TestContexts.Create("trap.test").Request;
        request.Headers.Cookie = "paduraksa_pow=gate; reader_token=trap";
        Span<char> value = stackalloc char[ProofOfWorkToken.MaxCookieLength + 1];

        Assert.True(ProofOfWorkToken.TryReadCookie(request, value, out var gateLength, out _));
        Assert.Equal("gate", value[..gateLength].ToString());
        Assert.True(ProofOfWorkToken.TryReadCookie(request, ProofOfWorkDomain.Honeypot.CookieName, value, out var trapLength, out _));
        Assert.Equal("trap", value[..trapLength].ToString());
    }

    [Fact]
    public void TheTrapsCookieNamesNeitherTheProductNorTheCheck()
    {
        var name = ProofOfWorkDomain.Honeypot.CookieName;

        Assert.DoesNotContain("pow", name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("paduraksa", name, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(ProofOfWorkDomain.Gate.CookieName, name);
    }

    [Fact]
    public void TheCookieCarriesThePathItIsScopedTo()
    {
        var cookie = ProofOfWorkEndpoint.Cookie("reader_token", "abc", 86_400, secure: true, "/archive-k7f3/");

        Assert.Equal("reader_token=abc; Path=/archive-k7f3/; HttpOnly; SameSite=Lax; Max-Age=86400; Secure", cookie);
        Assert.Equal("paduraksa_pow=abc; Path=/; HttpOnly; SameSite=Lax; Max-Age=60", ProofOfWorkEndpoint.Cookie("abc", 60, secure: false));
    }

    [Theory]
    [InlineData("/archive-k7f3/post-1/?a=b", "/archive-k7f3/", "/archive-k7f3/post-1/?a=b")]
    [InlineData("/elsewhere/", "/archive-k7f3/", "/archive-k7f3/")]
    [InlineData("//evil.example/", "/archive-k7f3/", "/archive-k7f3/")]
    [InlineData("https://evil.example/", "/archive-k7f3/", "/archive-k7f3/")]
    [InlineData("/elsewhere/", null, "/elsewhere/")]
    public void ASolverIsSentBackIntoTheTrapNeverOutOfIt(string target, string? prefix, string expected)
    {
        Assert.Equal(expected, ProofOfWorkEndpoint.SanitizeReturn(target, prefix));
    }

    private static string EncodeToken(ProofOfWorkDomain domain)
    {
        Span<char> token = stackalloc char[ProofOfWorkToken.EncodedLength];
        Assert.True(ProofOfWorkToken.TryEncode(domain, Key, Scope, Client, (uint)(Now + 3600), 16, token, out var length));
        return token[..length].ToString();
    }

    private static string Challenge(ProofOfWorkDomain domain)
    {
        Span<char> challenge = stackalloc char[ProofOfWorkChallenge.EncodedLength];
        Span<byte> nonce = stackalloc byte[ProofOfWorkChallenge.NonceLength];
        nonce.Fill(7);
        Assert.True(ProofOfWorkChallenge.TryCreate(domain, Key, Scope, Client, (uint)Now, 16, nonce, challenge, out var length));
        return challenge[..length].ToString();
    }
}
