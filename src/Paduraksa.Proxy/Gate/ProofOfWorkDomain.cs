namespace Paduraksa.Proxy.Gate;

/// <summary>
/// What tells one proof-of-work flow's challenges and tokens from another's: the MAC prefixes and the cookie's name. The browser
/// check and the trap each have one, over the same key, so a token solved for one verifies nowhere in the other (the tags differ),
/// and neither reads the other's cookie.
/// </summary>
internal sealed class ProofOfWorkDomain
{
    private ProofOfWorkDomain(string challengePrefix, string tokenPrefix, string cookieName)
    {
        ChallengePrefix = System.Text.Encoding.ASCII.GetBytes(challengePrefix);
        TokenPrefix = System.Text.Encoding.ASCII.GetBytes(tokenPrefix);
        CookieName = cookieName;
    }

    /// <summary>The browser check on a host's routes.</summary>
    public static ProofOfWorkDomain Gate { get; } = new("paduraksa-pow-challenge-v1", "paduraksa-pow-token-v1", "paduraksa_pow");

    /// <summary>
    /// The trap's. The cookie's name says nothing about the product or the check, since a crawler that is shown it reads it: it reads
    /// like a blog's.
    /// </summary>
    public static ProofOfWorkDomain Honeypot { get; } = new("paduraksa-honeypot-pow-v1/challenge", "paduraksa-honeypot-pow-v1/token", "reader_token");

    public ReadOnlySpan<byte> Challenge => ChallengePrefix;

    public ReadOnlySpan<byte> Token => TokenPrefix;

    public string CookieName { get; }

    private byte[] ChallengePrefix { get; }

    private byte[] TokenPrefix { get; }
}
