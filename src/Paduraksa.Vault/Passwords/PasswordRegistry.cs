using System.Collections.Concurrent;
using System.Security.Cryptography;

using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Vault.Passwords;

/// <summary>
/// The credentials' handles and the verifier (<see cref="IPasswordVerifier"/>) over them, in memory: the parsed verifiers and the
/// cache of recent successes. <c>PasswordCredentialStore</c> keeps them in <c>vault.db</c> and loads them into one of these.
/// </summary>
/// <remarks>
/// <para><b>Handles.</b> A slot per credential ID is created the first time it is opened and never removed, so a handle held by
/// a compiled snapshot keeps seeing the credential through a change of password, a delete and a later set. A slot is a few words;
/// IDs come from config documents and from sets, so there are as many as the documents ever named.</para>
/// <para><b>The cache key.</b> SHA-256 over a per-process random key, the credential's stamp (it changes with the salt, so a
/// password change, a delete or a re-import makes every entry of the old verifier miss at once, with nothing to sweep), the masked
/// client address and the user name and password as sent; the first 8 bytes. The user name is in it because a credential can
/// be renamed without changing its password. A lookup builds it on the stack: 0 B. A reader takes no lock: it reads the slot's
/// current state once.</para>
/// </remarks>
internal sealed class PasswordRegistry : IPasswordVerifier
{
    private const int MaxUsernameBytes = 64;

    private readonly ConcurrentDictionary<PasswordCredentialId, PasswordCredentialSlot> _slots = new();
    private readonly PasswordVerificationCache _cache;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly TimeProvider _time;
    private readonly long _start;
    private readonly ParsedPasswordHash _dummy = ParseDummy();

    public PasswordRegistry(TimeProvider time, int cacheCapacity = PasswordVerificationCache.DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _start = time.GetTimestamp();
        _cache = new PasswordVerificationCache(cacheCapacity);
    }

    private long NowMs => (long)_time.GetElapsedTime(_start).TotalMilliseconds;

    public PasswordCredentialSlot Open(PasswordCredentialId id)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A credential ID can't be empty.", nameof(id));
        }

        return _slots.GetOrAdd(id, static key => new PasswordCredentialSlot(key));
    }

    public void Put(PasswordCredentialId id, ParsedPasswordHash state) => Open(id).State = state;

    public void Clear(PasswordCredentialId id)
    {
        if (_slots.TryGetValue(id, out var slot))
        {
            slot.State = null;
        }
    }

    /// <summary>How many cache entries are live (a scan: for tests and diagnostics).</summary>
    public int CachedCount() => _cache.LiveCount(NowMs);

    /// <summary>The verifier a credential holds now, null when it holds none.</summary>
    public ParsedPasswordHash? Current(PasswordCredentialId id) => _slots.TryGetValue(id, out var slot) ? slot.State : null;

    public bool IsSet(PasswordCredentialId id) => _slots.TryGetValue(id, out var slot) && slot.State is not null;

    public IReadOnlyList<PasswordCredentialId> SetIds() =>
        [.. _slots.Values.Where(static s => s.State is not null).Select(static s => s.Id).Order()];

    public PasswordCacheProbe TryVerifyCached(IPasswordCredential credential, ReadOnlySpan<byte> username, ReadOnlySpan<byte> password, ReadOnlySpan<byte> client)
    {
        if (StateOf(credential) is not { } state || !TryFingerprint(state, client, username, password, out var key))
        {
            return default;
        }

        return new PasswordCacheProbe(_cache.TryHit(key, NowMs), (long)key);
    }

    public PasswordVerdict Verify(IPasswordCredential? credential, ReadOnlySpan<byte> username, ReadOnlySpan<byte> password, ReadOnlySpan<byte> client, bool remember)
    {
        var state = credential is null ? null : StateOf(credential);
        if (state is null)
        {
            // Nobody's, or no password: the same PBKDF2 a wrong password costs.
            PasswordHash.Verify(password, _dummy);
            return credential is null ? PasswordVerdict.Rejected : PasswordVerdict.NoPassword;
        }

        if (!PasswordHash.Verify(password, state))
        {
            return PasswordVerdict.Rejected;
        }

        if (remember && TryFingerprint(state, client, username, password, out var key))
        {
            _cache.Add(key, NowMs);
        }

        return PasswordVerdict.Verified;
    }

    private static ParsedPasswordHash? StateOf(IPasswordCredential credential) =>
        credential is PasswordCredentialSlot slot ? slot.State : throw new ArgumentException("The credential isn't one this vault opened.", nameof(credential));

    // Null when the credentials are longer than anything the gate reads: such an attempt is just never cached.
    private bool TryFingerprint(ParsedPasswordHash state, ReadOnlySpan<byte> client, ReadOnlySpan<byte> username, ReadOnlySpan<byte> password, out ulong key)
    {
        key = 0;
        if (username.Length > MaxUsernameBytes || password.Length > AccessListLimits.MaxPasswordBytes || client.Length > 16)
        {
            return false;
        }

        Span<byte> input = stackalloc byte[32 + 16 + 16 + 1 + MaxUsernameBytes + AccessListLimits.MaxPasswordBytes];
        _key.CopyTo(input);
        state.Stamp.CopyTo(input[32..]);
        input.Slice(48, 16).Clear();
        client.CopyTo(input[48..]);
        input[64] = (byte)username.Length;
        username.CopyTo(input[65..]);
        password.CopyTo(input[(65 + username.Length)..]);
        var length = 65 + username.Length + password.Length;
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input[..length], digest);
        CryptographicOperations.ZeroMemory(input);
        key = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(digest);
        if (key == 0)
        {
            key = 1;
        }

        return true;
    }

    private static ParsedPasswordHash ParseDummy() =>
        PasswordHash.TryParse(PasswordHash.Create(Convert.ToHexString(RandomNumberGenerator.GetBytes(8))), out var parsed)
            ? parsed
            : throw new InvalidOperationException("The dummy password hash didn't parse.");
}
