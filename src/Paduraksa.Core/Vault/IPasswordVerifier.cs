namespace Paduraksa.Core.Vault;

/// <summary>What a password check said.</summary>
public enum PasswordVerdict : byte
{
    /// <summary>The password is not the credential's (or there was no credential: an unknown user).</summary>
    Rejected,

    /// <summary>The password is the credential's.</summary>
    Verified,

    /// <summary>The credential has no password in the vault, so nothing could match. It cost as much as a rejection.</summary>
    NoPassword,
}

/// <summary>The answer of <see cref="IPasswordVerifier.TryVerifyCached"/>.</summary>
public readonly struct PasswordCacheProbe
{
    public PasswordCacheProbe(bool hit, long attemptKey)
    {
        Hit = hit;
        AttemptKey = attemptKey;
    }

    /// <summary>These exact credentials, from this client address, were verified recently: no PBKDF2 is needed.</summary>
    public bool Hit { get; }

    /// <summary>
    /// Names this attempt (this credential, this client address, these bytes) without revealing anything about them; 0 when the
    /// credential has no password. Equal keys are the same attempt, so the caller can queue identical ones behind each other
    /// and find the answer in the cache. Drawn from a per-process secret: nobody can compute or aim one.
    /// </summary>
    public long AttemptKey { get; }
}

/// <summary>
/// Checks a password against a credential the vault holds, and remembers the ones that passed. Split so the gate stays fast:
/// <see cref="TryVerifyCached"/> is the synchronous, allocation-free path every request takes, and <see cref="Verify"/> is
/// the slow one (PBKDF2, tens of milliseconds), which the caller schedules: how many run at once, in what order, for how long, is
/// the proxy's business. The cache lives here, so neither a hash nor a cache entry ever leaves the vault.
/// </summary>
public interface IPasswordVerifier
{
    /// <summary>
    /// Whether <paramref name="password"/> for <paramref name="username"/> from <paramref name="client"/> (the address as the
    /// caller masks it, 16 bytes) was verified in the last five minutes; a hit pushes the entry's expiry out again, so an active
    /// user stays verified. Allocation-free, lock-free and synchronous. A change of the credential's password, and a delete, make
    /// every earlier entry miss.
    /// </summary>
    PasswordCacheProbe TryVerifyCached(IPasswordCredential credential, ReadOnlySpan<byte> username, ReadOnlySpan<byte> password, ReadOnlySpan<byte> client);

    /// <summary>
    /// The PBKDF2 check. A null <paramref name="credential"/> (a name nobody has) and one with no password pay the same cost
    /// against a dummy verifier, so neither can be told from a wrong password by timing. With <paramref name="remember"/>, a
    /// success is put in the cache; a wrong password never is.
    /// </summary>
    PasswordVerdict Verify(IPasswordCredential? credential, ReadOnlySpan<byte> username, ReadOnlySpan<byte> password, ReadOnlySpan<byte> client, bool remember);
}
