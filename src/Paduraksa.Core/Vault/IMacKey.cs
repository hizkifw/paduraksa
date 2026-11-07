namespace Paduraksa.Core.Vault;

/// <summary>
/// The HMAC variants a <see cref="IMacKey"/> can compute. Persisted by stable name (<see cref="VaultNames"/>), never by
/// number. A MAC secret's algorithm is fixed when it is stored (<see cref="SecretOptions.MacAlgorithm"/>).
/// </summary>
public enum MacAlgorithm
{
    /// <summary>HMAC-SHA-256, 32-byte tag. JWS <c>HS256</c> (ACME external account binding), TSIG <c>hmac-sha256</c>.</summary>
    HmacSha256 = 0,

    /// <summary>HMAC-SHA-384, 48-byte tag.</summary>
    HmacSha384 = 1,

    /// <summary>HMAC-SHA-512, 64-byte tag.</summary>
    HmacSha512 = 2,
}

/// <summary>
/// A keyed MAC whose key stays in the vault (PLAN §8.2): callers hand data in and get a tag, or hand a tag in and
/// get a yes or no. Used for ACME external account binding (HS256), RFC 2136 TSIG, and later forward-auth and PoW tokens.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rotation.</b> A handle is a reference to the secret, not to one version of its value. It is cheap, thread-safe and
/// owned by the vault: do not dispose it, and hold it as long as is convenient. Every call resolves the <i>current</i>
/// version of the secret, so a rotation (<see cref="ISecretStore.ReplaceAsync"/>) takes effect without reopening. <see cref="Sign"/>
/// always uses the current version. <see cref="Verify"/> accepts a tag made under the current version <i>or</i> the
/// previous one, for a grace window after a replacement, so tokens issued just before a rotation (PoW, forward-auth) do
/// not all fail at once. The window's length is vault policy, not a caller option; after it only the current version
/// verifies. If the secret has been deleted, both methods throw <see cref="InvalidOperationException"/>.
/// </para>
/// <para><see cref="Sign"/> and <see cref="Verify"/> are synchronous and allocation-free, so they are usable on a request path.</para>
/// </remarks>
public interface IMacKey
{
    /// <summary>The algorithm this handle computes, fixed by the secret's metadata.</summary>
    MacAlgorithm Algorithm { get; }

    /// <summary>The tag length in bytes for <see cref="Algorithm"/>.</summary>
    int TagLength { get; }

    /// <summary>Computes the tag over <paramref name="data"/> into <paramref name="tag"/> (at least <see cref="TagLength"/> bytes) and returns the number of bytes written.</summary>
    /// <exception cref="ArgumentException"><paramref name="tag"/> is shorter than <see cref="TagLength"/>.</exception>
    int Sign(ReadOnlySpan<byte> data, Span<byte> tag);

    /// <summary>Checks <paramref name="tag"/> against the tag of <paramref name="data"/> in constant time (current version, or the previous one within the grace window).</summary>
    bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> tag);
}
